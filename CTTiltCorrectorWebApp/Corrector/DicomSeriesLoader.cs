using FellowOakDicom;

namespace CTTiltCorrector.Corrector;

/// <summary>
/// Loads a list of raw <see cref="DicomDataset"/> objects into a sorted,
/// validated collection of <see cref="SliceInfo"/> records ready for
/// geometry processing.
/// </summary>
public static class DicomSeriesLoader
{
    /// <summary>
    /// Metadata extracted from a single DICOM slice during loading.
    /// Used throughout the correction pipeline to avoid re-reading tags.
    /// </summary>
    public class SliceInfo
    {
        /// <summary>The raw DICOM dataset for this slice.</summary>
        public DicomDataset Dataset { get; set; } = null!;

        /// <summary>
        /// Projection of <see cref="ImagePositionPatient"/> onto the slice
        /// normal (row × col). Used for nearest-reference matching in
        /// <see cref="DicomWriterHelpers.FindNearestReferenceSlice"/> and for
        /// uniform-spacing validation. More reliable than raw IPP-Z when the
        /// gantry is tilted.
        /// </summary>
        public double SlicePosition { get; set; }

        /// <summary>
        /// The three-element LPS coordinate of the upper-left voxel centre
        /// (DICOM tag ImagePositionPatient).
        /// </summary>
        public double[] ImagePositionPatient { get; set; } = new double[3];

        /// <summary>DICOM InstanceNumber tag value (used for queue sort order).</summary>
        public int InstanceNumber { get; set; }
    }

    /// <summary>
    /// Validates and sorts a flat list of datasets into a geometry-ready
    /// <see cref="SliceInfo"/> collection.
    /// </summary>
    /// <remarks>
    /// Steps performed:
    /// <list type="number">
    ///   <item>Filter out any datasets missing IPP or IOP tags.</item>
    ///   <item>Assert all slices share a single SeriesInstanceUID.</item>
    ///   <item>Assert all slices share a uniform ImageOrientationPatient.</item>
    ///   <item>Compute <see cref="SliceInfo.SlicePosition"/> as the IPP dot-product with the tilt normal.</item>
    ///   <item>Sort by IPP-Z (ascending).</item>
    ///   <item>Assert uniform slice spacing along the tilt normal.</item>
    /// </list>
    /// </remarks>
    /// <param name="datasets">Raw datasets received from <see cref="InMemoryDicomStore"/>.</param>
    /// <returns>Sorted, validated list of <see cref="SliceInfo"/> records.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the series fails any validation check (mixed UIDs, non-uniform
    /// IOP, non-uniform spacing, or no valid slices).
    /// </exception>
    public static List<SliceInfo> Load(List<DicomDataset> datasets)
    {
        var candidates = new List<SliceInfo>();

        foreach (var ds in datasets)
        {
            if (!ds.Contains(DicomTag.ImagePositionPatient)) continue;
            if (!ds.Contains(DicomTag.ImageOrientationPatient)) continue;

            var ipp = ds.GetValues<double>(DicomTag.ImagePositionPatient);
            if (ipp == null || ipp.Length < 3) continue;

            candidates.Add(new SliceInfo
            {
                Dataset = ds,
                ImagePositionPatient = new[] { ipp[0], ipp[1], ipp[2] },
                InstanceNumber = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0)
            });
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("No valid DICOM slices in input.");

        DicomLoadValidators.AssertSingleSeries(candidates);
        var (refRow, refCol) = DicomLoadValidators.AssertUniformIop(candidates);
        double[] normal = OrientationHelper.SliceNormal(refRow, refCol);

        foreach (var s in candidates)
            s.SlicePosition = OrientationHelper.Dot(s.ImagePositionPatient, normal);

        candidates.Sort((a, b) => a.ImagePositionPatient[2].CompareTo(b.ImagePositionPatient[2]));

        DicomLoadValidators.AssertUniformSpacing(candidates);
        return candidates;
    }
}
