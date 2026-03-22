using FellowOakDicom;

namespace CTTiltCorrector.Corrector;

/// <summary>
/// Determines the output slice spacing for a sorted DICOM series.
///
/// Strategy: always use the SliceThickness tag as the output spacing.
/// The SliceThickness tag describes the intended reconstruction spacing
/// and is what viewers (including Eclipse) expect as the inter-slice
/// distance for display and MPR.
///
/// The IPP-derived spacing is NOT used for the output spacing because
/// many CT series are acquired with overlapping thin slices (small IPP
/// gap) but reconstructed at a coarser SliceThickness for storage and
/// display — using the IPP gap would produce the wrong number of output
/// slices and the wrong Z extent.
///
/// The IPP spacing is still used internally by <see cref="DicomSeriesLoader"/>
/// to sort slices and detect positional outliers, but it must not drive
/// the output grid.
/// </summary>
public static class SliceSpacingCalculator
{
    public static double Compute(IReadOnlyList<DicomSeriesLoader.SliceInfo> slices)
    {
        if (slices == null) throw new ArgumentNullException(nameof(slices));
        if (slices.Count == 0) throw new ArgumentException("Slice list is empty.", nameof(slices));

        // ── Use SliceThickness as the output spacing ───────────────────────
        try
        {
            var ds = slices[0].Dataset;
            if (ds.Contains(DicomTag.SliceThickness))
            {
                double st = ds.GetSingleValue<double>(DicomTag.SliceThickness);
                if (st > 0)
                {
                    Console.WriteLine($"[Spacing] Using SliceThickness tag: {st:F4} mm");
                    return st;
                }
            }
        }
        catch { }

        // ── Fallback: median IPP gap if SliceThickness is absent ──────────
        if (slices.Count >= 2)
        {
            double fallback = MedianPositionSpacing(slices);
            Console.WriteLine($"[Spacing] SliceThickness absent — using median IPP gap: {fallback:F4} mm");
            return fallback;
        }

        Console.WriteLine("[Spacing] Single slice, no SliceThickness — defaulting to 1.0 mm");
        return 1.0;
    }

    /// <summary>
    /// Robust median of consecutive SlicePosition gaps, with outlier
    /// rejection.  Used only as a fallback when SliceThickness is absent.
    /// </summary>
    public static double MedianPositionSpacing(IReadOnlyList<DicomSeriesLoader.SliceInfo> slices)
    {
        if (slices == null) throw new ArgumentNullException(nameof(slices));
        if (slices.Count < 2) return 1.0;

        double[] diffs = new double[slices.Count - 1];
        for (int i = 0; i < diffs.Length; i++)
            diffs[i] = Math.Abs(slices[i + 1].SlicePosition - slices[i].SlicePosition);

        Array.Sort(diffs);
        double roughMedian = diffs[diffs.Length / 2];
        if (roughMedian <= 0) return 1.0;

        double[] filtered = diffs.Where(d => d <= roughMedian * 4.0).ToArray();
        return filtered.Length > 0 ? filtered[filtered.Length / 2] : roughMedian;
    }
}