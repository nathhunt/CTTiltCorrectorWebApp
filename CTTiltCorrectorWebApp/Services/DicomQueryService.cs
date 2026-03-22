using CTTiltCorrector.Infrastructure;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;

namespace CTTiltCorrector.Services;

// ─── DTO returned to the UI ───────────────────────────────────────────────────

public record DicomSeriesResult(
    string PatientId,
    string PatientName,
    string PatientDOB,
    string StudyInstanceUid,
    string SeriesInstanceUid,
    string SeriesDescription,
    string Modality,
    string SeriesDate,
    int NumberOfImages);

// ─── Service ─────────────────────────────────────────────────────────────────

/// <summary>
/// Provides C-FIND (query) and C-MOVE (retrieve) SCU operations against ARIA.
/// </summary>
public class DicomQueryService
{
    private readonly DicomConfig _cfg;
    private readonly ILogger<DicomQueryService> _logger;

    public DicomQueryService(IOptions<DicomConfig> cfg, ILogger<DicomQueryService> logger)
    {
        _cfg = cfg.Value;
        _logger = logger;
    }

    // ─── C-FIND ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries ARIA at the Series level for all CT series belonging to a patient.
    /// </summary>
    public async Task<IReadOnlyList<DicomSeriesResult>> FindSeriesAsync(
        string patientId,
        CancellationToken ct = default)
    {
        var results = new List<DicomSeriesResult>();

        var request = BuildSeriesFindRequest(patientId);
        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.Dataset is not null)
                results.Add(MapDataset(response.Dataset));
        };

        var client = BuildClient();
        await client.AddRequestAsync(request);

        try
        {
            await client.SendAsync(ct);
            _logger.LogInformation(
                "C-FIND for PatientID={Id} returned {Count} series.", patientId, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C-FIND failed for PatientID={Id}.", patientId);
            throw;
        }

        return results;
    }

    // ─── C-MOVE ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Requests ARIA to push the specified series to our local C-STORE SCP.
    /// Returns when the C-MOVE operation completes (all images sent to our SCP).
    /// </summary>
    public async Task MoveSeriesAsync(
        string studyInstanceUid,
        string seriesInstanceUid,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var request = new DicomCMoveRequest(
            _cfg.MoveDestinationAeTitle,
            studyInstanceUid,
            seriesInstanceUid);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                var remaining = response.Remaining;
                var moved    = response.Completed;
                progress?.Report($"C-MOVE in progress — {moved} images received, {remaining} remaining…");
            }
            else if (response.Status == DicomStatus.Success)
            {
                tcs.TrySetResult(true);
                progress?.Report(
                    $"C-MOVE complete — {response.Completed} images, {response.Warnings} warnings, {response.Failures} failures.");
            }
            else
            {
                var msg = $"C-MOVE ended with status: {response.Status}";
                _logger.LogWarning(msg);
                tcs.TrySetException(new InvalidOperationException(msg));
            }
        };

        var client = BuildClient();
        await client.AddRequestAsync(request);

        try
        {
            await client.SendAsync(ct);
            await tcs.Task.WaitAsync(
                TimeSpan.FromSeconds(_cfg.ConnectionTimeoutSeconds * 10), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C-MOVE failed for Series={Uid}.", seriesInstanceUid);
            throw;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private IDicomClient BuildClient()
    {
        var client = DicomClientFactory.Create(
            _cfg.RemoteHost,
            _cfg.RemotePort,
            false,
            _cfg.LocalAeTitle,
            _cfg.RemoteAeTitle);

        client.ServiceOptions.RequestTimeout =
            TimeSpan.FromSeconds(_cfg.ConnectionTimeoutSeconds);

        return client;
    }

    private static DicomCFindRequest BuildSeriesFindRequest(string patientId)
    {
        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
        var ds = request.Dataset;

        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.PatientName, string.Empty);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);
        ds.AddOrUpdate(DicomTag.SeriesDescription, string.Empty);
        ds.AddOrUpdate(DicomTag.Modality, "CT");          // filter for CT only
        ds.AddOrUpdate(DicomTag.SeriesDate, string.Empty);
        ds.AddOrUpdate(DicomTag.NumberOfSeriesRelatedInstances, string.Empty);

        return request;
    }

    private static DicomSeriesResult MapDataset(DicomDataset ds) => new(
        PatientId:        ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
        PatientName:      ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
        PatientDOB:       ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
        StudyInstanceUid: ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
        SeriesInstanceUid:ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
        SeriesDescription:ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
        Modality:         ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
        SeriesDate:       ds.GetSingleValueOrDefault(DicomTag.SeriesDate, string.Empty),
        NumberOfImages:   ds.GetSingleValueOrDefault(DicomTag.NumberOfSeriesRelatedInstances, 0));
}
