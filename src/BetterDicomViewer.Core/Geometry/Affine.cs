namespace BetterDicomViewer.Core.Geometry;

/// <summary>
/// Affine transform p' = L * p + T stored as a 3x3 linear part (row-major) and a translation.
/// Columns of L are the images of the unit axes, which keeps DICOM index-to-patient matrices readable.
/// </summary>
public readonly struct Affine
{
    public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;
    public readonly double Tx, Ty, Tz;

    public Affine(double m00, double m01, double m02, double m10, double m11, double m12,
        double m20, double m21, double m22, double tx, double ty, double tz)
    {
        M00 = m00; M01 = m01; M02 = m02;
        M10 = m10; M11 = m11; M12 = m12;
        M20 = m20; M21 = m21; M22 = m22;
        Tx = tx; Ty = ty; Tz = tz;
    }

    public static Affine Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);

    public static Affine FromColumns(Vec3 c0, Vec3 c1, Vec3 c2, Vec3 translation) =>
        new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z, translation.X, translation.Y, translation.Z);

    public Vec3 Column0 => new(M00, M10, M20);
    public Vec3 Column1 => new(M01, M11, M21);
    public Vec3 Column2 => new(M02, M12, M22);
    public Vec3 Translation => new(Tx, Ty, Tz);

    public Vec3 TransformPoint(Vec3 p) => new(
        M00 * p.X + M01 * p.Y + M02 * p.Z + Tx,
        M10 * p.X + M11 * p.Y + M12 * p.Z + Ty,
        M20 * p.X + M21 * p.Y + M22 * p.Z + Tz);

    public Vec3 TransformVector(Vec3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    public double Determinant =>
        M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

    public Affine Inverse()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-18) throw new InvalidOperationException("Affine transform is singular.");
        var inv = 1.0 / det;
        var a00 = (M11 * M22 - M12 * M21) * inv;
        var a01 = (M02 * M21 - M01 * M22) * inv;
        var a02 = (M01 * M12 - M02 * M11) * inv;
        var a10 = (M12 * M20 - M10 * M22) * inv;
        var a11 = (M00 * M22 - M02 * M20) * inv;
        var a12 = (M02 * M10 - M00 * M12) * inv;
        var a20 = (M10 * M21 - M11 * M20) * inv;
        var a21 = (M01 * M20 - M00 * M21) * inv;
        var a22 = (M00 * M11 - M01 * M10) * inv;
        var tx = -(a00 * Tx + a01 * Ty + a02 * Tz);
        var ty = -(a10 * Tx + a11 * Ty + a12 * Tz);
        var tz = -(a20 * Tx + a21 * Ty + a22 * Tz);
        return new Affine(a00, a01, a02, a10, a11, a12, a20, a21, a22, tx, ty, tz);
    }

    /// <summary>Returns a * b, i.e. applies b first.</summary>
    public static Affine operator *(Affine a, Affine b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
        a.M00 * b.Tx + a.M01 * b.Ty + a.M02 * b.Tz + a.Tx,
        a.M10 * b.Tx + a.M11 * b.Ty + a.M12 * b.Tz + a.Ty,
        a.M20 * b.Tx + a.M21 * b.Ty + a.M22 * b.Tz + a.Tz);

    /// <summary>Row-major 4x4 representation used by the native bridge.</summary>
    public double[] ToRowMajor4x4() =>
    [
        M00, M01, M02, Tx,
        M10, M11, M12, Ty,
        M20, M21, M22, Tz,
        0, 0, 0, 1,
    ];

    public void CopyRowMajor4x4(Span<double> destination)
    {
        destination[0] = M00; destination[1] = M01; destination[2] = M02; destination[3] = Tx;
        destination[4] = M10; destination[5] = M11; destination[6] = M12; destination[7] = Ty;
        destination[8] = M20; destination[9] = M21; destination[10] = M22; destination[11] = Tz;
        destination[12] = 0; destination[13] = 0; destination[14] = 0; destination[15] = 1;
    }
}
