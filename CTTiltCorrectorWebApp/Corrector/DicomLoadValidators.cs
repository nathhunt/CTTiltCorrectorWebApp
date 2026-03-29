using FellowOakDicom;
using static CTTiltCorrector.Corrector.DicomSeriesLoader;

namespace CTTiltCorrector.Corrector;

public class DicomLoadValidators
{
    // Maximum per-element sum-of-absolute-differences allowed between a
    // slice's IOP and the reference IOP before the slice is rejected.
    private const double IopTolerance = 0.01;

    // Maximum deviation from the reference slice gap before spacing is
    // considered non-uniform.
    private const double SpacingTolerance = 0.01; // mm

    /// <summary>
    /// Throws if more than one SeriesInstanceUID is present in the candidate list.
    /// </summary>
    public static void AssertSingleSeries(List<SliceInfo> candidates)
    {
        var uids = candidates
            .Select(s => s.Dataset.GetString(DicomTag.SeriesInstanceUID) ?? "")
            .Distinct()
            .OrderBy(u => u)
            .ToList();

        if (uids.Count > 1)
            throw new InvalidOperationException(
                $"Folder contains {uids.Count} distinct SeriesInstanceUIDs. " +
                $"Please provide a folder with exactly one series.\n" +
                $"  UIDs found:\n" +
                string.Join("\n", uids.Select(u => $"    {u}")));
    }

    /// <summary>
    /// Throws if any slice's IOP differs from the first slice's IOP by more
    /// than <see cref="IopTolerance"/>. Returns the reference IOP on success.
    /// </summary>
    public static (double[] row, double[] col) AssertUniformIop(List<SliceInfo> candidates)
    {
        string iopStr0 = candidates[0].Dataset.GetString(DicomTag.ImageOrientationPatient)!;
        var (refRow, refCol) = OrientationHelper.ParseIOP(iopStr0);

        foreach (var s in candidates)
        {
            string iopStr = s.Dataset.GetString(DicomTag.ImageOrientationPatient)!;
            var (row, col) = OrientationHelper.ParseIOP(iopStr);

            double diff = Math.Abs(row[0] - refRow[0]) + Math.Abs(row[1] - refRow[1]) + Math.Abs(row[2] - refRow[2])
                        + Math.Abs(col[0] - refCol[0]) + Math.Abs(col[1] - refCol[1]) + Math.Abs(col[2] - refCol[2]);

            if (diff > IopTolerance)
                throw new InvalidOperationException(
                    $"IOP mismatch on slice '{s.Dataset.GetString(DicomTag.SOPInstanceUID)}' (diff={diff:F4}). " +
                    $"All slices must share the same ImageOrientationPatient.\n" +
                    $"  Reference : [{string.Join(", ", refRow.Concat(refCol).Select(v => v.ToString("F6")))}]\n" +
                    $"  This slice: [{string.Join(", ", row.Concat(col).Select(v => v.ToString("F6")))}]");
        }

        return (refRow, refCol);
    }

    /// <summary>
    /// Throws if the gap between any two consecutive slices deviates from the
    /// first gap by more than <see cref="SpacingTolerance"/> mm.
    /// Assumes slices are already sorted by position.
    /// </summary>
    public static void AssertUniformSpacing(List<SliceInfo> slices)
    {
        if (slices.Count < 2) return;

        double refGap = slices[1].SlicePosition - slices[0].SlicePosition;

        for (int i = 1; i < slices.Count - 1; i++)
        {
            double gap = slices[i + 1].SlicePosition - slices[i].SlicePosition;
            double delta = Math.Abs(gap - refGap);

            if (delta > SpacingTolerance)
                throw new InvalidOperationException(
                    $"Non-uniform slice spacing detected between slices {i} and {i + 1} " +
                    $"('{slices[i].Dataset.GetString(DicomTag.SOPInstanceUID)}' → '{slices[i + 1].Dataset.GetString(DicomTag.SOPInstanceUID)}').\n" +
                    $"  Expected gap : {refGap:F4} mm\n" +
                    $"  Actual gap   : {gap:F4} mm\n" +
                    $"  Delta        : {delta:F4} mm\n" +
                    $"The series may contain gaps, duplicate slices, or mixed acquisitions.");
        }
    }

    /// <summary>
    /// Throws if any slice is missing the PatientPosition tag, if multiple
    /// distinct values are found, or if the position is not HFS or FFS.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with diagnostic details (slice SOPInstanceUIDs and found values)
    /// when the series fails the check.
    /// </exception>
    public static void AssertPatientPosition(List<SliceInfo> slices)
    {
        var missing = slices.Where(s => !s.Dataset.Contains(DicomTag.PatientPosition)).ToList();
        if (missing.Any())
            throw new InvalidOperationException(
                $"PatientPosition tag is missing from {missing.Count} slice(s):\n" +
                string.Join("\n", missing.Select(s => $"  {s.Dataset.GetString(DicomTag.SOPInstanceUID)}")));

        var positions = slices.Select(s => s.Dataset.GetString(DicomTag.PatientPosition) ?? "").Distinct().ToList();
        if (positions.Count > 1)
            throw new InvalidOperationException(
                $"Multiple PatientPosition values found in series: {string.Join(", ", positions)}. " +
                $"All slices must share the same PatientPosition.");

        if (!(positions.Contains("HFS") || positions.Contains("FFS")))
            throw new InvalidOperationException(
                $"Unsupported PatientPosition value found in series: {string.Join(", ", positions.Where(p => p != "HFS" && p != "FFS"))}. " +
                $"Only HFS and FFS are supported.");
    }
}
