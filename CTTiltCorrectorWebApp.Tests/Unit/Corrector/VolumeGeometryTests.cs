using CTTiltCorrector.Corrector;
using FluentAssertions;

namespace CTTiltCorrectorWebApp.Tests.Unit.Corrector;

public class VolumeGeometryTests
{
    // ── Identity direction (no tilt) ─────────────────────────────────────────

    [Fact]
    public void ComputeAABB_IdentityDirection_OriginMatchesInput()
    {
        double[] origin    = [0.0, 0.0, 0.0];
        double[] direction = [1,0,0, 0,1,0, 0,0,1]; // identity
        double[] spacing   = [1.0, 1.0, 1.0];
        uint[]   size      = [10, 10, 10];

        var (aabbOrigin, _) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            origin, direction, spacing, size, 1.0, 1.0, 1.0);

        aabbOrigin.Should().BeEquivalentTo([0.0, 0.0, 0.0]);
    }

    [Fact]
    public void ComputeAABB_IdentityDirection_GridSizeMatchesInput()
    {
        double[] origin    = [0.0, 0.0, 0.0];
        double[] direction = [1,0,0, 0,1,0, 0,0,1];
        double[] spacing   = [1.0, 1.0, 1.0];
        uint[]   size      = [10, 20, 30];

        var (_, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            origin, direction, spacing, size, 1.0, 1.0, 1.0);

        gridSize.Should().BeEquivalentTo(new uint[] { 10, 20, 30 });
    }

    [Fact]
    public void ComputeAABB_NonZeroOrigin_ShiftedCorrectly()
    {
        double[] origin    = [100.0, 200.0, -50.0];
        double[] direction = [1,0,0, 0,1,0, 0,0,1];
        double[] spacing   = [1.0, 1.0, 1.0];
        uint[]   size      = [5, 5, 5];

        var (aabbOrigin, _) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            origin, direction, spacing, size, 1.0, 1.0, 1.0);

        aabbOrigin.Should().BeEquivalentTo([100.0, 200.0, -50.0]);
    }

    // ── Tilt (45-degree rotation around Z) ───────────────────────────────────

    [Fact]
    public void ComputeAABB_TiltedDirection_AABBLargerThanInput()
    {
        double s = Math.Sqrt(2) / 2;
        double[] origin    = [0.0, 0.0, 0.0];
        double[] direction = [s, -s, 0,   s, s, 0,   0, 0, 1]; // 45° around Z
        double[] spacing   = [1.0, 1.0, 1.0];
        uint[]   size      = [10, 10, 10];

        var (_, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            origin, direction, spacing, size, 1.0, 1.0, 1.0);

        // Rotated bounding box is larger than the input
        gridSize[0].Should().BeGreaterThan(10);
        gridSize[1].Should().BeGreaterThan(10);
        gridSize[2].Should().Be(10); // Z unchanged by rotation around Z
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public void ComputeAABB_NullOrigin_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            null!, [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 1, 1, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeAABB_WrongOriginLength_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0.0, 0.0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 1, 1, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeAABB_ZeroOutputSpacing_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 0, 1, 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComputeAABB_NegativeOutputSpacingY_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 1, -1, 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComputeAABB_NegativeOutputSpacingZ_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 1, 1, -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComputeAABB_WrongDirectionLength_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0], [1,1,1], [10,10,10], 1, 1, 1); // only 6 elements

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeAABB_NullSpacing_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], null!, [10,10,10], 1, 1, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeAABB_NullSize_Throws()
    {
        Action act = () => VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], null!, 1, 1, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeAABB_SingleVoxelVolume_GridSizeIsOne()
    {
        var (_, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [1,1,1], 1.0, 1.0, 1.0);

        gridSize.Should().BeEquivalentTo(new uint[] { 1, 1, 1 });
    }

    [Fact]
    public void ComputeAABB_CoarserOutputSpacing_ProducesFewerVoxels()
    {
        // Input: 10x10x10 at 1 mm spacing; output at 2 mm → expect ~5 voxels per dim
        var (_, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], [1,0,0, 0,1,0, 0,0,1], [1,1,1], [10,10,10], 2.0, 2.0, 2.0);

        gridSize[0].Should().BeLessThan(10);
        gridSize[1].Should().BeLessThan(10);
        gridSize[2].Should().BeLessThan(10);
    }

    // ── Gantry tilt (rotation around X axis) ────────────────────────────────

    [Fact]
    public void ComputeAABB_GantryTiltAroundX_ZExtentEnlarged()
    {
        // 30-degree tilt around X: row stays [1,0,0]; col tilts in Y-Z
        double cos30 = Math.Cos(Math.PI / 6);
        double sin30 = Math.Sin(Math.PI / 6);
        // direction matrix: row=[1,0,0], col=[0,cos30,sin30], norm=[0,-sin30,cos30]
        double[] dir = [1, 0, 0,   0, cos30, sin30,   0, -sin30, cos30];

        var (_, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            [0,0,0], dir, [1,1,1], [10,10,10], 1.0, 1.0, 1.0);

        // Tilt mixes Y and Z, so the Z grid must be larger than the untilted 10 voxels
        gridSize[2].Should().BeGreaterThan(10);
    }
}
