using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterDicomViewer.App.Services;

/// <summary>Captures a whole top-level window, including the title bar and system backdrop, with PrintWindow.</summary>
public static partial class WindowCapture
{
    private const uint PwRenderFullContent = 0x2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    public static void SavePng(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            PrintWindow(hwnd, mem, PwRenderFullContent);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
