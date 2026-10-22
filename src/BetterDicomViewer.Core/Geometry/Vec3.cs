using System.Globalization;
using System.Runtime.CompilerServices;

namespace BetterDicomViewer.Core.Geometry;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    public Vec3 Normalized()
    {
        var len = Length;
        return len > 1e-12 ? this / len : this;
    }

    public double this[int axis] => axis switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>Rotates <paramref name="v"/> around a unit <paramref name="axis"/> by <paramref name="radians"/> (Rodrigues).</summary>
    public static Vec3 Rotate(Vec3 v, Vec3 axis, double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return v * c + Cross(axis, v) * s + axis * (Dot(axis, v) * (1 - c));
    }

    public static double AngleBetween(Vec3 a, Vec3 b)
    {
        var la = a.Length;
        var lb = b.Length;
        if (la < 1e-12 || lb < 1e-12) return double.NaN;
        var cos = Math.Clamp(Dot(a, b) / (la * lb), -1.0, 1.0);
        return Math.Acos(cos);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Z:0.###})");
}
