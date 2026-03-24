using System.Threading.Channels;

namespace CTTiltCorrector.Services;

// ─── Queue ────────────────────────────────────────────────────────────────────

/// <summary>
/// Bounded single-writer, single-reader channel that serialises all
/// tilt-correction jobs. Even if multiple users submit jobs concurrently,
/// the processor always handles one at a time.
/// </summary>
public class CorrectionJobQueue
{
    // Capacity = 64; writers block when full (backpressure).
    private readonly Channel<QueuedJob> _channel =
        Channel.CreateBounded<QueuedJob>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode     = BoundedChannelFullMode.Wait
        });

    // Tracks series UIDs currently queued or being processed
    private readonly HashSet<string> _activeSeries = new();
    private readonly object _lock = new();

    public ChannelReader<QueuedJob> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a job. Awaits if the channel is full.
    /// Returns the <see cref="IProgress{T}"/> sink the UI should subscribe to.
    /// </summary>
    public async Task<bool> EnqueueAsync(
        CorrectionJob job,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_activeSeries.Contains(job.SeriesInstanceUid))
                return false;

            _activeSeries.Add(job.SeriesInstanceUid);
        }

        var queued = new QueuedJob(job, ct);
        await _channel.Writer.WriteAsync(queued, ct);
        return true;
    }
}

// ─── Job wrapper ─────────────────────────────────────────────────────────────

public record QueuedJob(
    CorrectionJob Job,
    CancellationToken CancellationToken);

// ─── Processor HostedService ─────────────────────────────────────────────────

/// <summary>
/// Long-running background service that drains <see cref="CorrectionJobQueue"/>
/// one job at a time, invoking <see cref="CorrectionService"/> for each.
/// </summary>
public class CorrectionJobProcessor : BackgroundService
{
    private readonly CorrectionJobQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<CorrectionJobProcessor> _logger;
    private readonly MonitorState _monitorState;

    public CorrectionJobProcessor(
        CorrectionJobQueue queue,
        IServiceProvider services,
        ILogger<CorrectionJobProcessor> logger,
        MonitorState monitorState)
    {
        _queue    = queue;
        _services = services;
        _logger   = logger;
        _monitorState = monitorState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CorrectionJobProcessor started.");

        await foreach (var queued in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation(
                "Processing job: Patient={Patient} Series={Series} User={User}",
                queued.Job.PatientId, queued.Job.SeriesInstanceUid, queued.Job.UserName);

            // CorrectionService is Scoped — create a new scope per job.
            await using var scope = _services.CreateAsyncScope();
            var correctionService = scope.ServiceProvider.GetRequiredService<CorrectionService>();

            try
            {
                await correctionService.RunAsync(
                    queued.Job,
                    queued.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Job cancelled: {Series}", queued.Job.SeriesInstanceUid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed: {Series}", queued.Job.SeriesInstanceUid);
                // Route the unhandled error to the submitting user's Monitor channel
                _monitorState.GetChannel(queued.Job.UserName)
                    .CreateProgressReporter()
                    .Report($"❌ Unhandled error: {ex.Message}");
            }
            finally
            {
                _monitorState.SetJobFinished(queued.Job.UserName);
            }
        }

        _logger.LogInformation("CorrectionJobProcessor stopped.");
    }
}
