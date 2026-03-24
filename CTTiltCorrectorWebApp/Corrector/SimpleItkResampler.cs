using FellowOakDicom;
using FellowOakDicom.Imaging;
using itk.simple;

namespace CTTiltCorrector.Corrector;

public static class SimpleItkResampler
{  
    public static Image Resample(IReadOnlyList<DicomSeriesLoader.SliceInfo> sortedSlices,
                          IProgress<string> progress,
                          double forcedSliceSpacingMm = 0)
    {
        if (sortedSlices == null || sortedSlices.Count == 0)
            throw new ArgumentException("sortedSlices must not be null or empty.");

        var ds0 = sortedSlices[0].Dataset;

        // ── 1. Read geometry from DICOM tags ───────────────────────────────
        var ipp0 = ds0.GetValues<double>(DicomTag.ImagePositionPatient);
        var iopVals = ds0.GetValues<double>(DicomTag.ImageOrientationPatient);
        var pixSpac = ds0.GetValues<double>(DicomTag.PixelSpacing);  // [rowSpacing, colSpacing]

        int nx = ds0.GetSingleValue<int>(DicomTag.Columns);
        int ny = ds0.GetSingleValue<int>(DicomTag.Rows);
        int nz = sortedSlices.Count;

        // IOP row and col direction cosines
        double[] rowDir = { iopVals[0], iopVals[1], iopVals[2] };
        double[] colDir = { iopVals[3], iopVals[4], iopVals[5] };
        double[] normal = OrientationHelper.SliceNormal(rowDir, colDir);

        // Pixel spacing: PixelSpacing = [row spacing, col spacing]
        // = [Y step per row, X step per col] in physical mm
        double sx = pixSpac[1];  // X: step along row direction per column
        double sy = pixSpac[0];  // Y: step along col direction per row

        // Slice spacing: true centre-to-centre gap along the tilt normal
        double sz = forcedSliceSpacingMm > 0
            ? forcedSliceSpacingMm
            : Math.Abs(sortedSlices[1].SlicePosition - sortedSlices[0].SlicePosition);

        // ── 2. Build ITK direction matrix ──────────────────────────────────
        //
        // ITK direction matrix columns are the unit vectors of each voxel axis
        // in physical LPS space, stored row-major:
        //
        //   col 0 = row direction   (X axis of image)
        //   col 1 = col direction   (Y axis of image)
        //   col 2 = slice normal    (Z axis of image)
        //
        // Row-major storage:
        //   [ rowDir.x  colDir.x  normal.x ]
        //   [ rowDir.y  colDir.y  normal.y ]
        //   [ rowDir.z  colDir.z  normal.z ]
        double[] direction =
        {
        rowDir[0], colDir[0], normal[0],
        rowDir[1], colDir[1], normal[1],
        rowDir[2], colDir[2], normal[2]
        };

        // ── 3. Apply RescaleSlope/Intercept and build pixel buffer ─────────
        //
        // ImageSeriesReader automatically applies the DICOM rescale transform
        // so the ITK image is in HU. We must do the same here so the
        // ResampleImageFilter and Writer see identical values.
        double rescaleSlope = ds0.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        double rescaleIntercept = ds0.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
        bool isSigned = ds0.GetSingleValueOrDefault(DicomTag.PixelRepresentation, (ushort)1) == 1;

        // Output buffer is float64 (same as sitkFloat64 that ImageSeriesReader produces)
        double[] volumeBuffer = new double[(long)nx * ny * nz];

        for (int z = 0; z < nz; z++)
        {
            var pixelData = DicomPixelData.Create(sortedSlices[z].Dataset);
            var frameData = pixelData.GetFrame(0);
            byte[] raw = frameData.Data;

            int sliceOffset = z * nx * ny;
            for (int i = 0; i < nx * ny; i++)
            {
                short stored = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                volumeBuffer[sliceOffset + i] = stored * rescaleSlope + rescaleIntercept;
            }
        }

        // ── 4. Import buffer into ITK as a float64 volume ─────────────────
        Image src;
        unsafe
        {
            fixed (double* ptr = volumeBuffer)
            {
                var importFilter = new ImportImageFilter();
                importFilter.SetSize(new VectorUInt32(new uint[] { (uint)nx, (uint)ny, (uint)nz }));
                importFilter.SetSpacing(new VectorDouble(new double[] { sx, sy, sz }));
                importFilter.SetOrigin(new VectorDouble(new double[] { ipp0[0], ipp0[1], ipp0[2] }));
                importFilter.SetDirection(new VectorDouble(direction));
                importFilter.SetBufferAsDouble((nint)ptr, (uint)((long)nx * ny * nz));
                src = importFilter.Execute();
            }
        }

        // ── 5. Report what ITK loaded ────────────────
        VectorDouble srcSpacing = src.GetSpacing();
        VectorUInt32 srcSize = src.GetSize();
        VectorDouble srcOrigin = src.GetOrigin();
        VectorDouble srcDir = src.GetDirection();

        progress.Report($"[ITK] Input size     : {srcSize[0]} x {srcSize[1]} x {srcSize[2]}");
        progress.Report($"[ITK] Input spacing  : {srcSpacing[0]:F4} x {srcSpacing[1]:F4} x {srcSpacing[2]:F4} mm");
        progress.Report($"[ITK] Input origin   : ({srcOrigin[0]:F3}, {srcOrigin[1]:F3}, {srcOrigin[2]:F3})");
        progress.Report($"[ITK] Input dir[0]   : ({srcDir[0]:F6}, {srcDir[1]:F6}, {srcDir[2]:F6})");
        progress.Report($"[ITK] Input dir[1]   : ({srcDir[3]:F6}, {srcDir[4]:F6}, {srcDir[5]:F6})");
        progress.Report($"[ITK] Input dir[2]   : ({srcDir[6]:F6}, {srcDir[7]:F6}, {srcDir[8]:F6})");

        // ── 6. Choose output spacing ────────────────
        
        double outSx = srcSpacing[0];
        double outSy = srcSpacing[1];
        double outSz = (forcedSliceSpacingMm > 0) ? forcedSliceSpacingMm : srcSpacing[2];

        // ── 4. Compute output bounding box ─────────────────────────────────
            //
            // The tilted input volume's axis-aligned bounding box (AABB) defines
            // the output extent.  We pass the full input geometry so that no
            // anatomy is clipped when the gantry tilt rotates corner voxels
            // outside the original X/Y footprint.
        var (aabbOrigin, gridSize) = VolumeGeometry.ComputeAxisAlignedBoundingBox(
            origin: new[] { srcOrigin[0], srcOrigin[1], srcOrigin[2] },
            direction: new[]
            {
            srcDir[0], srcDir[1], srcDir[2],
            srcDir[3], srcDir[4], srcDir[5],
            srcDir[6], srcDir[7], srcDir[8]
            },
            spacing: new[] { srcSpacing[0], srcSpacing[1], srcSpacing[2] },
            size: new[] { srcSize[0], srcSize[1], srcSize[2] },
            outSx: outSx, outSy: outSy, outSz: outSz);

        var outOrigin = new VectorDouble(aabbOrigin);
        var outSize = new VectorUInt32(gridSize);

        progress.Report($"[ITK] Output size    : {outSize[0]} x {outSize[1]} x {outSize[2]}");
        progress.Report($"[ITK] Output origin  : ({outOrigin[0]:F3}, {outOrigin[1]:F3}, {outOrigin[2]:F3})");

        // ── 5. Identity direction ──────────────────────────────────────────
        //
        // The output volume is resampled into standard HFS axial orientation:
        // X → left-to-right, Y → posterior-to-anterior, Z → inferior-to-superior.
        var identityDir = new VectorDouble(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });

