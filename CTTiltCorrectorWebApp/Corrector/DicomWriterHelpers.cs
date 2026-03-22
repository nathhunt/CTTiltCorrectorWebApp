using itk.simple;
using System.Runtime.InteropServices;

namespace CTTiltCorrector.Corrector;

public static class DicomWriterHelpers
{
    public static byte[] CopyPixelBuffer(Image plane, int byteCount, bool isSigned)
    {
        IntPtr ptr = isSigned ? plane.GetBufferAsInt16() : plane.GetBufferAsUInt16();
        byte[] bytes = new byte[byteCount];
        Marshal.Copy(ptr, bytes, 0, byteCount);
        return bytes;
    }

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

    public static void VerifyIdentityDirection(VectorDouble dir)
    {
        bool ok = Math.Abs(dir[0] - 1) < 1e-5 && Math.Abs(dir[1]) < 1e-5 && Math.Abs(dir[2]) < 1e-5
               && Math.Abs(dir[3]) < 1e-5 && Math.Abs(dir[4] - 1) < 1e-5 && Math.Abs(dir[5]) < 1e-5
               && Math.Abs(dir[6]) < 1e-5 && Math.Abs(dir[7]) < 1e-5 && Math.Abs(dir[8] - 1) < 1e-5;

        if (!ok)
            Console.WriteLine($"[Writer] WARNING: resampled direction is NOT identity!");
    }
}