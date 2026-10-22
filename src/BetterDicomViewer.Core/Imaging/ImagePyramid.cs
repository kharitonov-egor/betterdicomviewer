using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Imaging;

/// <summary>
/// 2x2 box-filtered levels for large 2D images, so a radiograph shown smaller than its native size is resampled from
/// a matching level instead of aliasing. Level L covers 2^L source pixels per pixel.
/// </summary>
public static class ImagePyramid
{
    public const int MinDimensionForPyramid = 1536;

    public static VoxelBuffer Downsample(VoxelBuffer src)
    {
        var w = Math.Max(1, src.Nx / 2);
        var h = Math.Max(1, src.Ny / 2);
        var c = src.Components;
        var dst = new VoxelBuffer(w, h, 1, c, src.Type);
        switch (src.Type)
        {
            case VoxelType.UInt8: Box<byte>(src, dst, v => v, v => (byte)Math.Round(v)); break;
            case VoxelType.Int16: Box<short>(src, dst, v => v, v => (short)Math.Round(v)); break;
            case VoxelType.UInt16: Box<ushort>(src, dst, v => v, v => (ushort)Math.Round(v)); break;
            case VoxelType.Int32: Box<int>(src, dst, v => v, v => (int)Math.Round(v)); break;
            case VoxelType.Float32: Box<float>(src, dst, v => v, v => (float)v); break;
        }
        dst.MarkModified();
        return dst;
    }

    private static void Box<T>(VoxelBuffer src, VoxelBuffer dst, Func<T, double> read, Func<double, T> write) where T : unmanaged
    {
        var s = src.SliceSpan<T>(0).ToArray();
        var d = dst.SliceSpan<T>(0);
        var c = src.Components;
        var sw = src.Nx;
        var sh = src.Ny;
        for (var y = 0; y < dst.Ny; y++)
        for (var x = 0; x < dst.Nx; x++)
        for (var k = 0; k < c; k++)
        {
            var x0 = Math.Min(2 * x, sw - 1);
            var x1 = Math.Min(2 * x + 1, sw - 1);
            var y0 = Math.Min(2 * y, sh - 1);
            var y1 = Math.Min(2 * y + 1, sh - 1);
            var sum = read(s[(y0 * sw + x0) * c + k]) + read(s[(y0 * sw + x1) * c + k]) + read(s[(y1 * sw + x0) * c + k]) + read(s[(y1 * sw + x1) * c + k]);
            d[(y * dst.Nx + x) * c + k] = write(sum / 4);
        }
    }

    /// <summary>Index-to-world transform of level <paramref name="level"/> given the full-resolution transform.</summary>
    public static Affine LevelToWorld(Affine baseIndexToWorld, int level)
    {
        var s = (double)(1 << level);
        var origin = baseIndexToWorld.TransformPoint(new Vec3((s - 1) / 2, (s - 1) / 2, 0));
        return Affine.FromColumns(baseIndexToWorld.Column0 * s, baseIndexToWorld.Column1 * s, baseIndexToWorld.Column2, origin);
    }
}