        // ── 6. Resample ────────────────────────────────────────────────────
        //
        // Linear interpolation is used to avoid aliasing artefacts at the
        // resampled voxel boundaries.  The default pixel value is set to the
        // corner voxel of the source volume (typically air, ~ -1024 HU) so
        // that regions outside the original FOV are filled with a clinically
        // neutral background rather than zero (which would be water density).

        double bgValue = src.GetPixelAsDouble(new VectorUInt32(new uint[] { 0, 0, 0 }));

        var resample = new ResampleImageFilter();
        resample.SetOutputDirection(identityDir);
        resample.SetOutputOrigin(outOrigin);
        resample.SetOutputSpacing(new VectorDouble(new double[] { outSx, outSy, outSz }));
        resample.SetSize(outSize);
        resample.SetInterpolator(InterpolatorEnum.sitkLinear);
        resample.SetDefaultPixelValue(bgValue);

        progress.Report("[ITK] Resampling ....");

        Image result = resample.Execute(src);
        src.Dispose();

        VectorDouble resDir = result.GetDirection();
        progress.Report($"[ITK] Result dir[0]  : ({resDir[0]:F6}, {resDir[1]:F6}, {resDir[2]:F6})");
        progress.Report($"[ITK] Result dir[1]  : ({resDir[3]:F6}, {resDir[4]:F6}, {resDir[5]:F6})");
        progress.Report($"[ITK] Result dir[2]  : ({resDir[6]:F6}, {resDir[7]:F6}, {resDir[8]:F6})");
        progress.Report("[ITK] Resampling complete.");

        return result;
    }
}