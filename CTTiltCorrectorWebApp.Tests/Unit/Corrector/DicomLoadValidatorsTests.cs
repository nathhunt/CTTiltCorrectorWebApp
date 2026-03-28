using CTTiltCorrector.Corrector;
using FellowOakDicom;
using FluentAssertions;
using static CTTiltCorrector.Corrector.DicomSeriesLoader;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class DicomLoadValidatorsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SliceInfo MakeSlice(
        string seriesUid = "1.2.3",
        string sopUid = "1.2.3.4",
        double[] iop = null!,
        double slicePosition = 0.0,
        string? patientPosition = "HFS")
    {
        iop ??= [1, 0, 0, 0, 1, 0];

        var ds = new DicomDataset();
        ds.Add(DicomTag.SeriesInstanceUID, seriesUid);
        ds.Add(DicomTag.SOPInstanceUID, sopUid);
        ds.Add(DicomTag.ImageOrientationPatient, iop);
        if (patientPosition is not null)
            ds.Add(DicomTag.PatientPosition, patientPosition);

        return new SliceInfo { Dataset = ds, SlicePosition = slicePosition };
    }

    private static List<SliceInfo> UniformSlices(int count, double spacing = 1.0, string seriesUid = "1.2.3")
    {
        return Enumerable.Range(0, count)
            .Select(i => MakeSlice(
                seriesUid: seriesUid,
                sopUid: $"1.2.3.{i + 1}",
                slicePosition: i * spacing))
            .ToList();
    }

    // ── AssertSingleSeries ────────────────────────────────────────────────────

    [Fact]
    public void AssertSingleSeries_SingleUid_DoesNotThrow()
    {
        var slices = UniformSlices(3);

        Action act = () => DicomLoadValidators.AssertSingleSeries(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertSingleSeries_TwoDistinctUids_Throws()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(seriesUid: "1.2.3", sopUid: "1.2.3.1"),
            MakeSlice(seriesUid: "1.2.4", sopUid: "1.2.3.2"),
        };

        Action act = () => DicomLoadValidators.AssertSingleSeries(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*2 distinct SeriesInstanceUIDs*");
    }

    [Fact]
    public void AssertSingleSeries_ThreeDistinctUids_MessageListsAllUids()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(seriesUid: "1.2.3", sopUid: "1.2.3.1"),
            MakeSlice(seriesUid: "1.2.4", sopUid: "1.2.3.2"),
            MakeSlice(seriesUid: "1.2.5", sopUid: "1.2.3.3"),
        };

        Action act = () => DicomLoadValidators.AssertSingleSeries(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*3 distinct SeriesInstanceUIDs*");
    }

    // ── AssertUniformIop ──────────────────────────────────────────────────────

    [Fact]
    public void AssertUniformIop_AllSameIop_ReturnsReferenceVectors()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", iop: [1, 0, 0, 0, 1, 0]),
            MakeSlice(sopUid: "2", iop: [1, 0, 0, 0, 1, 0]),
            MakeSlice(sopUid: "3", iop: [1, 0, 0, 0, 1, 0]),
        };

        var (row, col) = DicomLoadValidators.AssertUniformIop(slices);

        row.Should().BeEquivalentTo([1.0, 0.0, 0.0]);
        col.Should().BeEquivalentTo([0.0, 1.0, 0.0]);
    }

    [Fact]
    public void AssertUniformIop_DeviationBelowTolerance_DoesNotThrow()
    {
        // Total element-wise diff = 0.009 < 0.01 tolerance
        double tiny = 0.009 / 6.0;
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", iop: [1, 0, 0, 0, 1, 0]),
            MakeSlice(sopUid: "2", iop: [1 + tiny, tiny, tiny, tiny, 1 + tiny, tiny]),
        };

        Action act = () => DicomLoadValidators.AssertUniformIop(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertUniformIop_DeviationExceedsTolerance_Throws()
    {
        // Shift one element by 0.02 → diff > 0.01 tolerance
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", iop: [1, 0, 0, 0, 1, 0]),
            MakeSlice(sopUid: "2", iop: [1, 0, 0, 0, 1, 0.02]),
        };

        Action act = () => DicomLoadValidators.AssertUniformIop(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IOP mismatch*");
    }

    [Fact]
    public void AssertUniformIop_SingleSlice_ReturnsItsOwnIop()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", iop: [1, 0, 0, 0, 1, 0]),
        };

        var (row, col) = DicomLoadValidators.AssertUniformIop(slices);

        row.Should().BeEquivalentTo([1.0, 0.0, 0.0]);
        col.Should().BeEquivalentTo([0.0, 1.0, 0.0]);
    }

    // ── AssertUniformSpacing ──────────────────────────────────────────────────

    [Fact]
    public void AssertUniformSpacing_FewerThanTwoSlices_DoesNotThrow()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", slicePosition: 0),
        };

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertUniformSpacing_ExactlyTwoSlices_DoesNotThrow()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", slicePosition: 0),
            MakeSlice(sopUid: "2", slicePosition: 2.5),
        };

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertUniformSpacing_UniformSpacing_DoesNotThrow()
    {
        var slices = UniformSlices(5, spacing: 3.0);

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertUniformSpacing_SpacingDeviationBelowTolerance_DoesNotThrow()
    {
        // Reference gap = 2.0 mm; one gap = 2.009 mm → delta = 0.009 < 0.01
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", slicePosition: 0.0),
            MakeSlice(sopUid: "2", slicePosition: 2.0),
            MakeSlice(sopUid: "3", slicePosition: 4.009),
        };

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertUniformSpacing_NonUniformSpacing_Throws()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", slicePosition: 0.0),
            MakeSlice(sopUid: "2", slicePosition: 2.0),
            MakeSlice(sopUid: "3", slicePosition: 4.05),  // gap = 2.05, delta = 0.05 > 0.01
        };

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Non-uniform slice spacing*");
    }

    [Fact]
    public void AssertUniformSpacing_GapInMiddleOfLargeSeries_ThrowsNamingOffendingSlices()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", slicePosition: 0.0),
            MakeSlice(sopUid: "2", slicePosition: 1.0),
            MakeSlice(sopUid: "3", slicePosition: 2.0),
            MakeSlice(sopUid: "4", slicePosition: 5.0),  // jump: gap = 3.0, delta = 2.0 > 0.01
            MakeSlice(sopUid: "5", slicePosition: 6.0),
        };

        Action act = () => DicomLoadValidators.AssertUniformSpacing(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Non-uniform slice spacing*");
    }

    // ── AssertPatientPosition ─────────────────────────────────────────────────

    [Fact]
    public void AssertPatientPosition_AllHfs_DoesNotThrow()
    {
        var slices = UniformSlices(3);  // MakeSlice defaults to HFS

        Action act = () => DicomLoadValidators.AssertPatientPosition(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertPatientPosition_AllFfs_DoesNotThrow()
    {
        var slices = Enumerable.Range(0, 3)
            .Select(i => MakeSlice(sopUid: $"1.2.3.{i}", slicePosition: i, patientPosition: "FFS"))
            .ToList();

        Action act = () => DicomLoadValidators.AssertPatientPosition(slices);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertPatientPosition_MissingTag_Throws()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", patientPosition: "HFS"),
            MakeSlice(sopUid: "2", patientPosition: null),  // tag omitted
        };

        Action act = () => DicomLoadValidators.AssertPatientPosition(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PatientPosition tag is missing*");
    }

    [Fact]
    public void AssertPatientPosition_MixedPositions_Throws()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", patientPosition: "HFS"),
            MakeSlice(sopUid: "2", patientPosition: "FFS"),
        };

        Action act = () => DicomLoadValidators.AssertPatientPosition(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Multiple PatientPosition values*");
    }

    [Fact]
    public void AssertPatientPosition_UnsupportedPosition_Throws()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(sopUid: "1", patientPosition: "HFP"),
            MakeSlice(sopUid: "2", patientPosition: "HFP"),
        };

        Action act = () => DicomLoadValidators.AssertPatientPosition(slices);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported PatientPosition*");
    }
}
