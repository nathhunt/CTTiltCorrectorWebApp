using CTTiltCorrector.Services;
using FellowOakDicom;

namespace CTTiltCorrector;

/// <summary>
/// Replace the body of <see cref="CorrectAsync"/> with your tilt correction algorithm.
///
/// Registration in Program.cs (already wired):
///   builder.Services.AddScoped&lt;ITiltCorrector, YourTiltCorrector&gt;();
///
/// Contract:
///   - Input:  all slices for the series, sorted ascending by Instance Number, fully in memory.
///   - Output: the corrected datasets ready to be C-STOREd back to ARIA.
///             UIDs, pixel data, and tag modifications are entirely your responsibility.
///   - Use <paramref name="progress"/> to stream status messages to the Monitor UI.
///   - Respect <paramref name="ct"/> inside any long-running loops.
/// </summary>
public class YourTiltCorrector : ITiltCorrector
{
    private readonly ILogger<YourTiltCorrector> _logger;

    public YourTiltCorrector(ILogger<YourTiltCorrector> logger)
    {
        _logger = logger;
    }

    public Task<List<DicomDataset>> CorrectAsync(
        List<DicomDataset> slices,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // ── Replace everything below with your implementation ──────────────

        progress.Report($"YourTiltCorrector: received {slices.Count} slices.");

        // TODO: implement tilt correction
        // e.g.:
        //   var corrected = MyAlgorithm.Run(slices, ct);
        //   progress.Report("Correction complete.");
        //   return Task.FromResult(corrected);

        throw new NotImplementedException(
            "Replace YourTiltCorrector.CorrectAsync with your tilt correction algorithm.");

        // ──────────────────────────────────────────────────────────────────
    }
}
