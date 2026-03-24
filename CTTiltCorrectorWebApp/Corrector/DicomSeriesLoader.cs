using FellowOakDicom;

namespace CTTiltCorrector.Corrector;

public static class DicomSeriesLoader
{
    public class SliceInfo
    {
        public DicomDataset Dataset { get; set; } = null!;
        public double SlicePosition { get; set; }
        public double[] ImagePositionPatient { get; set; } = new double[3];
        public int InstanceNumber { get; set; }
    }

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

        // Same validation calls as before — just update DicomLoadValidators
        // to use SliceInfo.Dataset instead of SliceInfo.DicomFile.Dataset
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
