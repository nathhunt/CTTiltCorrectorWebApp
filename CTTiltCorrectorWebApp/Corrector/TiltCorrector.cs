using CTTiltCorrector.Services;
using DicomTiltCorrector;
using FellowOakDicom;
using itk.simple;

namespace CTTiltCorrector.Corrector;

public class TiltCorrector : ITiltCorrector
{
    public TiltCorrector()
    {
    }

    public async Task<List<DicomDataset>> CorrectAsync(
    List<DicomDataset> slices,
    IProgress<string> progress,
    CancellationToken ct)
    {
        progress.Report($"Loading {slices.Count} slices…");

        // ── 1. Load and sort ──────────────────────────────────────────────
        var sortedSlices = DicomSeriesLoader.Load(slices);

        // ── 2. Check if already identity IOP ─────────────────────────────
        string iopStr = sortedSlices[0].Dataset.GetString(DicomTag.ImageOrientationPatient)
                        ?? "1\\0\\0\\0\\1\\0";
        var (row, col) = OrientationHelper.ParseIOP(iopStr);

        if (row[0] == 1 && row[1] == 0 && row[2] == 0 &&
            col[0] == 0 && col[1] == 1 && col[2] == 0)
        {
            progress.Report("Already HFS identity — no correction needed.");
            return slices;
        }

        // ── 3. Compute slice spacing ──────────────────────────────────────
        double spacing = SliceSpacingCalculator.Compute(sortedSlices, progress);
        progress.Report($"Slice spacing: {spacing:F4} mm");

        // ── 4. Derive corrected PatientPosition ───────────────────────────
        string origPos = sortedSlices[0].Dataset
            .GetSingleValueOrDefault(DicomTag.PatientPosition, "HFS");
        string correctedPos = (origPos == "HFP" || origPos == "FFP") ? "HFP" : "HFS";
        progress.Report($"PatientPosition: {origPos} -> {correctedPos}");

        // ── 5. Resample ───────────────────────────────────────────────────
        progress.Report("Resampling to HFS identity orientation…");

        Image corrected;
        try
        {
            corrected = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // This is where the heavy lifting and potential SimpleITK errors happen
                return SimpleItkResampler.Resample(sortedSlices, progress, spacing);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            progress.Report("Resampling cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            // Catch SimpleITK ApplicationExceptions or memory access errors
            var errorMessage = $"[ERROR] Resampling failed: {ex.Message}";
            progress.Report(errorMessage);

            // Log the full stack trace for debugging at WRCC
            Console.WriteLine($"{errorMessage}\n{ex.StackTrace}");

            // Rethrow or return null depending on how your UI handles failures
            throw new Exception(errorMessage, ex);
        }

        // ── 6. Build output datasets ──────────────────────────────────────
        progress.Report("Building output datasets…");

        List<DicomDataset> results = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var writer = new DicomSeriesWriter(progress, sortedSlices, correctedPos);
            return writer.BuildDatasets(corrected);
        }, ct);

        corrected.Dispose();

        return results;
    }
}
