using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App.Viewer;

/// <summary>
/// One image viewport. 2D frames are rendered at device-pixel resolution into a WriteableBitmap; vector overlays
/// (labels, measurements, reference lines) are drawn on top in a DrawingVisual. 3D frames come from a VTK thread.
/// </summary>
public sealed partial class ViewportControl : Grid, IDisposable
{
    private readonly IViewerHost _host;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly OverlayHost _overlay = new();
    private readonly Border _frame = new();
    private WriteableBitmap? _bitmap;
    private SliceRenderer? _renderer;
    private VolumeRenderService? _vr;
    private int? _pinnedFrame;
    private string? _renderError;
    private bool _vrInteractive;
    private DateTime _lastVrInteraction;

    public ViewportControl(IViewerHost host, int index)
    {
        _host = host;
        Model = new ViewportModel(index);
        Model.Changed += _ => RequestRender();
        Background = (Brush)Application.Current.Resources["ViewportBackground"];
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Children.Add(_image);
        Children.Add(_overlay);
        _frame.BorderThickness = new Thickness(1);
        _frame.BorderBrush = (Brush)Application.Current.Resources["ViewportBorder"];
        _frame.IsHitTestVisible = false;
        Children.Add(_frame);
        Cursor = System.Windows.Input.Cursors.Cross;
        AutomationProperties_SetName($"Viewport {index + 1}");
    }

    private void AutomationProperties_SetName(string name) =>
        System.Windows.Automation.AutomationProperties.SetName(this, name);

