using CTTiltCorrector.Data;
using CTTiltCorrector.Infrastructure;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CTTiltCorrector.Services;

// ─── Job descriptor ───────────────────────────────────────────────────────────

public record CorrectionJob(
    string PatientId,
    string StudyInstanceUid,
    string SeriesInstanceUid,
    string UserName);

// ─── Interface for the tilt corrector (plug in your implementation) ───────────

/// <summary>
/// Implement this interface in your own class and register it in Program.cs.
/// The framework calls it with all slices in memory, sorted by Instance Number.
/// The returned list is sent directly to ARIA — no disk I/O.
///
/// Suggested registration in Program.cs:
///   builder.Services.AddScoped&lt;ITiltCorrector, YourTiltCorrector&gt;();
/// </summary>
public interface ITiltCorrector
{
    /// <summary>
    /// Receives a complete, sorted, in-memory CT series and returns the
    /// corrected series ready for transmission back to ARIA.
    /// </summary>
    /// <param name="slices">
    ///   All <see cref="DicomDataset"/> objects for the series,
    ///   sorted ascending by Instance Number.
    /// </param>
    /// <param name="progress">
    ///   Optional sink for streaming status messages to the Monitor UI.
    ///   Report as frequently as useful (e.g. per-slice, per-phase).
    /// </param>
    /// <param name="ct">Cancellation token — honour it inside long loops.</param>
    /// <returns>
    ///   The corrected datasets. UIDs, pixel data, and tags are entirely
    ///   your responsibility — the framework just sends whatever you return.
    /// </returns>
    Task<List<DicomDataset>> CorrectAsync(
        List<DicomDataset> slices,
        IProgress<string> progress,
        CancellationToken ct);
}

// ─── CorrectionService ────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates the full in-memory pipeline:
///   C-MOVE  →  InMemoryDicomStore  →  ITiltCorrector  →  C-STORE SCU  →  ARIA
/// </summary>
public class CorrectionService
{
    private readonly DicomQueryService _dicomQuery;
    private readonly InMemoryDicomStore _store;
    private readonly ITiltCorrector _corrector;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MonitorState _monitorState;
    private readonly DicomConfig _dicomCfg;
    private readonly AppConfig _appCfg;
    private readonly ILogger<CorrectionService> _logger;

    // Polling: how long to wait for the slice count to stop growing
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public CorrectionService(
        DicomQueryService dicomQuery,
        InMemoryDicomStore store,
        ITiltCorrector corrector,
        IDbContextFactory<AppDbContext> dbFactory,
        MonitorState monitorState,
        IOptions<DicomConfig> dicomCfg,
        IOptions<AppConfig> appCfg,
        ILogger<CorrectionService> logger)
    {
        _dicomQuery = dicomQuery;
        _store = store;
        _corrector = corrector;
        _dbFactory = dbFactory;
        _monitorState = monitorState;
        _dicomCfg = dicomCfg.Value;
        _appCfg = appCfg.Value;
        _logger = logger;
    }

    // ─── Main pipeline ────────────────────────────────────────────────────────

