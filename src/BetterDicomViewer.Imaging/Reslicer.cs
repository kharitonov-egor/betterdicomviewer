using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Imaging.Native;

namespace BetterDicomViewer.Imaging;

public enum Interpolation
{
    Nearest = 0,
    Linear = 1,
    Cubic = 2,
}

/// <summary>Owns one native vtkImageReslice instance. Not thread-safe: use one per rendering thread and layer.</summary>
public sealed unsafe class Reslicer : IDisposable
{
    private IntPtr _handle;

    public Reslicer()
    {
        if (!NativeImaging.IsAvailable) throw new NativeImagingException($"Native imaging bridge unavailable: {NativeImaging.LoadError}");
        _handle = NativeMethods.bdv_reslicer_create();
        if (_handle == IntPtr.Zero) throw new NativeImagingException(NativeMethods.LastError());
    }

    /// <summary>Reslices <paramref name="source"/> through <paramref name="axes"/> into <paramref name="output"/> (width*height*components elements of <paramref name="outType"/>).</summary>
    public void Reslice(VoxelBuffer source, ReadOnlySpan<double> axes, int width, int height, Interpolation interpolation,
        int slabMode, int slabSlices, double background, void* output, VoxelType outType)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (axes.Length != 16) throw new ArgumentException("Expected a 4x4 matrix.", nameof(axes));
        fixed (double* a = axes)
        {
            var ok = NativeMethods.bdv_reslice(_handle, source.Pointer, source.Nx, source.Ny, source.Nz, source.Components, (int)source.Type,
                source.Version, a, width, height, (int)interpolation, slabMode, slabSlices, background, output, (int)outType);
            if (ok == 0) throw new NativeImagingException(NativeMethods.LastError());
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.bdv_reslicer_destroy(_handle);
        _handle = IntPtr.Zero;
    }
}
