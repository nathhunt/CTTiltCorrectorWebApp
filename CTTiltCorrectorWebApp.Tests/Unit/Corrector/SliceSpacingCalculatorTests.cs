using CTTiltCorrector.Corrector;
using FellowOakDicom;
using FluentAssertions;
using static CTTiltCorrector.Corrector.DicomSeriesLoader;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class SliceSpacingCalculatorTests
{
    private static readonly IProgress<string> NoOp = new Progress<string>(_ => { });

    private static SliceInfo MakeSlice(double position, double? sliceThickness = null)
    {
        var ds = new DicomDataset();
        if (sliceThickness.HasValue)
            ds.Add(DicomTag.SliceThickness, (decimal)sliceThickness.Value);
        return new SliceInfo { Dataset = ds, SlicePosition = position };
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public void Compute_NullSlices_Throws()
    {
        Action act = () => SliceSpacingCalculator.Compute(null!, NoOp);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_EmptyList_Throws()
    {
        Action act = () => SliceSpacingCalculator.Compute([], NoOp);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*empty*");
    }

    // ── SliceThickness tag path ───────────────────────────────────────────────

    [Fact]
    public void Compute_SliceThicknessPresent_ReturnsThatValue()
    {
        var slices = new List<SliceInfo> { MakeSlice(0.0, sliceThickness: 3.0) };

        double result = SliceSpacingCalculator.Compute(slices, NoOp);

        result.Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void Compute_SliceThicknessPresentOnFirstSlice_IgnoresOtherSlices()
    {
        // Only the first slice's tag is read; others are irrelevant
        var slices = new List<SliceInfo>
        {
            MakeSlice(0.0, sliceThickness: 5.0),
            MakeSlice(1.0, sliceThickness: 1.0), // ignored
        };

        double result = SliceSpacingCalculator.Compute(slices, NoOp);

        result.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Compute_SliceThicknessZero_FallsBackToMedianIppGap()
    {
        // Zero thickness is invalid → fall back to IPP gap of 2.0 mm
        var slices = new List<SliceInfo>
        {
            MakeSlice(0.0, sliceThickness: 0.0),
            MakeSlice(2.0),
        };

        double result = SliceSpacingCalculator.Compute(slices, NoOp);

        result.Should().BeApproximately(2.0, 1e-9);
    }

    // ── Median IPP gap fallback ───────────────────────────────────────────────

    [Fact]
    public void Compute_NoSliceThickness_UsesMedianIppGap()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(0.0),
            MakeSlice(2.5),
            MakeSlice(5.0),
        };

        double result = SliceSpacingCalculator.Compute(slices, NoOp);

        result.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Compute_SingleSliceNoThickness_ReturnsDefault1mm()
    {
        var slices = new List<SliceInfo> { MakeSlice(0.0) };

        double result = SliceSpacingCalculator.Compute(slices, NoOp);

        result.Should().BeApproximately(1.0, 1e-9);
    }

    // ── MedianPositionSpacing ─────────────────────────────────────────────────

    [Fact]
    public void MedianPositionSpacing_NullSlices_Throws()
    {
        Action act = () => SliceSpacingCalculator.MedianPositionSpacing(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MedianPositionSpacing_SingleSlice_ReturnsDefault1mm()
    {
        var slices = new List<SliceInfo> { MakeSlice(0.0) };

        double result = SliceSpacingCalculator.MedianPositionSpacing(slices);

        result.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void MedianPositionSpacing_TwoSlices_ReturnsSingleGap()
    {
        var slices = new List<SliceInfo>
        {
            MakeSlice(0.0),
            MakeSlice(3.0),
        };

        double result = SliceSpacingCalculator.MedianPositionSpacing(slices);

        result.Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void MedianPositionSpacing_UniformSpacing_ReturnsCommonGap()
    {
        var slices = Enumerable.Range(0, 10)
            .Select(i => MakeSlice(i * 1.5))
            .ToList();

        double result = SliceSpacingCalculator.MedianPositionSpacing(slices);

        result.Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void MedianPositionSpacing_OutlierGap_IsRejectedByFilter()
    {
        // 9 gaps of 1 mm, one huge outlier — median should still be ~1 mm
        var positions = Enumerable.Range(0, 10).Select(i => (double)i).ToList();
        positions.Add(1000.0); // outlier
        var slices = positions.Select(p => MakeSlice(p)).ToList();

        double result = SliceSpacingCalculator.MedianPositionSpacing(slices);

        result.Should().BeApproximately(1.0, 1e-9);
    }
}
