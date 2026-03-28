using CTTiltCorrector.Corrector;
using FellowOakDicom;
using FluentAssertions;
using static CTTiltCorrector.Corrector.DicomSeriesLoader;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class DicomSeriesLoaderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Standard axial IOP: row=X, col=Y → normal=[0,0,1]
    private static readonly double[] AxialIop = [1, 0, 0, 0, 1, 0];

    /// <summary>
    /// Builds a minimal valid DICOM dataset for an axial slice at the given Z position.
    /// </summary>
    private static DicomDataset MakeDataset(
        double z,
        string seriesUid = "1.2.3",
        string sopUid = null!,
        double[] iop = null!,
        string? patientPosition = "HFS",
        int instanceNumber = 0)
    {
        iop ??= AxialIop;
        sopUid ??= $"1.2.3.{z}";

        var ds = new DicomDataset();
        ds.Add(DicomTag.SeriesInstanceUID, seriesUid);
        ds.Add(DicomTag.SOPInstanceUID, sopUid);
        ds.Add(DicomTag.ImagePositionPatient, new double[] { 0, 0, z });
        ds.Add(DicomTag.ImageOrientationPatient, iop);
        ds.Add(DicomTag.InstanceNumber, instanceNumber);
        if (patientPosition is not null)
            ds.Add(DicomTag.PatientPosition, patientPosition);
        return ds;
    }

    private static List<DicomDataset> MakeAxialSeries(int count, double spacing = 1.0, string seriesUid = "1.2.3")
        => Enumerable.Range(0, count)
            .Select(i => MakeDataset(i * spacing, seriesUid: seriesUid, sopUid: $"1.2.3.{i + 1}", instanceNumber: i + 1))
            .ToList();

    // ── Filtering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Load_EmptyList_Throws()
    {
        Action act = () => DicomSeriesLoader.Load([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No valid DICOM slices*");
    }

    [Fact]
    public void Load_AllDatasetsMissingIpp_Throws()
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.SeriesInstanceUID, "1.2.3");
        ds.Add(DicomTag.ImageOrientationPatient, AxialIop);

        Action act = () => DicomSeriesLoader.Load([ds]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No valid DICOM slices*");
    }

    [Fact]
    public void Load_AllDatasetsMissingIop_Throws()
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.SeriesInstanceUID, "1.2.3");
        ds.Add(DicomTag.ImagePositionPatient, new double[] { 0, 0, 0 });

        Action act = () => DicomSeriesLoader.Load([ds]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No valid DICOM slices*");
    }

    [Fact]
    public void Load_MixedValidAndInvalidDatasets_OnlyLoadsValid()
    {
        // One dataset missing IPP — should be silently skipped, leaving 2 valid slices
        var missingIpp = new DicomDataset();
        missingIpp.Add(DicomTag.SeriesInstanceUID, "1.2.3");
        missingIpp.Add(DicomTag.ImageOrientationPatient, AxialIop);
        missingIpp.Add(DicomTag.PatientPosition, "HFS");

        var datasets = MakeAxialSeries(2);
        datasets.Add(missingIpp);

        var result = DicomSeriesLoader.Load(datasets);

        result.Should().HaveCount(2);
    }

    // ── Happy-path loading ────────────────────────────────────────────────────

    [Fact]
    public void Load_SingleValidSlice_ReturnsSingleSlice()
    {
        var datasets = new List<DicomDataset> { MakeDataset(0.0) };

        var result = DicomSeriesLoader.Load(datasets);

        result.Should().HaveCount(1);
    }

    [Fact]
    public void Load_ThreeAxialSlices_ReturnsThreeSlices()
    {
        var datasets = MakeAxialSeries(3);

        var result = DicomSeriesLoader.Load(datasets);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Load_SlicesRetainOriginalDatasets()
    {
        var datasets = MakeAxialSeries(2);

        var result = DicomSeriesLoader.Load(datasets);

        result.Select(s => s.Dataset).Should().BeSubsetOf(datasets);
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    [Fact]
    public void Load_SlicesInReverseZOrder_ReturnsSortedByZ()
    {
        // Provide slices in descending Z order
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 20.0, sopUid: "1.2.3.3"),
            MakeDataset(z: 10.0, sopUid: "1.2.3.2"),
            MakeDataset(z: 0.0,  sopUid: "1.2.3.1"),
        };

        var result = DicomSeriesLoader.Load(datasets);

        result.Select(s => s.ImagePositionPatient[2])
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void Load_SlicesInRandomZOrder_ReturnsSortedByZ()
    {
        // Uniform 1 mm spacing but provided out of order
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 2.0, sopUid: "1.2.3.3"),
            MakeDataset(z: 0.0, sopUid: "1.2.3.1"),
            MakeDataset(z: 3.0, sopUid: "1.2.3.4"),
            MakeDataset(z: 1.0, sopUid: "1.2.3.2"),
        };

        var result = DicomSeriesLoader.Load(datasets);

        result.Select(s => s.ImagePositionPatient[2])
            .Should().BeInAscendingOrder();
    }

    // ── SlicePosition calculation ─────────────────────────────────────────────

    [Fact]
    public void Load_AxialSeries_SlicePositionEqualsZ()
    {
        // For axial IOP the normal is [0,0,1], so SlicePosition = dot(IPP,[0,0,1]) = Z
        var datasets = MakeAxialSeries(3, spacing: 2.5);

        var result = DicomSeriesLoader.Load(datasets);

        for (int i = 0; i < result.Count; i++)
        {
            double expectedZ = result[i].ImagePositionPatient[2];
            result[i].SlicePosition.Should().BeApproximately(expectedZ, 1e-10);
        }
    }

    [Fact]
    public void Load_ImagePositionPatientPopulated()
    {
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 3.0, sopUid: "1"),
        };

        var result = DicomSeriesLoader.Load(datasets);

        result[0].ImagePositionPatient.Should().BeEquivalentTo(new double[] { 0, 0, 3.0 });
    }

    // ── Validation delegation ─────────────────────────────────────────────────

    [Fact]
    public void Load_MultipleSeriesUids_Throws()
    {
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 0.0, seriesUid: "1.2.3", sopUid: "1.2.3.1"),
            MakeDataset(z: 1.0, seriesUid: "1.2.4", sopUid: "1.2.3.2"),
        };

        Action act = () => DicomSeriesLoader.Load(datasets);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SeriesInstanceUID*");
    }

    [Fact]
    public void Load_InconsistentIop_Throws()
    {
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 0.0, sopUid: "1", iop: [1, 0, 0, 0, 1, 0]),
            MakeDataset(z: 1.0, sopUid: "2", iop: [1, 0, 0, 0, 1, 0.5]),
        };

        Action act = () => DicomSeriesLoader.Load(datasets);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IOP mismatch*");
    }

    [Fact]
    public void Load_NonUniformSpacing_Throws()
    {
        var datasets = new List<DicomDataset>
        {
            MakeDataset(z: 0.0,  sopUid: "1"),
            MakeDataset(z: 1.0,  sopUid: "2"),
            MakeDataset(z: 4.0,  sopUid: "3"),  // gap jumps from 1 to 3 mm
        };

        Action act = () => DicomSeriesLoader.Load(datasets);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Non-uniform slice spacing*");
    }
}
