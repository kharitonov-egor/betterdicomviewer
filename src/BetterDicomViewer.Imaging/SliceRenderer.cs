using System.Runtime.InteropServices;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Viewing;

namespace BetterDicomViewer.Imaging;

public sealed class GrayDisplay
{
    public double Center { get; set; } = 40;
    public double Width { get; set; } = 400;
    public VoiFunction Function { get; set; } = VoiFunction.Linear;
    public bool Invert { get; set; }
    public Colormap Colormap { get; set; } = Colormap.Grayscale;

    /// <summary>Multiplies stored values before windowing (used to window PET in SUV units).</summary>
    public double ValueFactor { get; set; } = 1.0;

    public GrayDisplay Clone() => (GrayDisplay)MemberwiseClone();
}

public sealed class BaseLayer
{
    public required VoxelBuffer Data { get; init; }
    public required Affine WorldToIndex { get; init; }
    public Interpolation Interpolation { get; set; } = Interpolation.Linear;
    public GrayDisplay Display { get; set; } = new();
    public double Background { get; set; }
}

public sealed class FusionLayer
{
    public required VoxelBuffer Data { get; init; }
    public required Affine WorldToIndex { get; init; }
    public double ValueFactor { get; set; } = 1.0;
    public double Lower { get; set; }
    public double Upper { get; set; } = 5;
    public Colormap Colormap { get; set; } = Colormap.HotIron;
    public double Opacity { get; set; } = 0.5;
}

public sealed class LabelLayer
{
    public required VoxelBuffer Labels { get; init; }
    public required Affine WorldToIndex { get; init; }

    /// <summary>BGRA colour per label value; alpha 0 hides a label.</summary>
    public uint[] Colors { get; init; } = new uint[256];

    public double FillOpacity { get; set; } = 0.35;
    public bool Outline { get; set; } = true;
}

/// <summary>
/// Composes a 2D view: native reslice of each layer onto the viewport pixel grid, then window/level, colour maps
/// and alpha blending into a BGRA buffer. One instance per viewport; not thread-safe.
/// </summary>
public sealed unsafe class SliceRenderer : IDisposable
{
    private readonly Reslicer _base = new();
    private Reslicer? _fusion;
    private Reslicer? _labels;
    private byte[] _baseBuffer = [];
    private float[] _fusionBuffer = [];
    private byte[] _labelBuffer = [];
    private uint[]? _lut;
    private (double C, double W, VoiFunction F, bool I, Colormap M, double K, VoxelType T) _lutKey;

    public double LastResliceMs { get; private set; }
    public double LastComposeMs { get; private set; }

