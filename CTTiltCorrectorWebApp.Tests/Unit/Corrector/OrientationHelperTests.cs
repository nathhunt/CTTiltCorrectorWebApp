using CTTiltCorrector.Corrector;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class OrientationHelperTests
{
    // ── ParseIOP ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseIOP_ValidString_ReturnsCorrectVectors()
    {
        var (row, col) = OrientationHelper.ParseIOP("1\\0\\0\\0\\1\\0");

        row.Should().BeEquivalentTo([1.0, 0.0, 0.0]);
        col.Should().BeEquivalentTo([0.0, 1.0, 0.0]);
    }

    [Fact]
    public void ParseIOP_TiltedGantry_ParsesCorrectly()
    {
        double s = Math.Round(Math.Sqrt(2) / 2, 6);
        var (row, col) = OrientationHelper.ParseIOP($"1\\0\\0\\0\\{s}\\{s}");

        row[0].Should().BeApproximately(1.0, 1e-6);
        col[1].Should().BeApproximately(s, 1e-6);
        col[2].Should().BeApproximately(s, 1e-6);
    }

    [Fact]
    public void ParseIOP_WrongElementCount_ThrowsFormatException()
    {
        Action act = () => OrientationHelper.ParseIOP("1\\0\\0\\0\\1");

        act.Should().Throw<FormatException>();
    }

    // ── SliceNormal ───────────────────────────────────────────────────────────

    [Fact]
    public void SliceNormal_AxialPlane_PointsAlongZ()
    {
        double[] row = [1, 0, 0];
        double[] col = [0, 1, 0];

        var normal = OrientationHelper.SliceNormal(row, col);

        normal.Should().BeEquivalentTo([0.0, 0.0, 1.0], opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void SliceNormal_CoronalPlane_PointsAlongY()
    {
        // row=[1,0,0] × col=[0,0,-1] = (0·(-1)-0·0, 0·0-1·(-1), 1·0-0·0) = (0, 1, 0)
        double[] row = [1, 0, 0];
        double[] col = [0, 0, -1];

        var normal = OrientationHelper.SliceNormal(row, col);

        normal[0].Should().BeApproximately(0.0, 1e-10);
        normal[1].Should().BeApproximately(1.0, 1e-10);
        normal[2].Should().BeApproximately(0.0, 1e-10);
    }

    [Fact]
    public void SliceNormal_ResultIsNormalised()
    {
        double[] row = [1, 0, 0];
        double[] col = [0, 1, 0];

        var normal = OrientationHelper.SliceNormal(row, col);
        double magnitude = Math.Sqrt(normal[0]*normal[0] + normal[1]*normal[1] + normal[2]*normal[2]);

        magnitude.Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void SliceNormal_ZeroVector_ThrowsInvalidOperationException()
    {
        double[] row = [0, 0, 0];
        double[] col = [0, 0, 0];

        Action act = () => OrientationHelper.SliceNormal(row, col);

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Dot ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_PerpendicularVectors_ReturnsZero()
    {
        double[] a = [1, 0, 0];
        double[] b = [0, 1, 0];

        OrientationHelper.Dot(a, b).Should().BeApproximately(0.0, 1e-10);
    }

    [Fact]
    public void Dot_ParallelUnitVectors_ReturnsOne()
    {
        double[] a = [1, 0, 0];

        OrientationHelper.Dot(a, a).Should().BeApproximately(1.0, 1e-10);
    }
}
