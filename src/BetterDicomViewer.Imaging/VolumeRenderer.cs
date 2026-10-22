using System.Runtime.InteropServices;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Imaging.Native;

namespace BetterDicomViewer.Imaging;

public enum BlendMode
{
    Composite = 0,
    MaximumIntensity = 1,
    MinimumIntensity = 2,
    AverageIntensity = 3,
}

public sealed record TransferFunction(
    IReadOnlyList<(double X, double R, double G, double B)> Colors,
    IReadOnlyList<(double X, double A)> Opacity,
    IReadOnlyList<(double X, double A)> Gradient,
    BlendMode Blend,
    bool Shade,
    double Ambient = 0.2,
    double Diffuse = 0.8,
    double Specular = 0.25,
    double SpecularPower = 20);

/// <summary>
/// Offscreen GPU ray caster (VTK) returning BGRA frames. All calls must come from the same thread,
/// because the OpenGL context is bound to the thread that created it.
/// </summary>
public sealed unsafe class VolumeRenderer : IDisposable
{
    private IntPtr _handle;

    public VolumeRenderer()
    {
        if (!NativeImaging.IsAvailable) throw new NativeImagingException($"Native imaging bridge unavailable: {NativeImaging.LoadError}");
        _handle = NativeMethods.bdv_vr_create();
        if (_handle == IntPtr.Zero) throw new NativeImagingException(NativeMethods.LastError());
    }

    public string RendererInfo => Marshal.PtrToStringUTF8(NativeMethods.bdv_vr_renderer_info(_handle)) ?? "";

    public void SetVolume(VoxelBuffer data, Affine indexToWorld)
    {
        Span<double> m = stackalloc double[16];
        indexToWorld.CopyRowMajor4x4(m);
        fixed (double* mp = m)
            Check(NativeMethods.bdv_vr_set_volume(_handle, data.Pointer, data.Nx, data.Ny, data.Nz, (int)data.Type, data.Version, mp));
    }

    public void ClearVolume() => Check(NativeMethods.bdv_vr_clear_volume(_handle));

    public void SetTransfer(TransferFunction tf, double sampleDistance)
    {
        var colors = tf.Colors.SelectMany(c => new[] { c.X, c.R, c.G, c.B }).ToArray();
        var opacity = tf.Opacity.SelectMany(o => new[] { o.X, o.A }).ToArray();
        var gradient = tf.Gradient.SelectMany(o => new[] { o.X, o.A }).ToArray();
        fixed (double* c = colors)
        fixed (double* o = opacity)
        fixed (double* g = gradient)
        {
            Check(NativeMethods.bdv_vr_set_transfer(_handle, c, tf.Colors.Count, o, tf.Opacity.Count, g, tf.Gradient.Count,
                (int)tf.Blend, tf.Shade ? 1 : 0, tf.Ambient, tf.Diffuse, tf.Specular, tf.SpecularPower, sampleDistance));
        }
    }

    public void SetCamera(Vec3 position, Vec3 focal, Vec3 up, double viewAngle, bool parallel, double parallelScale)
    {
        double* p = stackalloc double[] { position.X, position.Y, position.Z };
        double* f = stackalloc double[] { focal.X, focal.Y, focal.Z };
        double* u = stackalloc double[] { up.X, up.Y, up.Z };
        Check(NativeMethods.bdv_vr_set_camera(_handle, p, f, u, viewAngle, parallel ? 1 : 0, parallelScale));
    }

    /// <summary>Clip planes as (origin, normal) pairs; the half-space the normal points into is kept.</summary>
    public void SetClipPlanes(IReadOnlyList<(Vec3 Origin, Vec3 Normal)> planes)
    {
        var data = planes.SelectMany(p => new[] { p.Origin.X, p.Origin.Y, p.Origin.Z, p.Normal.X, p.Normal.Y, p.Normal.Z }).ToArray();
        fixed (double* d = data) Check(NativeMethods.bdv_vr_set_clip_planes(_handle, d, planes.Count));
    }

    public void SetBackground(double r, double g, double b) => Check(NativeMethods.bdv_vr_set_background(_handle, r, g, b));

    public void SetLabels(VoxelBuffer? labels, Affine indexToWorld, IReadOnlyList<(double R, double G, double B, double A)> colors, int smoothIterations)
    {
        Span<double> m = stackalloc double[16];
        indexToWorld.CopyRowMajor4x4(m);
        var rgba = colors.SelectMany(c => new[] { c.R, c.G, c.B, c.A }).ToArray();
        fixed (double* mp = m)
        fixed (double* cp = rgba)
        {
            if (labels is null)
                Check(NativeMethods.bdv_vr_set_labels(_handle, null, 0, 0, 0, 0, mp, cp, 0, smoothIterations));
            else
                Check(NativeMethods.bdv_vr_set_labels(_handle, (byte*)labels.Pointer, labels.Nx, labels.Ny, labels.Nz, labels.Version, mp, cp, colors.Count, smoothIterations));
        }
    }

    public void Render(int width, int height, bool interactive, uint[] output)
    {
        if (output.Length < width * height) throw new ArgumentException("Output buffer too small.", nameof(output));
        fixed (uint* o = output) Check(NativeMethods.bdv_vr_render(_handle, width, height, interactive ? 1 : 0, o));
    }

    public Vec3? Pick(int x, int y, int width, int height)
    {
        double* w = stackalloc double[3];
        return NativeMethods.bdv_vr_pick(_handle, x, y, width, height, w) != 0 ? new Vec3(w[0], w[1], w[2]) : null;
    }

    private static void Check(int ok)
    {
        if (ok == 0) throw new NativeImagingException(NativeMethods.LastError());
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.bdv_vr_destroy(_handle);
        _handle = IntPtr.Zero;
    }
}

public static class SurfaceExporter
{
    /// <summary>Extracts one label as a smoothed surface in patient coordinates (mm) and writes STL (binary) or OBJ.</summary>
    public static unsafe void Export(VoxelBuffer labels, Affine indexToWorld, byte label, string path, bool obj, int smoothIterations = 15)
    {
        if (!NativeImaging.IsAvailable) throw new NativeImagingException($"Native imaging bridge unavailable: {NativeImaging.LoadError}");
        Span<double> m = stackalloc double[16];
        indexToWorld.CopyRowMajor4x4(m);
        fixed (double* mp = m)
        {
            if (NativeMethods.bdv_export_label_surface((byte*)labels.Pointer, labels.Nx, labels.Ny, labels.Nz, mp, label, smoothIterations, path, obj ? 1 : 0) == 0)
                throw new NativeImagingException(NativeMethods.LastError());
        }
    }
}
