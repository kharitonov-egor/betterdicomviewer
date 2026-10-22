using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Viewing;

public enum SlabMode
{
    None = 0,
    Mean = 1,
    Max = 2,
    Min = 3,
}

/// <summary>
/// A 2D viewport into world space. Screen pixel (x, y), measured in device pixels from the top-left corner,
/// maps to Center + (x - W/2) * Scale * Right + (y - H/2) * Scale * Down. Right and Down are orthonormal.
/// </summary>
public sealed class SliceView
{
    public Vec3 Center { get; set; }
    public Vec3 Right { get; set; } = Vec3.UnitX;
    public Vec3 Down { get; set; } = Vec3.UnitY;

    /// <summary>World units per device pixel.</summary>
    public double Scale { get; set; } = 1;

    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public double SlabThicknessMm { get; set; }
    public SlabMode SlabMode { get; set; }

    public Vec3 Normal => Vec3.Cross(Right, Down).Normalized();

    public SliceView Clone() => (SliceView)MemberwiseClone();

    public Vec3 ScreenToWorld(double x, double y) =>
        Center + Right * ((x - Width / 2.0) * Scale) + Down * ((y - Height / 2.0) * Scale);

    public (double X, double Y, double Depth) WorldToScreen(Vec3 p)
    {
        var d = p - Center;
        return (Vec3.Dot(d, Right) / Scale + Width / 2.0, Vec3.Dot(d, Down) / Scale + Height / 2.0, Vec3.Dot(d, Normal));
    }

    public void SetPlane(Vec3 right, Vec3 down)
    {
        var r = right.Normalized();
        var d = (down - r * Vec3.Dot(down, r)).Normalized();
        Right = r;
        Down = d;
    }

    /// <summary>Rotates the in-plane axes around the view normal (positive = clockwise on screen).</summary>
    public void RotateInPlane(double radians)
    {
        var n = Normal;
        SetPlane(Vec3.Rotate(Right, n, radians), Vec3.Rotate(Down, n, radians));
    }

    public void FlipHorizontal() => Right = -Right;
    public void FlipVertical() => Down = -Down;

    /// <summary>Zooms around a screen point so that the world point under it stays fixed.</summary>
    public void ZoomAt(double x, double y, double factor)
    {
        var anchor = ScreenToWorld(x, y);
        Scale /= factor;
        var moved = ScreenToWorld(x, y);
        Center += anchor - moved;
    }

    public void Pan(double dx, double dy) => Center -= Right * (dx * Scale) + Down * (dy * Scale);

    /// <summary>
    /// Row-major 4x4 matrix mapping output pixel indices (i, j, k) to input voxel indices for the native reslicer.
    /// Output pixel centres sit at screen coordinates (i + 0.5, j + 0.5); k steps along the normal by <paramref name="slabStep"/>.
    /// </summary>
    public double[] ResliceAxes(Affine worldToIndex, double slabStep = 1.0)
    {
        var c0 = worldToIndex.TransformVector(Right * Scale);
        var c1 = worldToIndex.TransformVector(Down * Scale);
        var c2 = worldToIndex.TransformVector(Normal * slabStep);
        var origin = worldToIndex.TransformPoint(ScreenToWorld(0.5, 0.5));
        return
        [
            c0.X, c1.X, c2.X, origin.X,
            c0.Y, c1.Y, c2.Y, origin.Y,
            c0.Z, c1.Z, c2.Z, origin.Z,
            0, 0, 0, 1,
        ];
    }

    /// <summary>Fits the given world-space extent into the viewport with a margin.</summary>
    public void FitTo(IEnumerable<Vec3> corners, Vec3 center, double margin = 1.04)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var c in corners)
        {
            var d = c - center;
            var x = Vec3.Dot(d, Right);
            var y = Vec3.Dot(d, Down);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        var w = Math.Max(maxX - minX, 1e-3);
        var h = Math.Max(maxY - minY, 1e-3);
        Scale = Math.Max(w / Math.Max(Width, 1), h / Math.Max(Height, 1)) * margin;
        var inPlaneCenter = center + Right * ((minX + maxX) / 2) + Down * ((minY + maxY) / 2);
        Center = inPlaneCenter + Normal * Vec3.Dot(Center - inPlaneCenter, Normal);
    }
}
