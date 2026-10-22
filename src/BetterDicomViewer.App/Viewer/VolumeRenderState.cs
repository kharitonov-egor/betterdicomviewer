using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App.Viewer;

public sealed record VrPreset(string Name, string Modality, Func<double, double, TransferFunction> Build);

/// <summary>Transfer-function presets. CT presets are in HU; PET presets are in display units (SUV when available).</summary>
public static class VrPresets
{
    private static TransferFunction Composite((double, double, double, double)[] colors, (double, double)[] opacity, (double, double)[]? gradient = null, bool shade = true) =>
        new(colors.Select(c => (c.Item1, c.Item2, c.Item3, c.Item4)).ToList(), opacity.Select(o => (o.Item1, o.Item2)).ToList(),
            (gradient ?? []).Select(o => (o.Item1, o.Item2)).ToList(), BlendMode.Composite, shade);

    public static readonly VrPreset CtSoftTissue = new("CT soft tissue", "CT", (shift, scale) => Composite(
        [(-500 + shift, 0.45, 0.2, 0.15), (-100 + shift, 0.75, 0.4, 0.3), (60 + shift, 0.9, 0.6, 0.5), (300 + shift, 1, 0.92, 0.82), (1500 + shift, 1, 1, 1)],
        [(-800 + shift, 0), (-350 + shift, 0), (-100 + shift, 0.05 * scale), (60 + shift, 0.18 * scale), (250 + shift, 0.45 * scale), (800 + shift, 0.75 * scale)],
        [(0, 0.3), (40, 0.8), (150, 1)]));

    public static readonly VrPreset CtBone = new("CT bone", "CT", (shift, scale) => Composite(
        [(150 + shift, 0.6, 0.35, 0.2), (300 + shift, 0.92, 0.8, 0.65), (700 + shift, 1, 0.96, 0.88), (1500 + shift, 1, 1, 1)],
        [(120 + shift, 0), (220 + shift, 0.15 * scale), (450 + shift, 0.6 * scale), (1200 + shift, 0.9 * scale)],
        [(0, 0.2), (60, 0.9), (200, 1)]));

    public static readonly VrPreset CtLung = new("CT lung", "CT", (shift, scale) => Composite(
        [(-1000 + shift, 0.3, 0.45, 0.75), (-750 + shift, 0.55, 0.7, 0.95), (-400 + shift, 0.9, 0.85, 0.85), (200 + shift, 1, 0.95, 0.9)],
        [(-1000 + shift, 0), (-920 + shift, 0), (-750 + shift, 0.12 * scale), (-550 + shift, 0.04 * scale), (-300 + shift, 0), (150 + shift, 0), (300 + shift, 0.4 * scale)],
        [(0, 0.2), (80, 1)]));

    public static readonly VrPreset CtAngio = new("CT vessels", "CT", (shift, scale) => Composite(
        [(120 + shift, 0.55, 0.05, 0.05), (220 + shift, 0.9, 0.25, 0.15), (400 + shift, 1, 0.85, 0.75), (1200 + shift, 1, 1, 1)],
        [(110 + shift, 0), (180 + shift, 0.25 * scale), (320 + shift, 0.65 * scale), (1000 + shift, 0.85 * scale)],
        [(0, 0.3), (60, 1)]));

    public static readonly VrPreset PetHot = new("PET hot", "PT", (shift, scale) => Composite(
        [(0 + shift, 0, 0, 0), (2 + shift, 0.6, 0, 0), (5 + shift, 1, 0.45, 0), (9 + shift, 1, 0.9, 0.3), (15 + shift, 1, 1, 1)],
        [(0.8 + shift, 0), (2 + shift, 0.05 * scale), (5 + shift, 0.35 * scale), (12 + shift, 0.85 * scale)],
        null, shade: false));

    public static IReadOnlyList<VrPreset> All { get; } = [CtSoftTissue, CtBone, CtLung, CtAngio, PetHot];

    public static VrPreset ByName(string name) => All.FirstOrDefault(p => p.Name == name) ?? CtSoftTissue;

    /// <summary>Grey ramp across a window, for MIP / MinIP / average projections.</summary>
    public static TransferFunction Projection(BlendMode blend, double center, double width, bool invert)
    {
        var lo = center - width / 2;
        var hi = center + width / 2;
        var (a, b) = invert ? (1.0, 0.0) : (0.0, 1.0);
        return new TransferFunction(
            [(lo, a, a, a), (hi, b, b, b)],
            [(lo, 1), (hi, 1)],
            [], blend, false);
    }
}

public enum VrMode
{
    Preset,
    Mip,
    MinIp,
    Average,
}

/// <summary>Camera, transfer function and clipping state for a 3D viewport. Units: patient mm, display units for presets.</summary>
public sealed class VolumeRenderState
{
    public VrMode Mode { get; set; } = VrMode.Preset;
    public string PresetName { get; set; } = VrPresets.CtSoftTissue.Name;
    public double OpacityShift { get; set; }
    public double OpacityScale { get; set; } = 1;
    public bool Shade { get; set; } = true;
    public bool InvertProjection { get; set; }
    public Vec3 Direction { get; set; } = new(0, 1, 0);
    public Vec3 Up { get; set; } = Vec3.UnitZ;
    public double Zoom { get; set; } = 1;
    public Vec3 Pan { get; set; } = Vec3.Zero;
    public double[] CropMin { get; set; } = [0, 0, 0];
    public double[] CropMax { get; set; } = [1, 1, 1];
    public bool ShowLabels { get; set; } = true;
    public string RendererInfo { get; set; } = "";
    public double LastRenderMs { get; set; }

    public void ResetCamera(Vec3 direction, Vec3 up)
    {
        Direction = direction.Normalized();
        Up = up.Normalized();
        Zoom = 1;
        Pan = Vec3.Zero;
    }

    /// <summary>Trackball rotation from a mouse drag in pixels.</summary>
    public void Orbit(double dxPixels, double dyPixels)
    {
        var right = Vec3.Cross(Direction, Up).Normalized();
        var yaw = -dxPixels * 0.01;
        var pitch = dyPixels * 0.01;
        var dir = Vec3.Rotate(Direction, Up, yaw);
        var up = Up;
        right = Vec3.Cross(dir, up).Normalized();
        dir = Vec3.Rotate(dir, right, pitch);
        up = Vec3.Rotate(up, right, pitch);
        Direction = dir.Normalized();
        Up = up.Normalized();
    }

    public void Roll(double radians) => Up = Vec3.Rotate(Up, Direction, radians).Normalized();

    public VolumeRenderState Clone()
    {
        var c = (VolumeRenderState)MemberwiseClone();
        c.CropMin = (double[])CropMin.Clone();
        c.CropMax = (double[])CropMax.Clone();
        return c;
    }
}
