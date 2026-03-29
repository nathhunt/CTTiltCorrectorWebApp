using itk.simple;
using System.Runtime.InteropServices;

namespace CTTiltCorrector.Corrector;

/// <summary>
/// Low-level helpers used by <see cref="DicomSeriesWriter"/> when converting
/// resampled ITK slices back into DICOM datasets.
/// </summary>
public static class DicomWriterHelpers
{
    /// <summary>
    /// Copies the raw pixel buffer from a 2-D ITK <see cref="Image"/> into a
    /// managed byte array via <c>Marshal.Copy</c>.
    /// </summary>
    /// <param name="plane">A single 2-D slice extracted from the resampled volume.</param>
    /// <param name="byteCount">Expected byte count: <c>nx * ny * 2</c> (16-bit pixels).</param>
    /// <param name="isSigned">
    ///   <see langword="true"/> for signed int16 pixels (PixelRepresentation = 1);
    ///   <see langword="false"/> for unsigned uint16 (PixelRepresentation = 0).
    /// </param>
    /// <returns>Raw pixel bytes in little-endian order, ready for a DICOM OW element.</returns>
    public static byte[] CopyPixelBuffer(Image plane, int byteCount, bool isSigned)
    {
        IntPtr ptr = isSigned ? plane.GetBufferAsInt16() : plane.GetBufferAsUInt16();
        byte[] bytes = new byte[byteCount];
        Marshal.Copy(ptr, bytes, 0, byteCount);
        return bytes;
    }

    /// <summary>
    /// Returns the reference slice whose <see cref="DicomSeriesLoader.SliceInfo.SlicePosition"/>
    /// (tilt-normal projection of IPP) is closest to <paramref name="targetPosition"/>.
    /// </summary>
    /// <remarks>
    /// Matching on the tilt-normal projection rather than raw IPP-Z ensures
    /// correct tag inheritance when the source acquisition has gantry tilt.
    /// </remarks>
    /// <param name="targetPosition">Target position along the tilt normal (mm).</param>
    /// <param name="referenceSlices">
    ///   Sorted reference slices from the original series. Must be non-empty.
    /// </param>
    public static DicomSeriesLoader.SliceInfo FindNearestReferenceSlice(
        double targetPosition,
        DicomSeriesLoader.SliceInfo[] referenceSlices)
    {
        DicomSeriesLoader.SliceInfo best = referenceSlices[0];
        double bestDist = double.MaxValue;

        foreach (var s in referenceSlices)
        {
            // Match on the tilt-normal projection, not on raw IPP Z.
            double dist = Math.Abs(s.SlicePosition - targetPosition);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = s;
            }
        }
        return best;
    }

    /// <summary>
    /// Checks whether the resampled volume's direction matrix is identity
    /// (tolerance 1e-5) and reports a warning via <paramref name="progress"/>
    /// if it is not. A non-identity result indicates a SimpleITK configuration
    /// error and would produce incorrect ImageOrientationPatient tags.
    /// </summary>
    public static void VerifyIdentityDirection(VectorDouble dir, IProgress<string> progress)
    {
        bool ok = Math.Abs(dir[0] - 1) < 1e-5 && Math.Abs(dir[1]) < 1e-5 && Math.Abs(dir[2]) < 1e-5
               && Math.Abs(dir[3]) < 1e-5 && Math.Abs(dir[4] - 1) < 1e-5 && Math.Abs(dir[5]) < 1e-5
               && Math.Abs(dir[6]) < 1e-5 && Math.Abs(dir[7]) < 1e-5 && Math.Abs(dir[8] - 1) < 1e-5;

        if (!ok)
            progress.Report($"[Writer] WARNING: resampled direction is NOT identity!");
    }
}