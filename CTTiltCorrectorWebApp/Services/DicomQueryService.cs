using CTTiltCorrector.Infrastructure;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;

namespace CTTiltCorrector.Services;

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public record DicomPatientResult(
    string PatientId,
    string PatientName,
    string PatientDob);

public record DicomSeriesResult(
    string PatientId,
    string PatientName,
    string PatientDob,
    string StudyInstanceUid,
    string SeriesInstanceUid,
    string SeriesDescription,
    string Modality,
    string SeriesDate,
    int NumberOfImages);

// ─── Service ─────────────────────────────────────────────────────────────────

public class DicomQueryService
{
    private readonly DicomConfig _cfg;
    private readonly ILogger<DicomQueryService> _logger;

    // Minimum number of images for a series to be considered a real CT acquisition.
    // RT dose grids, structure sets, and DRRs are almost always < 10 instances.
    // Scouts/localizers are typically 1-3. Real CT series are 20+ at minimum.
    private const int MinDiagnosticImageCount = 10;

    public DicomQueryService(IOptions<DicomConfig> cfg, ILogger<DicomQueryService> logger)
    {
        _cfg = cfg.Value;
        _logger = logger;
    }

    // ─── SOP Class exclusions ─────────────────────────────────────────────────

    /// <summary>
    /// SOP Class UIDs that are never diagnostic CT image series.
    /// Covers RT objects, dose reports, secondary captures, and presentation states
    /// that ARIA may tag with CT modality.
    /// </summary>
    private static readonly HashSet<string> ExcludedSopClasses = new()
    {
        // RT Storage
        "1.2.840.10008.5.1.4.1.1.481.1",    // RT Image Storage (DRR)
        "1.2.840.10008.5.1.4.1.1.481.2",    // RT Dose Storage
        "1.2.840.10008.5.1.4.1.1.481.3",    // RT Structure Set Storage
        "1.2.840.10008.5.1.4.1.1.481.4",    // RT Beams Treatment Record
        "1.2.840.10008.5.1.4.1.1.481.5",    // RT Plan Storage
        "1.2.840.10008.5.1.4.1.1.481.6",    // RT Brachy Treatment Record
        "1.2.840.10008.5.1.4.1.1.481.7",    // RT Treatment Summary Record
        "1.2.840.10008.5.1.4.1.1.481.8",    // RT Ion Plan Storage
        "1.2.840.10008.5.1.4.1.1.481.9",    // RT Ion Beams Treatment Record
        "1.2.840.10008.5.1.4.34.7",         // RT Beams Delivery Instruction
        // Dose / SR
        "1.2.840.10008.5.1.4.1.1.88.67",    // X-Ray Radiation Dose SR
        "1.2.840.10008.5.1.4.1.1.88.68",    // Radiopharmaceutical Radiation Dose SR
        "1.2.840.10008.5.1.4.1.1.88.69",    // CT Radiation Dose SR
        "1.2.840.10008.5.1.4.1.1.88.71",    // Radiotherapy Radiation Dose SR
        // Secondary captures
        "1.2.840.10008.5.1.4.1.1.7",        // Secondary Capture Image Storage
        "1.2.840.10008.5.1.4.1.1.7.1",
        "1.2.840.10008.5.1.4.1.1.7.2",
        "1.2.840.10008.5.1.4.1.1.7.3",
        "1.2.840.10008.5.1.4.1.1.7.4",
        // Presentation states
        "1.2.840.10008.5.1.4.1.1.11.1",     // Grayscale Softcopy Presentation State
        "1.2.840.10008.5.1.4.1.1.11.2",     // Color Softcopy Presentation State
    };

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Full query pipeline for an ARIA oncology system:
    ///   1. Study-level C-FIND  → patient demographics + study UIDs
    ///   2. Series-level C-FIND per study → candidate series
    ///   3. CT modality filter  (client-side)
    ///   4. SOP class / description filter (client-side, catches RT objects)
    ///   5. Image-level C-FIND per remaining series → actual instance count
    ///   6. Minimum image count filter → eliminates scouts, DRRs, dose grids
    /// </summary>
    public async Task<(DicomPatientResult? Patient, IReadOnlyList<DicomSeriesResult> Series)>
        FindAsync(string patientId, CancellationToken ct = default)
    {
        // Step 1 — Study-level C-FIND (reliable source of patient demographics)
        var studies = await FindStudiesAsync(patientId, ct);

        if (studies.Count == 0)
            return (null, Array.Empty<DicomSeriesResult>());

        var patient = new DicomPatientResult(
            PatientId: studies[0].PatientId,
            PatientName: studies[0].PatientName,
            PatientDob: studies[0].PatientDob);

        // Step 2 — Series-level C-FIND per study
        var allSeries = new List<DicomSeriesResult>();
        foreach (var study in studies)
        {
            ct.ThrowIfCancellationRequested();
            var series = await FindSeriesInStudyAsync(study, ct);
            allSeries.AddRange(series);
        }

        _logger.LogInformation("Raw series: {Count}", allSeries.Count);

        // Step 3 — CT modality filter
        var ctSeries = allSeries
            .Where(s => string.Equals(s.Modality, "CT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("After CT modality filter: {Count}", ctSeries.Count);

        // Step 4 — SOP class / description pre-filter
        // Eliminates RT objects and known non-diagnostic series before we
        // issue image-level queries — reduces unnecessary network calls
        var candidates = ctSeries.Where(IsLikelyDiagnosticCt).ToList();

        _logger.LogInformation("After pre-filter: {Count} candidates", candidates.Count);

        // Step 5 — Image-level C-FIND per remaining series → actual instance count
        // and description fallback from ProtocolName / ImageComments
        var verified = new List<DicomSeriesResult>();
        foreach (var series in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var (actualCount, descFallback) = await QuerySeriesImagesAsync(series, ct);

            if (actualCount < MinDiagnosticImageCount)
            {
                _logger.LogDebug(
                    "Excluded series {Uid} — only {Count} images", series.SeriesInstanceUid, actualCount);
                continue;
            }

            // Resolve description: series level → image level fallback → exclude
            var description = !string.IsNullOrEmpty(series.SeriesDescription)
                ? series.SeriesDescription
                : descFallback;

            if (string.IsNullOrEmpty(description))
            {
                _logger.LogDebug(
                    "Excluded series {Uid} — no description at any level", series.SeriesInstanceUid);
                continue;
            }

            verified.Add(series with
            {
                NumberOfImages = actualCount,
                SeriesDescription = description
            });
        }

        _logger.LogInformation(
            "Final result: {Count} diagnostic CT series (excluded {Excl})",
            verified.Count, candidates.Count - verified.Count);

        // Merge patient demographics
        verified = verified
            .Select(s => s with
            {
                PatientName = string.IsNullOrEmpty(s.PatientName) ? patient.PatientName : s.PatientName,
                PatientDob = string.IsNullOrEmpty(s.PatientDob) ? patient.PatientDob : s.PatientDob
            })
            .ToList();

        return (patient, verified);
    }

    // ─── C-MOVE ───────────────────────────────────────────────────────────────

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
                progress?.Report(
                    $"C-MOVE in progress — {response.Completed} received, {response.Remaining} remaining…");
            }
            else if (response.Status == DicomStatus.Success)
            {
                tcs.TrySetResult(true);
                progress?.Report(
                    $"C-MOVE complete — {response.Completed} images, " +
                    $"{response.Warnings} warnings, {response.Failures} failures.");
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

    // ─── Private query methods ────────────────────────────────────────────────

    private record StudyResult(
        string PatientId,
        string PatientName,
        string PatientDob,
        string StudyInstanceUid,
        string StudyDate);

    private async Task<List<StudyResult>> FindStudiesAsync(
        string patientId, CancellationToken ct)
    {
        var results = new List<StudyResult>();

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        var ds = request.Dataset;
        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.PatientName, string.Empty);
        ds.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
        ds.AddOrUpdate(DicomTag.StudyDate, string.Empty);

        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.Dataset is not null)
            {
                results.Add(new StudyResult(
                    PatientId: response.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, patientId),
                    PatientName: response.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
                    PatientDob: response.Dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
                    StudyInstanceUid: response.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
                    StudyDate: response.Dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty)));
            }
        };

        var client = BuildClient();
        await client.AddRequestAsync(request);

        try
        {
            await client.SendAsync(ct);
            _logger.LogInformation(
                "Study C-FIND for PatientID={Id}: {Count} studies.", patientId, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Study C-FIND failed for PatientID={Id}.", patientId);
            throw;
        }

        return results;
    }

    private async Task<List<DicomSeriesResult>> FindSeriesInStudyAsync(
        StudyResult study, CancellationToken ct)
    {
        var results = new List<DicomSeriesResult>();

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);
        var ds = request.Dataset;
        ds.AddOrUpdate(DicomTag.PatientID, study.PatientId);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, study.StudyInstanceUid);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);
        ds.AddOrUpdate(DicomTag.SeriesDescription, string.Empty);
        ds.AddOrUpdate(DicomTag.Modality, string.Empty);
        ds.AddOrUpdate(DicomTag.SeriesDate, string.Empty);
        ds.AddOrUpdate(DicomTag.NumberOfSeriesRelatedInstances, string.Empty);
        ds.AddOrUpdate(DicomTag.SOPClassUID, string.Empty);

        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.Dataset is not null)
            {
                var seriesDate = response.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDate, string.Empty);

                // Fall back to Study date when Series date is missing — common in ARIA
                if (string.IsNullOrEmpty(seriesDate))
                    seriesDate = study.StudyDate;

                results.Add(new DicomSeriesResult(
                    PatientId: study.PatientId,
                    PatientName: study.PatientName,
                    PatientDob: study.PatientDob,
                    StudyInstanceUid: study.StudyInstanceUid,
                    SeriesInstanceUid: response.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
                    SeriesDescription: response.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
                    Modality: response.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
                    SeriesDate: seriesDate,
                    NumberOfImages: response.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfSeriesRelatedInstances, 0)));
            }
        };

        var client = BuildClient();
        await client.AddRequestAsync(request);

        try { await client.SendAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Series C-FIND failed for Study={Uid}.", study.StudyInstanceUid);
        }

        return results;
    }

    /// <summary>
    /// Issues an Image-level C-FIND for the series and returns:
    ///   - The actual instance count (definitive, since ARIA returns 0 at series level)
    ///   - A description fallback from ProtocolName or ImageComments on the first
    ///     instance, used when SeriesDescription is missing at series level
    /// </summary>
    private async Task<(int Count, string DescriptionFallback)> QuerySeriesImagesAsync(
        DicomSeriesResult series, CancellationToken ct)
    {
        int count = 0;
        string descriptionFallback = string.Empty;

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
        var ds = request.Dataset;
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, string.Empty);
        ds.AddOrUpdate(DicomTag.SOPClassUID, string.Empty);
        ds.AddOrUpdate(DicomTag.ProtocolName, string.Empty);
        ds.AddOrUpdate(DicomTag.ImageComments, string.Empty);

        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Pending && response.Dataset is not null)
            {
                var sopClass = response.Dataset
                    .GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

                if (ExcludedSopClasses.Contains(sopClass))
                    return;

                count++;

                // Grab description fallback from first instance only
                if (count == 1 && string.IsNullOrEmpty(descriptionFallback))
                {
                    var protocol = response.Dataset
                        .GetSingleValueOrDefault(DicomTag.ProtocolName, string.Empty);
                    var comments = response.Dataset
                        .GetSingleValueOrDefault(DicomTag.ImageComments, string.Empty);

                    descriptionFallback = !string.IsNullOrEmpty(protocol) ? protocol
                        : !string.IsNullOrEmpty(comments) ? comments
                        : string.Empty;
                }
            }
        };

        var client = BuildClient();
        await client.AddRequestAsync(request);

        try { await client.SendAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Image C-FIND failed for Series={Uid} — excluding series.", series.SeriesInstanceUid);
            return (0, string.Empty);
        }

        return (count, descriptionFallback);
    }

    // ─── Pre-filter (before image-level queries) ──────────────────────────────

    /// <summary>
    /// Fast client-side pre-filter applied before the image-level C-FIND loop
    /// to avoid querying obviously non-diagnostic series.
    /// The image count check in the main pipeline is the definitive filter.
    /// </summary>
    private static bool IsLikelyDiagnosticCt(DicomSeriesResult s)
    {
        var desc = s.SeriesDescription.ToUpperInvariant();

        if (desc.Contains("4DCT") ||
            desc.Contains("RESPIRATORY"))      // RT planning phase series pattern
            return false;

        return true;
    }

    // ─── Client factory ───────────────────────────────────────────────────────

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
}
