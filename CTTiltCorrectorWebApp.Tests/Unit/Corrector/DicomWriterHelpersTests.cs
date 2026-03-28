using CTTiltCorrector.Corrector;
using FluentAssertions;
using static CTTiltCorrector.Corrector.DicomSeriesLoader;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class DicomWriterHelpersTests
{
    // ── FindNearestReferenceSlice ─────────────────────────────────────────────

    private static SliceInfo At(double pos) => new SliceInfo { SlicePosition = pos };

    [Fact]
    public void FindNearest_SingleSlice_ReturnsThatSlice()
    {
        var slice = At(5.0);

        var result = DicomWriterHelpers.FindNearestReferenceSlice(7.0, [slice]);

        result.Should().BeSameAs(slice);
    }

    [Fact]
    public void FindNearest_ExactPositionMatch_ReturnsThatSlice()
    {
        var slices = new SliceInfo[] { At(0.0), At(10.0), At(20.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(10.0, slices);

        result.SlicePosition.Should().Be(10.0);
    }

    [Fact]
    public void FindNearest_TargetBetweenSlices_ReturnsCloserOne()
    {
        var slices = new SliceInfo[] { At(0.0), At(10.0), At(20.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(13.0, slices);

        result.SlicePosition.Should().Be(10.0);
    }

    [Fact]
    public void FindNearest_TargetCloserToHigherSlice_ReturnsHigherOne()
    {
        var slices = new SliceInfo[] { At(0.0), At(10.0), At(20.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(17.0, slices);

        result.SlicePosition.Should().Be(20.0);
    }

    [Fact]
    public void FindNearest_TargetBelowAllSlices_ReturnsLowest()
    {
        var slices = new SliceInfo[] { At(10.0), At(20.0), At(30.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(-5.0, slices);

        result.SlicePosition.Should().Be(10.0);
    }

    [Fact]
    public void FindNearest_TargetAboveAllSlices_ReturnsHighest()
    {
        var slices = new SliceInfo[] { At(10.0), At(20.0), At(30.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(999.0, slices);

        result.SlicePosition.Should().Be(30.0);
    }

    [Fact]
    public void FindNearest_NegativePositions_WorksCorrectly()
    {
        var slices = new SliceInfo[] { At(-30.0), At(-20.0), At(-10.0) };

        var result = DicomWriterHelpers.FindNearestReferenceSlice(-22.0, slices);

        result.SlicePosition.Should().Be(-20.0);
    }

    [Fact]
    public void FindNearest_ManySlices_ReturnsCorrectOne()
    {
        var slices = Enumerable.Range(0, 100)
            .Select(i => At(i * 1.0))
            .ToArray();

        var result = DicomWriterHelpers.FindNearestReferenceSlice(47.3, slices);

        result.SlicePosition.Should().Be(47.0);
    }
}
