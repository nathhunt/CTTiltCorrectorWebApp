namespace CTTiltCorrector.Corrector;

/// <summary>
/// Helpers for DICOM image orientation (patient) calculations.
/// Image Orientation Patient tag (0020,0037) = [Xx, Xy, Xz, Yx, Yy, Yz]
/// where X is the row direction cosine and Y is the column direction cosine.
/// </summary>
public static class OrientationHelper
{
    /// <summary>
    /// Parse the six-element ImageOrientationPatient string into two 3-vectors.
    /// </summary>
    public static (double[] row, double[] col) ParseIOP(string iop)
    {
        var parts = iop.Split('\\');
        if (parts.Length != 6)
            throw new FormatException($"Expected 6 values in ImageOrientationPatient, got: {iop}");

        double[] row = { double.Parse(parts[0]), double.Parse(parts[1]), double.Parse(parts[2]) };
        double[] col = { double.Parse(parts[3]), double.Parse(parts[4]), double.Parse(parts[5]) };
        return (row, col);
    }

    /// <summary>
    /// Compute the normalised slice normal from IOP (row × col).
    /// </summary>
    public static double[] SliceNormal(double[] row, double[] col)
    {
        double[] n = new double[]
        {
            row[1]*col[2] - row[2]*col[1],
            row[2]*col[0] - row[0]*col[2],
            row[0]*col[1] - row[1]*col[0]
        };
        double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        if (len < 1e-10) throw new InvalidOperationException("Cannot normalise zero vector.");
        return new double[] { n[0] / len, n[1] / len, n[2] / len };
    }

    /// <summary>Dot product of two 3-vectors.</summary>
    public static double Dot(double[] a, double[] b)
        => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}