    public ViewportModel Model { get; }
    public double LastRenderMs { get; private set; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            _frame.BorderThickness = new Thickness(value ? 2 : 1);
            _frame.BorderBrush = value
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["ViewportBorder"];
        }
    }

    private bool _isActive;

    public void RequestRender() => RenderScheduler.Enqueue(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var (w, h) = DevicePixelSize();
        var view = Model.View;
        if (view.Width > 1 && view.Height > 1 && w > 1 && h > 1)
        {
            // Keep the same field of view when the viewport is resized.
            var oldExtent = Math.Min(view.Width, view.Height) * view.Scale;
            view.Width = w;
            view.Height = h;
            view.Scale = oldExtent / Math.Min(w, h);
        }
        else
        {
            view.Width = w;
            view.Height = h;
            if (Model.HasContent) Model.Fit();
        }
        RequestRender();
    }

    public (int W, int H) DevicePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Max(1, Math.Round(ActualWidth * dpi.DpiScaleX)), (int)Math.Max(1, Math.Round(ActualHeight * dpi.DpiScaleY)));
    }

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    internal void RenderNow()
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (Model.Is3D && Model.Volume is not null) Render3D();
            else Render2D();
            _renderError = null;
        }
        catch (Exception ex)
        {
            _renderError = ex.Message;
        }
        LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        DrawOverlay();
    }

    private void EnsureBitmap(int w, int h)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _bitmap = new WriteableBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
    }

    private unsafe void Render2D()
    {
        var (w, h) = DevicePixelSize();
        EnsureBitmap(w, h);
        var view = Model.View;
        view.Width = w;
        view.Height = h;
        if (Model.Set is null)
        {
            ClearBitmap();
            return;
        }

        BaseLayer? layer = null;
        if (Model.Volume is { } volume)
        {
            layer = new BaseLayer
            {
                Data = volume.Data,
                WorldToIndex = volume.WorldToIndex,
                Interpolation = Model.Interpolation,
                Display = Model.Display,
                Background = volume.ValueUnit == "HU" ? -1024 : Math.Min(0, volume.ValueRange.Min),
            };
        }
        else if (Model.Frames is { } frames)
        {
            var buffer = frames.TryGet(Model.FrameIndex);
            if (buffer is null) return;
            if (_pinnedFrame != Model.FrameIndex)
            {
                if (_pinnedFrame is { } old) frames.Unpin(old);
                frames.Pin(Model.FrameIndex);
                _pinnedFrame = Model.FrameIndex;
            }
            frames.Prefetch(Model.FrameIndex, Model.CinePlaying ? 8 : 3);
            layer = new BaseLayer
            {
                Data = buffer,
                WorldToIndex = frames.IndexToWorld.Inverse(),
                Interpolation = Model.Interpolation,
                Display = Model.Display,
            };
        }
        if (layer is null) return;

        FusionLayer? fusion = null;
        if (Model.Fusion is { Visible: true } f)
        {
            fusion = new FusionLayer
            {
                Data = f.Pet.Data,
                WorldToIndex = f.Pet.WorldToIndex,
                ValueFactor = f.DisplayFactor,
                Lower = f.Lower,
                Upper = f.Upper,
                Colormap = f.Colormap,
                Opacity = f.Opacity,
            };
        }

        LabelLayer? labels = null;
        if (Model.Labels is { } map && Model.ShowLabels && Model.Volume is not null)
        {
            var colors = new uint[256];
            foreach (var s in map.Segments.Where(s => s.Visible)) colors[s.Label] = 0xFF000000u | s.Color;
            labels = new LabelLayer { Labels = map.Labels, WorldToIndex = map.IndexToPatient.Inverse(), Colors = colors, FillOpacity = Model.LabelOpacity, Outline = true };
        }

        _renderer ??= new SliceRenderer();
        _bitmap!.Lock();
        try
        {
            _renderer.Render(view, layer, fusion, labels, (uint*)_bitmap.BackBuffer);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    private unsafe void ClearBitmap()
    {
        if (_bitmap is null) return;
        _bitmap.Lock();
        new Span<uint>((void*)_bitmap.BackBuffer, _bitmap.PixelWidth * _bitmap.PixelHeight).Fill(0xFF000000);
        _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        _bitmap.Unlock();
    }

    private void Render3D()
    {
        var volume = Model.Volume!;
        if (!volume.IsComplete)
        {
            Model.Message = string.Create(CultureInfo.InvariantCulture, $"Loading volume for 3D… {volume.Progress * 100:0}%");
            return;
        }
        if (_vr is null)
        {
            _vr = new VolumeRenderService();
            _vr.FrameReady += frame => Dispatcher.BeginInvoke(() => PresentVrFrame(frame));
        }
        var (w, h) = DevicePixelSize();
        _vr.Request(new VrRequest
        {
            Volume = volume,
            State = Model.Vr.Clone(),
            Width = w,
            Height = h,
            Interactive = _vrInteractive,
            WindowCenter = Model.Display.Center,
            WindowWidth = Model.Display.Width,
            Labels = Model.Labels,
        });
        Model.Message = null;
    }

    private unsafe void PresentVrFrame(VrFrame frame)
    {
        if (!Model.Is3D) return;
        EnsureBitmap(frame.Width, frame.Height);
        if (_bitmap!.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        Model.Vr.LastRenderMs = frame.Milliseconds;
        if (!string.IsNullOrEmpty(frame.RendererInfo)) Model.Vr.RendererInfo = frame.RendererInfo;
        _renderError = frame.Error is null ? null : "3D rendering failed: " + frame.Error;
        DrawOverlay();
        if (!_vrInteractive && (DateTime.UtcNow - _lastVrInteraction).TotalMilliseconds < 50) RequestRender();
    }

    /// <summary>Ends an interactive 3D drag with one full-quality render.</summary>
    private void EndVrInteraction()
    {
        _vrInteractive = false;
        RequestRender();
    }

    public void ReleaseContent()
    {
        if (_pinnedFrame is { } f && Model.Frames is { } frames) frames.Unpin(f);
        _pinnedFrame = null;
        if (Model.Volume is { } v) _host.Images.Release(v);
        Model.Clear();
        ClearBitmap();
    }

    /// <summary>The rendered image plus overlays, at device resolution, for screenshot export.</summary>
    public RenderTargetBitmap Snapshot(bool includeOverlay)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var (w, h) = DevicePixelSize();
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_bitmap is not null) dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
            if (includeOverlay) DrawOverlayContent(dc, forExport: true);
        }
        rtb.Render(visual);
        return rtb;
    }

    public void Dispose()
    {
        ReleaseContent();
        _renderer?.Dispose();
        _vr?.Dispose();
    }
}

/// <summary>Hosts a single DrawingVisual so overlays can be redrawn without a layout pass.</summary>
public sealed class OverlayHost : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public OverlayHost()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
    }

    public DrawingContext Open() => _visual.RenderOpen();

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;
}
