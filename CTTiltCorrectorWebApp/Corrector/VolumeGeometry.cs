namespace CTTiltCorrector.Corrector;

/// <summary>
/// Pure geometry helpers for working with 3-D image volumes in LPS space.
/// All methods are stateless and dependency-free so they can be exercised
/// directly from unit tests without any SimpleITK or DICOM infrastructure.
/// </summary>
public static class VolumeGeometry
{
    /// <summary>
    /// Compute the axis-aligned bounding box (AABB) of a source volume that
    /// may have an arbitrary direction matrix, then derive the output grid
    /// parameters needed to resample it into identity (LPS-aligned) space.
    /// </summary>
    /// <remarks>
    /// The physical position of voxel (i, j, k) is:
    ///   p = origin + D * [sx·i, sy·j, sz·k]ᵀ
    /// where D is the 3×3 row-major direction matrix and sx/sy/sz are the
    /// per-axis voxel spacings.
    ///
    /// We evaluate p at all 8 corners of the volume and take the per-axis
    /// min/max to obtain the LPS-aligned AABB, from which we derive the
    /// output origin and the number of voxels needed in each dimension.
    /// </remarks>
    /// <param name="origin">
    ///   LPS coordinate of voxel index (0, 0, 0) — 3-element array.
    /// </param>
    /// <param name="direction">
    ///   Row-major 3×3 direction matrix — 9-element array
    ///   (D[row*3 + col]).
    /// </param>
    /// <param name="spacing">
    ///   Voxel spacing in mm — 3-element array [sx, sy, sz].
    /// </param>
    /// <param name="size">
    ///   Voxel count — 3-element array [Nx, Ny, Nz].
    /// </param>
    /// <param name="outSx">Output X voxel spacing (mm).</param>
    /// <param name="outSy">Output Y voxel spacing (mm).</param>
    /// <param name="outSz">Output Z voxel spacing (mm).</param>
    /// <returns>
    ///   <c>aabbOrigin</c>: LPS coordinate of the min-corner of the AABB
    ///   (becomes the origin of the identity-direction output grid).<br/>
    ///   <c>gridSize</c>: Number of voxels in each dimension required to
    ///   cover the AABB at the requested output spacing.
    /// </returns>
    public static (double[] aabbOrigin, uint[] gridSize) ComputeAxisAlignedBoundingBox(
        double[] origin,
        double[] direction,
        double[] spacing,
        uint[] size,
        double outSx,
        double outSy,
        double outSz)
    {
        if (origin == null || origin.Length != 3) throw new ArgumentException("origin must have 3 elements.", nameof(origin));
        if (direction == null || direction.Length != 9) throw new ArgumentException("direction must have 9 elements.", nameof(direction));
        if (spacing == null || spacing.Length != 3) throw new ArgumentException("spacing must have 3 elements.", nameof(spacing));
        if (size == null || size.Length != 3) throw new ArgumentException("size must have 3 elements.", nameof(size));
        if (outSx <= 0) throw new ArgumentOutOfRangeException(nameof(outSx), "Output spacing must be > 0.");
        if (outSy <= 0) throw new ArgumentOutOfRangeException(nameof(outSy), "Output spacing must be > 0.");
        if (outSz <= 0) throw new ArgumentOutOfRangeException(nameof(outSz), "Output spacing must be > 0.");

        // Physical extents along each source axis (in mm)
        double ex = (size[0] - 1) * spacing[0];
        double ey = (size[1] - 1) * spacing[1];
        double ez = (size[2] - 1) * spacing[2];

        // Unpack the row-major 3×3 direction matrix
        double[,] D = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                D[r, c] = direction[r * 3 + c];

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        // Walk all 8 corners: cx ∈ {0, ex}, cy ∈ {0, ey}, cz ∈ {0, ez}
        foreach (double cx in new[] { 0.0, ex })
            foreach (double cy in new[] { 0.0, ey })
                foreach (double cz in new[] { 0.0, ez })
                {
                    double px = origin[0] + D[0, 0] * cx + D[0, 1] * cy + D[0, 2] * cz;
                    double py = origin[1] + D[1, 0] * cx + D[1, 1] * cy + D[1, 2] * cz;
                    double pz = origin[2] + D[2, 0] * cx + D[2, 1] * cy + D[2, 2] * cz;

                    if (px < minX) minX = px; if (px > maxX) maxX = px;
                    if (py < minY) minY = py; if (py > maxY) maxY = py;
                    if (pz < minZ) minZ = pz; if (pz > maxZ) maxZ = pz;
                }

        double[] aabbOrigin = { minX, minY, minZ };

        uint[] gridSize =
        {
            (uint)Math.Floor((maxX - minX) / outSx) + 1,
            (uint)Math.Floor((maxY - minY) / outSy) + 1,
            (uint)Math.Floor((maxZ - minZ) / outSz) + 1
        };

        return (aabbOrigin, gridSize);
    }
}
