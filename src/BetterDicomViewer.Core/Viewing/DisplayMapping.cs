namespace BetterDicomViewer.Core.Viewing;

public enum VoiFunction
{
    Linear,
    LinearExact,
    Sigmoid,
}

/// <summary>Window/level transforms from PS3.3 C.11.2.1.2 (VOI LUT Function), returning [0, 1].</summary>
public static class Voi
{
    public static VoiFunction Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "LINEAR_EXACT" => VoiFunction.LinearExact,
        "SIGMOID" => VoiFunction.Sigmoid,
        _ => VoiFunction.Linear,
    };

    public static double Map(double x, double center, double width, VoiFunction function)
    {
        switch (function)
        {
            case VoiFunction.LinearExact:
            {
                var w = Math.Max(width, 1e-9);
                return Math.Clamp((x - center) / w + 0.5, 0, 1);
            }
            case VoiFunction.Sigmoid:
            {
                var w = Math.Max(width, 1e-9);
                return 1.0 / (1.0 + Math.Exp(-4.0 * (x - center) / w));
            }
            default:
            {
                // Linear (C.11.2.1.2.1): width must be >= 1.
                var w = Math.Max(width, 1.0);
                if (x <= center - 0.5 - (w - 1) / 2) return 0;
                if (x > center - 0.5 + (w - 1) / 2) return 1;
                return (x - (center - 0.5)) / (w - 1) + 0.5;
            }
        }
    }
}

public sealed class Colormap
{
    private Colormap(string name, uint[] bgra)
    {
        Name = name;
        Bgra = bgra;
    }

    public string Name { get; }

    /// <summary>256 opaque BGRA entries (0xAARRGGBB as little-endian uint).</summary>
    public uint[] Bgra { get; }

    public override string ToString() => Name;

    public static uint Pack(byte r, byte g, byte b, byte a = 255) => (uint)(a << 24 | r << 16 | g << 8 | b);

    private static Colormap FromStops(string name, params (double T, byte R, byte G, byte B)[] stops)
    {
        var lut = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            var k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].T) k++;
            var a = stops[k];
            var b = stops[k + 1];
            var f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 0;
            lut[i] = Pack(
                (byte)Math.Round(a.R + (b.R - a.R) * f),
                (byte)Math.Round(a.G + (b.G - a.G) * f),
                (byte)Math.Round(a.B + (b.B - a.B) * f));
        }
        return new Colormap(name, lut);
    }

    public static readonly Colormap Grayscale = FromStops("Grayscale", (0, 0, 0, 0), (1, 255, 255, 255));
    public static readonly Colormap HotIron = FromStops("Hot iron", (0, 0, 0, 0), (0.35, 160, 0, 0), (0.6, 255, 96, 0), (0.85, 255, 210, 40), (1, 255, 255, 255));
    public static readonly Colormap Pet = FromStops("PET", (0, 0, 0, 0), (0.2, 0, 0, 160), (0.4, 120, 0, 200), (0.6, 230, 30, 60), (0.8, 255, 170, 0), (1, 255, 255, 255));
    public static readonly Colormap Rainbow = FromStops("Rainbow", (0, 0, 0, 120), (0.2, 0, 60, 255), (0.4, 0, 220, 220), (0.6, 60, 230, 0), (0.8, 255, 200, 0), (1, 255, 0, 0));
    public static readonly Colormap Bone = FromStops("Bone", (0, 0, 0, 0), (0.375, 81, 81, 113), (0.75, 166, 199, 199), (1, 255, 255, 255));
    public static readonly Colormap InvertedGray = FromStops("Inverted gray", (0, 255, 255, 255), (1, 0, 0, 0));

    public static IReadOnlyList<Colormap> All { get; } = [Grayscale, HotIron, Pet, Rainbow, Bone, InvertedGray];

    public static Colormap ByName(string? name) => All.FirstOrDefault(c => c.Name == name) ?? Grayscale;
}
