using System.Diagnostics;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App.Viewer;

public sealed class VrRequest
{
    public required ImageVolume Volume { get; init; }
    public required VolumeRenderState State { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool Interactive { get; init; }
    public double WindowCenter { get; init; }
    public double WindowWidth { get; init; }
    public LabelMap? Labels { get; init; }
}

public sealed record VrFrame(uint[] Pixels, int Width, int Height, double Milliseconds, string? Error, string RendererInfo);

/// <summary>
/// Runs one VTK offscreen renderer on a dedicated thread. Requests coalesce, so a burst of camera changes renders
/// only the newest state.
/// </summary>
public sealed class VolumeRenderService : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _gate = new();
    private VrRequest? _pending;
    private (int X, int Y, int W, int H, TaskCompletionSource<Vec3?> Tcs)? _pick;
    private volatile bool _stop;

    private VolumeRenderer? _renderer;
    private ImageVolume? _uploaded;
    private long _uploadedVersion = -1;

    public VolumeRenderService()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "3D renderer" };
        _thread.Start();
    }

    public event Action<VrFrame>? FrameReady;

    public void Request(VrRequest request)
    {
        lock (_gate) _pending = request;
        _signal.Set();
    }

    public Task<Vec3?> PickAsync(int x, int y, int width, int height)
    {
        var tcs = new TaskCompletionSource<Vec3?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pick = (x, y, width, height, tcs);
        _signal.Set();
        return tcs.Task;
    }

    private void Run()
    {
        while (!_stop)
        {
            _signal.WaitOne();
            if (_stop) break;

            (int X, int Y, int W, int H, TaskCompletionSource<Vec3?> Tcs)? pick;
            VrRequest? request;
            lock (_gate)
            {
                pick = _pick;
                _pick = null;
                request = _pending;
                _pending = null;
            }

            if (pick is { } p)
            {
                try
                {
                    p.Tcs.TrySetResult(_renderer is null || _uploaded is null ? null : _renderer.Pick(p.X, p.Y, p.W, p.H));
                }
                catch (Exception)
                {
                    p.Tcs.TrySetResult(null);
                }
            }

            if (request is null) continue;
            FrameReady?.Invoke(RenderOne(request));
        }

        _renderer?.Dispose();
    }

    private VrFrame RenderOne(VrRequest r)
    {
        var w = Math.Max(1, r.Width);
        var h = Math.Max(1, r.Height);
        var sw = Stopwatch.StartNew();
        try
        {
            _renderer ??= new VolumeRenderer();
            var volume = r.Volume;
            if (!ReferenceEquals(_uploaded, volume) || (_uploadedVersion != volume.Data.Version && volume.IsComplete))
            {
                _renderer.SetVolume(volume.Data, volume.IndexToWorld);
                _uploaded = volume;
                _uploadedVersion = volume.Data.Version;
            }

            var s = r.State;
            var g = volume.Source.Geometry!;
            var spacing = Math.Min(volume.Spacing.X, Math.Min(volume.Spacing.Y, volume.Spacing.Z));
            _renderer.SetTransfer(BuildTransfer(r), Math.Max(0.25, spacing * 0.5));

            var focal = g.Center + s.Pan;
            var distance = g.DiagonalMm * 1.9 / Math.Max(s.Zoom, 0.05);
            _renderer.SetCamera(focal - s.Direction * distance, focal, s.Up, 30, false, g.DiagonalMm / 2 / s.Zoom);
            _renderer.SetClipPlanes(ClipPlanes(volume, s));
            var whiteBackground = s.Mode != VrMode.Preset && s.InvertProjection;
            if (whiteBackground) _renderer.SetBackground(1, 1, 1);
            else _renderer.SetBackground(0.035, 0.035, 0.04);

            if (r.Labels is { } labels && s.ShowLabels && labels.Segments.Any(x => x.Visible))
            {
                var colors = new List<(double, double, double, double)>();
                for (var label = 1; label <= labels.Segments.Max(x => x.Label); label++)
                {
                    var seg = labels.Segments.FirstOrDefault(x => x.Label == label);
                    if (seg is null || !seg.Visible) colors.Add((0, 0, 0, 0));
                    else colors.Add((((seg.Color >> 16) & 0xFF) / 255.0, ((seg.Color >> 8) & 0xFF) / 255.0, (seg.Color & 0xFF) / 255.0, 1.0));
                }
                _renderer.SetLabels(labels.Labels, labels.IndexToPatient, colors, 12);
            }
            else
            {
                _renderer.SetLabels(null, Affine.Identity, [], 0);
            }

            var pixels = new uint[w * h];
            _renderer.Render(w, h, r.Interactive, pixels);
            return new VrFrame(pixels, w, h, sw.Elapsed.TotalMilliseconds, null, _renderer.RendererInfo);
        }
        catch (Exception ex)
        {
            return new VrFrame(new uint[w * h], w, h, sw.Elapsed.TotalMilliseconds, ex.Message, "");
        }
    }

    private static TransferFunction BuildTransfer(VrRequest r)
    {
        var s = r.State;
        var blend = s.Mode switch
        {
            VrMode.Mip => BlendMode.MaximumIntensity,
            VrMode.MinIp => BlendMode.MinimumIntensity,
            VrMode.Average => BlendMode.AverageIntensity,
            _ => BlendMode.Composite,
        };
        // Display units (HU, SUV) map back to stored units through the SUV factor for PET.
        var factor = r.Volume.Suv is { IsAvailable: true } suv ? suv.Factor(r.Volume.Nz / 2) ?? 1.0 : 1.0;
        TransferFunction tf;
        if (blend != BlendMode.Composite)
        {
            tf = VrPresets.Projection(blend, r.WindowCenter / factor, r.WindowWidth / factor, s.InvertProjection);
        }
        else
        {
            var preset = VrPresets.ByName(s.PresetName);
            tf = preset.Build(s.OpacityShift, s.OpacityScale) with { Shade = s.Shade };
            if (factor != 1.0)
            {
                tf = tf with
                {
                    Colors = tf.Colors.Select(c => (c.X / factor, c.R, c.G, c.B)).ToList(),
                    Opacity = tf.Opacity.Select(o => (o.X / factor, o.A)).ToList(),
                };
            }
        }
        return tf;
    }

    private static List<(Vec3, Vec3)> ClipPlanes(ImageVolume v, VolumeRenderState s)
    {
        var planes = new List<(Vec3, Vec3)>();
        var i2w = v.IndexToWorld;
        var axes = new[] { i2w.Column0, i2w.Column1, i2w.Column2 };
        var dims = new[] { v.Nx - 1, v.Ny - 1, v.Nz - 1 };
        for (var a = 0; a < 3; a++)
        {
            var dir = axes[a].Normalized();
            if (s.CropMin[a] > 0.001)
            {
                var idx = new double[3];
                idx[a] = s.CropMin[a] * dims[a];
                planes.Add((i2w.TransformPoint(new Vec3(idx[0], idx[1], idx[2])), dir));
            }
            if (s.CropMax[a] < 0.999)
            {
                var idx = new double[3];
                idx[a] = s.CropMax[a] * dims[a];
                planes.Add((i2w.TransformPoint(new Vec3(idx[0], idx[1], idx[2])), -dir));
            }
        }
        return planes;
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
