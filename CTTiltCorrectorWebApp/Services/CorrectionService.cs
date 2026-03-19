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
        IOptions<DicomConfig> dicomCfg,
        IOptions<AppConfig> appCfg,
        ILogger<CorrectionService> logger)
    {
        _dicomQuery = dicomQuery;
        _store      = store;
        _corrector  = corrector;
        _dbFactory  = dbFactory;
        _dicomCfg   = dicomCfg.Value;
        _appCfg     = appCfg.Value;
        _logger     = logger;
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

        var combined = Combine(progress, logWriter);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var run = new CorrectionRun
        {
            PatientId         = job.PatientId,
            SeriesInstanceUid = job.SeriesInstanceUid,
            ExecutionDate     = DateTime.UtcNow,
            UserName          = job.UserName,
            LogFilePath       = logPath,
            Status            = "Running"
        };
        db.CorrectionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── 2. Request ARIA to push the series to our SCP ─────────────────
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
        int lastCount  = -1;
        var stableFor  = TimeSpan.Zero;

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

    /// <summary>
    /// Opens a single DICOM association to ARIA and C-STOREs every corrected
    /// dataset. All data lives in memory — no temp files created.
    /// </summary>
    private async Task SendToAriaAsync(
        List<DicomDataset> datasets,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var client = DicomClientFactory.Create(
            _dicomCfg.RemoteHost,
            _dicomCfg.RemotePort,
            useTls: false,
            callingAe: _dicomCfg.LocalAeTitle,
            calledAe: _dicomCfg.RemoteAeTitle);

        client.ServiceOptions.RequestTimeout =
            TimeSpan.FromSeconds(_dicomCfg.ConnectionTimeoutSeconds);

        int sent = 0;
        int total = datasets.Count;

        foreach (var dataset in datasets)
        {
            ct.ThrowIfCancellationRequested();

            // Wrap the in-memory dataset in a DicomFile so C-STORE can send it
            var dicomFile = new DicomFile(dataset);
            var request   = new DicomCStoreRequest(dicomFile);

            int capturedIndex = ++sent; // capture for lambda
            request.OnResponseReceived += (_, response) =>
            {
                if (response.Status != DicomStatus.Success)
                    _logger.LogWarning(
                        "C-STORE response non-success for slice {N}/{T}: {Status}",
                        capturedIndex, total, response.Status);
            };

            await client.AddRequestAsync(request);

            if (sent % 20 == 0 || sent == total)
                progress.Report($"  Queued {sent}/{total} slices for upload…");
        }

        await client.SendAsync(ct);
        progress.Report($"  C-STORE SCU association closed — {total} slices sent.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private (string path, StreamWriter writer) CreateLogWriter(CorrectionJob job)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var folder    = Path.Combine(_appCfg.LogRootPath, $"{timestamp}_{job.PatientId}");
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
