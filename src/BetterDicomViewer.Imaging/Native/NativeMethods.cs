using System.Runtime.InteropServices;

namespace BetterDicomViewer.Imaging.Native;

internal static partial class NativeMethods
{
    private const string Lib = "bdv_vtk";

    [LibraryImport(Lib)]
    internal static partial IntPtr bdv_version();

    [LibraryImport(Lib)]
    internal static partial IntPtr bdv_last_error();

    [LibraryImport(Lib)]
    internal static partial void bdv_initialize(int threads);

    [LibraryImport(Lib)]
    internal static partial IntPtr bdv_reslicer_create();

    [LibraryImport(Lib)]
    internal static partial void bdv_reslicer_destroy(IntPtr reslicer);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_reslice(IntPtr reslicer,
        void* data, int nx, int ny, int nz, int components, int scalarType, long dataVersion,
        double* axesRowMajor16,
        int outWidth, int outHeight,
        int interpolation, int slabMode, int slabSlices,
        double background,
        void* output, int outScalarType);

    [LibraryImport(Lib)]
    internal static partial IntPtr bdv_vr_create();

    [LibraryImport(Lib)]
    internal static partial void bdv_vr_destroy(IntPtr vr);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_set_volume(IntPtr vr, void* data, int nx, int ny, int nz, int scalarType, long dataVersion, double* indexToWorld16);

    [LibraryImport(Lib)]
    internal static partial int bdv_vr_clear_volume(IntPtr vr);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_set_transfer(IntPtr vr,
        double* colorPoints, int colorCount,
        double* opacityPoints, int opacityCount,
        double* gradientPoints, int gradientCount,
        int blendMode, int shade,
        double ambient, double diffuse, double specular, double specularPower,
        double sampleDistance);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_set_camera(IntPtr vr, double* position, double* focalPoint, double* viewUp, double viewAngle, int parallel, double parallelScale);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_set_clip_planes(IntPtr vr, double* planes, int planeCount);

    [LibraryImport(Lib)]
    internal static partial int bdv_vr_set_background(IntPtr vr, double r, double g, double b);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_set_labels(IntPtr vr, byte* labels, int nx, int ny, int nz, long version, double* indexToWorld16, double* rgba, int labelCount, int smoothIterations);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_render(IntPtr vr, int width, int height, int interactive, void* outBgra);

    [LibraryImport(Lib)]
    internal static unsafe partial int bdv_vr_pick(IntPtr vr, int x, int y, int width, int height, double* outWorld3);

    [LibraryImport(Lib)]
    internal static partial IntPtr bdv_vr_renderer_info(IntPtr vr);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int bdv_export_label_surface(byte* labels, int nx, int ny, int nz, double* indexToWorld16, int label, int smoothIterations, string pathUtf8, int format);

    internal static string LastError() => Marshal.PtrToStringUTF8(bdv_last_error()) ?? "Unknown native error";
}

public sealed class NativeImagingException(string message) : Exception(message);

public static class NativeImaging
{
    private static bool? _available;
    private static string? _loadError;

    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeMethods.bdv_initialize(0);
                _available = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _loadError = ex.Message;
                _available = false;
            }
            return _available.Value;
        }
    }

    public static string? LoadError => _loadError;

    public static string Version => IsAvailable ? Marshal.PtrToStringUTF8(NativeMethods.bdv_version()) ?? "?" : "unavailable";
}