    public async Task RunAsync(
        CorrectionJob job,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // ── 1. Create log file + DB record ────────────────────────────────────
        var (logPath, logWriter) = CreateLogWriter(job);
        await using var _ = logWriter;

        // Notify this user's Monitor channel that a job is starting
        _monitorState.SetJobStarted(
            job.UserName,
            $"{job.PatientId} — {job.SeriesInstanceUid[^Math.Min(20, job.SeriesInstanceUid.Length)..]}");

        // Route progress to both the user's Monitor channel and the log file
        var userProgress = _monitorState.CreateProgressReporter(job.UserName);
        var combined = Combine(userProgress, logWriter);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var run = new CorrectionRun
        {
            PatientId = job.PatientId,
            SeriesInstanceUid = job.SeriesInstanceUid,
            ExecutionDate = DateTime.UtcNow,
            UserName = job.UserName,
            LogFilePath = logPath,
            Status = "Running"
        };
        db.CorrectionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── 2. Register series as expected, then request ARIA to push ─────
            _store.Expect(job.SeriesInstanceUid);
            combined.Report("⬇  Requesting series from ARIA via C-MOVE…");
            await _dicomQuery.MoveSeriesAsync(
                job.StudyInstanceUid,
                job.SeriesInstanceUid,
                progress: combined,
                ct: ct);

            // ── 3. Wait until all slices have arrived in memory ───────────────
            combined.Report("⏳  Waiting for all slices to arrive…");
            await WaitForStableDeliveryAsync(job.SeriesInstanceUid, combined, ct);

            // ── 4. Drain from the in-memory store (sorted by Instance Number) ─
            var slices = _store.Drain(job.SeriesInstanceUid);
            combined.Report($"✅  {slices.Count} slices received into memory.");

            if (slices.Count == 0)
                throw new InvalidOperationException(
                    "No DICOM slices were received. Verify ARIA C-MOVE config.");

            // ── 5. Call your tilt correction function ─────────────────────────
            combined.Report("🔧  Calling tilt correction algorithm…");
            List<DicomDataset> corrected = await _corrector.CorrectAsync(slices, combined, ct);
            combined.Report($"✅  Correction complete — {corrected.Count} output slices.");

            // Release the source slices; corrected set is all we need now
            slices.Clear();

            // ── 6. Send corrected datasets back to ARIA via C-STORE SCU ───────
            combined.Report($"⬆️  Sending {corrected.Count} corrected slices to ARIA…");
            await SendToAriaAsync(corrected, combined, ct);
            combined.Report("✅  Upload to ARIA complete.");

            run.Status = "Completed";
            combined.Report("🏁  Job completed successfully.");
        }
        catch (OperationCanceledException)
        {
            run.Status = "Cancelled";
            combined.Report("⚠️  Job cancelled.");
            _store.Discard(job.SeriesInstanceUid);
            _logger.LogWarning("Job cancelled: {Series}", job.SeriesInstanceUid);
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            combined.Report($"❌  Job failed: {ex.Message}");
            _store.Discard(job.SeriesInstanceUid);
            _logger.LogError(ex, "Job failed: {Series}", job.SeriesInstanceUid);
            throw;
        }
        finally
        {
            db.CorrectionRuns.Update(run);
            await db.SaveChangesAsync(CancellationToken.None);
            await logWriter.FlushAsync(CancellationToken.None);
            _monitorState.SetJobFinished(job.UserName);
        }
    }

    // ─── Step 3: wait for stable slice count ─────────────────────────────────

    /// <summary>
    /// Polls <see cref="InMemoryDicomStore"/> until the slice count has not
    /// changed for <see cref="StableWindow"/>. This signals that ARIA has
    /// finished pushing all images.
    ///
    /// For a more deterministic approach, compare against the
    /// NumberOfSeriesRelatedInstances value obtained during C-FIND.
    /// </summary>
    private async Task WaitForStableDeliveryAsync(
        string seriesUid,
        IProgress<string> progress,
        CancellationToken ct)
    {
        int lastCount = -1;
        var stableFor = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            int current = _store.Count(seriesUid);

            if (current == lastCount && current > 0)
            {
                stableFor += PollInterval;
                if (stableFor >= StableWindow)
                    return;  // count stable — delivery complete
            }
            else
            {
                stableFor = TimeSpan.Zero;
                lastCount = current;
                if (current > 0)
                    progress.Report($"  {current} slices in memory…");
            }

            await Task.Delay(PollInterval, ct);
        }

        ct.ThrowIfCancellationRequested();
    }

    // ─── Step 6: send corrected datasets to ARIA ──────────────────────────────

    private const int MaxUploadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Opens a single DICOM association to ARIA and C-STOREs every corrected
    /// dataset. Retries the entire association up to <see cref="MaxUploadAttempts"/>
    /// times on failure. Throws if all attempts are exhausted or if any individual
    /// slice response is non-Success, which causes the job to be marked Failed.
    /// </summary>
    private async Task SendToAriaAsync(
        List<DicomDataset> datasets,
        IProgress<string> progress,
        CancellationToken ct)
    {
        int total = datasets.Count;

        for (int attempt = 1; attempt <= MaxUploadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                progress.Report($"  ⏳ Retry {attempt}/{MaxUploadAttempts} in {RetryDelay.TotalSeconds}s…");
                await Task.Delay(RetryDelay, ct);
            }

            var failures = new List<(int Index, DicomStatus Status)>();
            int sent = 0;

            try
            {
                var client = DicomClientFactory.Create(
                    _dicomCfg.RemoteHost,
                    _dicomCfg.RemotePort,
                    useTls: false,
                    callingAe: _dicomCfg.LocalAeTitle,
                    calledAe: _dicomCfg.RemoteAeTitle);

                client.ServiceOptions.RequestTimeout =
                    TimeSpan.FromSeconds(_dicomCfg.ConnectionTimeoutSeconds);

                foreach (var dataset in datasets)
                {
                    ct.ThrowIfCancellationRequested();

                    var dicomFile = new DicomFile(dataset);
                    var request = new DicomCStoreRequest(dicomFile);
                    int capturedIndex = ++sent;

                    request.OnResponseReceived += (_, response) =>
                    {
                        if (response.Status != DicomStatus.Success)
                        {
                            failures.Add((capturedIndex, response.Status));
                            _logger.LogWarning(
                                "C-STORE non-success slice {N}/{T}: {Status}",
                                capturedIndex, total, response.Status);
                        }
                    };

                    await client.AddRequestAsync(request);

                    if (sent % 20 == 0 || sent == total)
                        progress.Report($"  Queued {sent}/{total} slices for upload…");
                }

                await client.SendAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"  ❌ Upload attempt {attempt}/{MaxUploadAttempts} failed: {ex.Message}";
                progress.Report(msg);
                _logger.LogError(ex, "Upload attempt {Attempt} failed.", attempt);

                if (attempt == MaxUploadAttempts)
                    throw new InvalidOperationException(
                        $"Upload to ARIA failed after {MaxUploadAttempts} attempts. Last error: {ex.Message}", ex);

                continue; // retry
            }

            // Association succeeded — check for per-slice failures
            if (failures.Count > 0)
            {
                var detail = string.Join(", ", failures.Select(f => $"slice {f.Index}: {f.Status}"));
                var msg = $"  ❌ {failures.Count}/{total} slices rejected by ARIA: {detail}";
                progress.Report(msg);

                if (attempt == MaxUploadAttempts)
                    throw new InvalidOperationException(
                        $"{failures.Count}/{total} slices failed after {MaxUploadAttempts} attempts. {detail}");

                continue; // retry
            }

            // All slices confirmed Success
            progress.Report($"  ✅ Upload complete — {total}/{total} slices accepted by ARIA.");
            return;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private (string path, StreamWriter writer) CreateLogWriter(CorrectionJob job)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(_appCfg.LogRootPath, $"{timestamp}_{job.PatientId}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "run.log");
        return (path, new StreamWriter(path, append: false));
    }

    /// <summary>
    /// Wraps two progress sinks (UI + log file) into one combined reporter.
    /// </summary>
    private static IProgress<string> Combine(IProgress<string> ui, StreamWriter log)
        => new Progress<string>(msg =>
        {
            var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}";
            ui.Report(line);
            log.WriteLine(line);
            log.Flush();
        });
}