    public void Render(SliceView view, BaseLayer layer, FusionLayer? fusion, LabelLayer? labels, uint* output)
    {
        var w = view.Width;
        var h = view.Height;
        var n = w * h;
        var components = layer.Data.Components;
        var outType = layer.Data.Type;
        var bytes = n * components * outType.Size();
        if (_baseBuffer.Length < bytes) _baseBuffer = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var (slabMode, slabSlices, slabStep) = SlabParameters(view, layer);
        var axes = view.ResliceAxes(layer.WorldToIndex, slabStep);
        fixed (byte* b = _baseBuffer)
        {
            _base.Reslice(layer.Data, axes, w, h, layer.Interpolation, slabMode, slabSlices, layer.Background, b, outType);
        }

        if (fusion is not null)
        {
            _fusion ??= new Reslicer();
            if (_fusionBuffer.Length < n) _fusionBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
            var fAxes = view.ResliceAxes(fusion.WorldToIndex, slabStep);
            fixed (float* f = _fusionBuffer)
            {
                _fusion.Reslice(fusion.Data, fAxes, w, h, Interpolation.Linear, slabMode, slabSlices, 0, f, VoxelType.Float32);
            }
        }

        if (labels is not null)
        {
            _labels ??= new Reslicer();
            if (_labelBuffer.Length < n) _labelBuffer = GC.AllocateUninitializedArray<byte>(n, pinned: true);
            var lAxes = view.ResliceAxes(labels.WorldToIndex);
            fixed (byte* l = _labelBuffer)
            {
                _labels.Reslice(labels.Labels, lAxes, w, h, Interpolation.Nearest, 0, 1, 0, l, VoxelType.UInt8);
            }
        }

        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Compose(w, h, layer, fusion, labels, output);
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastResliceMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastComposeMs = (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static (int Mode, int Slices, double Step) SlabParameters(SliceView view, BaseLayer layer)
    {
        if (view.SlabMode == SlabMode.None || view.SlabThicknessMm <= 0) return (0, 1, 1.0);
        var spacing = Math.Min(layer.WorldToIndex.Inverse().Column0.Length, Math.Min(layer.WorldToIndex.Inverse().Column1.Length, layer.WorldToIndex.Inverse().Column2.Length));
        var slices = Math.Clamp((int)Math.Round(view.SlabThicknessMm / Math.Max(spacing, 0.1)), 1, 512);
        if (slices <= 1) return (0, 1, 1.0);
        return ((int)view.SlabMode, slices, view.SlabThicknessMm / slices);
    }

    private void Compose(int w, int h, BaseLayer layer, FusionLayer? fusion, LabelLayer? labels, uint* output)
    {
        var type = layer.Data.Type;
        var rgb = layer.Data.Components >= 3;
        if (!rgb && type is VoxelType.Int16 or VoxelType.UInt16 or VoxelType.UInt8) EnsureLut(layer.Display, type);

        var lut = _lut;
        var display = layer.Display;
        var fusionColors = fusion?.Colormap.Bgra;
        var labelColors = labels?.Colors;
        var labelAlpha = labels is null ? 0 : (int)Math.Round(Math.Clamp(labels.FillOpacity, 0, 1) * 255);
        var fusionAlpha = fusion is null ? 0 : (int)Math.Round(Math.Clamp(fusion.Opacity, 0, 1) * 255);
        var outline = labels?.Outline ?? false;

        fixed (byte* basePtr = _baseBuffer)
        fixed (float* fusionPtr = _fusionBuffer)
        fixed (byte* labelPtr = _labelBuffer)
        {
            var bp = (IntPtr)basePtr;
            var fp = (IntPtr)fusionPtr;
            var lp = (IntPtr)labelPtr;
            var op = (IntPtr)output;

            void Row(int y)
            {
                var o = (uint*)op + (long)y * w;
                var rowStart = y * w;
                switch (rgb, type)
                {
                    case (true, _):
                    {
                        var src = (byte*)bp + rowStart * 3;
                        for (var x = 0; x < w; x++)
                            o[x] = 0xFF000000u | (uint)src[x * 3] << 16 | (uint)src[x * 3 + 1] << 8 | src[x * 3 + 2];
                        break;
                    }
                    case (false, VoxelType.Int16):
                    {
                        var src = (short*)bp + rowStart;
                        for (var x = 0; x < w; x++) o[x] = lut![src[x] + 32768];
                        break;
                    }
                    case (false, VoxelType.UInt16):
                    {
                        var src = (ushort*)bp + rowStart;
                        for (var x = 0; x < w; x++) o[x] = lut![src[x]];
                        break;
                    }
                    case (false, VoxelType.UInt8):
                    {
                        var src = (byte*)bp + rowStart;
                        for (var x = 0; x < w; x++) o[x] = lut![src[x]];
                        break;
                    }
                    case (false, VoxelType.Float32):
                    {
                        var src = (float*)bp + rowStart;
                        var map = display.Colormap.Bgra;
                        for (var x = 0; x < w; x++)
                        {
                            var t = Voi.Map(src[x] * display.ValueFactor, display.Center, display.Width, display.Function);
                            if (display.Invert) t = 1 - t;
                            o[x] = map[(int)(t * 255 + 0.5)];
                        }
                        break;
                    }
                    case (false, VoxelType.Int32):
                    {
                        var src = (int*)bp + rowStart;
                        var map = display.Colormap.Bgra;
                        for (var x = 0; x < w; x++)
                        {
                            var t = Voi.Map(src[x] * display.ValueFactor, display.Center, display.Width, display.Function);
                            if (display.Invert) t = 1 - t;
                            o[x] = map[(int)(t * 255 + 0.5)];
                        }
                        break;
                    }
                }

                if (fusion is not null)
                {
                    var f = (float*)fp + rowStart;
                    var lo = fusion.Lower;
                    var range = Math.Max(fusion.Upper - fusion.Lower, 1e-9);
                    var k = fusion.ValueFactor;
                    for (var x = 0; x < w; x++)
                    {
                        var v = f[x] * k;
                        if (v <= lo) continue;
                        var idx = (int)Math.Min(255, (v - lo) / range * 255);
                        o[x] = Blend(o[x], fusionColors![idx], fusionAlpha);
                    }
                }

                if (labels is not null)
                {
                    var l = (byte*)lp + rowStart;
                    for (var x = 0; x < w; x++)
                    {
                        var label = l[x];
                        if (label == 0) continue;
                        var color = labelColors![label];
                        var a = (int)(color >> 24);
                        if (a == 0) continue;
                        var edge = outline && (x == 0 || x == w - 1 || y == 0 || y == h - 1
                            || l[x - 1] != label || l[x + 1] != label || l[x - w] != label || l[x + w] != label);
                        o[x] = Blend(o[x], color, edge ? 255 : labelAlpha * a / 255);
                    }
                }
            }

            if ((long)w * h >= 60_000) Parallel.For(0, h, Row);
            else for (var y = 0; y < h; y++) Row(y);
        }
    }

    private static uint Blend(uint dst, uint src, int alpha)
    {
        if (alpha <= 0) return dst;
        if (alpha >= 255) return src | 0xFF000000u;
        var inv = 255 - alpha;
        var r = (((src >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inv) / 255;
        var g = (((src >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inv) / 255;
        var b = ((src & 0xFF) * alpha + (dst & 0xFF) * inv) / 255;
        return 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;
    }

    private void EnsureLut(GrayDisplay d, VoxelType type)
    {
        var key = (d.Center, d.Width, d.Function, d.Invert, d.Colormap, d.ValueFactor, type);
        if (_lut is not null && _lutKey == key) return;
        var size = type == VoxelType.UInt8 ? 256 : 65536;
        var lut = new uint[size];
        var map = d.Colormap.Bgra;
        for (var i = 0; i < size; i++)
        {
            double value = type switch
            {
                VoxelType.Int16 => i - 32768,
                _ => i,
            };
            var t = Voi.Map(value * d.ValueFactor, d.Center, d.Width, d.Function);
            if (d.Invert) t = 1 - t;
            lut[i] = map[(int)(t * 255 + 0.5)];
        }
        _lut = lut;
        _lutKey = key;
    }

    public void Dispose()
    {
        _base.Dispose();
        _fusion?.Dispose();
        _labels?.Dispose();
    }
}
