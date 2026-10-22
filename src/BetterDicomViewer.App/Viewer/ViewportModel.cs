using System.Windows.Media;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.App.Viewer;

public enum PlaneKind
{
    Acquisition,
    Axial,
    Coronal,
    Sagittal,
}

public sealed class FusionState
{
    public required ImageVolume Pet { get; init; }
    public double Opacity { get; set; } = 0.5;
    public Colormap Colormap { get; set; } = Colormap.HotIron;
    public double Lower { get; set; }
    public double Upper { get; set; } = 5;
    public bool Visible { get; set; } = true;
    public bool UsesSuv => Pet.Suv is { IsAvailable: true };
    public string Unit => UsesSuv ? "SUV" : Pet.ValueUnit;
    public double Factor(int slice) => Pet.Suv?.Factor(slice) ?? 1.0;
    public double DisplayFactor => UsesSuv ? Pet.Suv!.Factor(Pet.Nz / 2) ?? 1.0 : 1.0;
}

/// <summary>State of one viewport: what is shown, the 2D camera, display settings, overlays and the 3D view.</summary>
public sealed class ViewportModel
{
    public ViewportModel(int index) => Index = index;

    public int Index { get; }
    public DisplaySet? Set { get; private set; }
    public ImageVolume? Volume { get; private set; }
    public FrameSetSource? Frames { get; private set; }
    public bool Is3D { get; set; }
    public SliceView View { get; } = new();
    public PlaneKind Plane { get; private set; } = PlaneKind.Acquisition;
    public int FrameIndex { get; set; }
    public GrayDisplay Display { get; set; } = new();
    public Interpolation Interpolation { get; set; } = Interpolation.Linear;
    public FusionState? Fusion { get; set; }
    public LabelMap? Labels { get; set; }
    public bool ShowLabels { get; set; } = true;
    public double LabelOpacity { get; set; } = 0.35;
    public VolumeRenderState Vr { get; } = new();
    public bool CinePlaying { get; set; }
    public double CineFps { get; set; } = 25;
    public double ZoomFactor { get; set; } = 1;
    public string? Message { get; set; }
    public bool HasContent => Set is not null;
    public bool IsVolume => Volume is not null;

    public event Action<ViewportModel>? Changed;

    public void NotifyChanged() => Changed?.Invoke(this);

    public void Clear()
    {
        Set = null;
        Volume = null;
        Frames = null;
        Fusion = null;
        Labels = null;
        Is3D = false;
        Message = null;
    }

    public void ShowVolume(DisplaySet set, ImageVolume volume, PlaneKind plane)
    {
        Set = set;
        Volume = volume;
        Frames = null;
        Plane = plane;
        Message = null;
        ConfigurePlane(plane);
    }

    public void ShowFrames(DisplaySet set, FrameSetSource frames, int frameIndex)
    {
        Set = set;
        Volume = null;
        Frames = frames;
        Plane = PlaneKind.Acquisition;
        Is3D = false;
        Fusion = null;
        Labels = null;
        Message = null;
        FrameIndex = Math.Clamp(frameIndex, 0, set.Frames.Count - 1);
        View.SetPlane(Vec3.UnitX, Vec3.UnitY);
        View.SlabMode = SlabMode.None;
        View.SlabThicknessMm = 0;
    }

    public Affine IndexToWorld => Volume?.IndexToWorld ?? Frames?.IndexToWorld ?? Affine.Identity;

    public string? FrameOfReferenceUid => Volume is not null ? Set?.FrameOfReferenceUid : null;

    public string ValueUnit => Volume?.ValueUnit ?? Frames?.ValueUnit ?? "";

    public static (Vec3 Right, Vec3 Down) PlaneAxes(PlaneKind plane) => plane switch
    {
        PlaneKind.Axial => (Vec3.UnitX, Vec3.UnitY),
        PlaneKind.Coronal => (Vec3.UnitX, -Vec3.UnitZ),
        PlaneKind.Sagittal => (Vec3.UnitY, -Vec3.UnitZ),
        _ => (Vec3.UnitX, Vec3.UnitY),
    };

    public void ConfigurePlane(PlaneKind plane)
    {
        Plane = plane;
        if (Volume is null) return;
        var g = Volume.Source.Geometry!;
        if (plane == PlaneKind.Acquisition) View.SetPlane(g.RowDirection, g.ColumnDirection);
        else
        {
            var (r, d) = PlaneAxes(plane);
            View.SetPlane(r, d);
        }
        View.Center = g.Center;
        SnapToVoxelPlane();
    }

    /// <summary>Fits the image to the viewport, keeping the current slice position.</summary>
    public void Fit()
    {
        if (Volume is not null)
        {
            var depth = Vec3.Dot(View.Center, View.Normal);
            var g = Volume.Source.Geometry!;
            View.Center = g.Center + View.Normal * (depth - Vec3.Dot(g.Center, View.Normal));
            View.FitTo(g.Corners(), View.Center);
        }
        else if (Set is not null)
        {
            var size = FrameSize();
            var corners = new[] { new Vec3(-0.5, -0.5, 0), IndexToWorld.TransformPoint(new Vec3(size.W - 0.5, size.H - 0.5, 0)) };
            View.Center = (corners[0] + corners[1]) / 2;
            View.FitTo(BoxCorners(corners[0], corners[1]), View.Center);
        }
        ZoomFactor = 1;
    }

    private static IEnumerable<Vec3> BoxCorners(Vec3 a, Vec3 b)
    {
        yield return a;
        yield return new Vec3(b.X, a.Y, 0);
        yield return new Vec3(a.X, b.Y, 0);
        yield return b;
    }

    public (int W, int H) FrameSize() => Set is null ? (1, 1) : (Set.Columns, Set.Rows);

    /// <summary>Unit step along the view normal: voxel spacing for axis-aligned planes, smallest spacing otherwise.</summary>
    public double SliceStep()
    {
        if (Volume is null) return 1;
        var n = View.Normal;
        var i2w = Volume.IndexToWorld;
        var axes = new[] { i2w.Column0, i2w.Column1, i2w.Column2 };
        foreach (var axis in axes)
        {
            var len = axis.Length;
            if (Math.Abs(Vec3.Dot(axis / len, n)) > 0.999) return Math.Abs(Vec3.Dot(axis, n));
        }
        return Math.Min(axes[0].Length, Math.Min(axes[1].Length, axes[2].Length));
    }

    private (double Min, double Max) DepthRange()
    {
        var n = View.Normal;
        var g = Volume!.Source.Geometry!;
        var depths = g.Corners().Select(c => Vec3.Dot(c, n)).ToList();
        return (depths.Min(), depths.Max());
    }

    /// <summary>Direction of "next image": acquisition order for native planes, head-to-feet, anterior-to-posterior and right-to-left otherwise.</summary>
    private int NextSign()
    {
        var n = View.Normal;
        return Plane switch
        {
            PlaneKind.Acquisition => Volume!.Source.Geometry!.AcquisitionDirection * Math.Sign(Vec3.Dot(n, Volume.Source.Geometry.Normal) + 1e-9),
            PlaneKind.Axial => Vec3.Dot(n, -Vec3.UnitZ) >= 0 ? 1 : -1,
            PlaneKind.Coronal => Vec3.Dot(n, Vec3.UnitY) >= 0 ? 1 : -1,
            _ => Vec3.Dot(n, Vec3.UnitX) >= 0 ? 1 : -1,
        };
    }

    public (int Index, int Count) SliceIndex()
    {
        if (Set is null) return (0, 0);
        if (Volume is null) return (FrameIndex, Set.Frames.Count);
        var step = SliceStep();
        var (min, max) = DepthRange();
        var count = (int)Math.Round((max - min) / step) + 1;
        var depth = Vec3.Dot(View.Center, View.Normal);
        var k = (int)Math.Round((depth - min) / step);
        if (NextSign() < 0) k = count - 1 - k;
        return (Math.Clamp(k, 0, count - 1), count);
    }

    public void GoToSlice(int index)
    {
        if (Set is null) return;
        if (Volume is null)
        {
            FrameIndex = Math.Clamp(index, 0, Set.Frames.Count - 1);
            return;
        }
        var step = SliceStep();
        var (min, max) = DepthRange();
        var count = (int)Math.Round((max - min) / step) + 1;
        index = Math.Clamp(index, 0, count - 1);
        if (NextSign() < 0) index = count - 1 - index;
        var target = min + index * step;
        var depth = Vec3.Dot(View.Center, View.Normal);
        View.Center += View.Normal * (target - depth);
        SnapToVoxelPlane();
    }

    public void ScrollBy(int delta)
    {
        var (k, _) = SliceIndex();
        GoToSlice(k + delta);
    }

    /// <summary>For planes parallel to a voxel axis, keeps the plane exactly on voxel centres so slices are not interpolated.</summary>
    public void SnapToVoxelPlane()
    {
        if (Volume is null) return;
        var n = View.Normal;
        var w2i = Volume.WorldToIndex;
        var i2w = Volume.IndexToWorld;
        var axes = new[] { i2w.Column0, i2w.Column1, i2w.Column2 };
        for (var a = 0; a < 3; a++)
        {
            if (Math.Abs(Vec3.Dot(axes[a].Normalized(), n)) < 0.999) continue;
            var idx = w2i.TransformPoint(View.Center);
            var current = idx[a];
            var snapped = Math.Round(current);
            var deltaIndex = snapped - current;
            var shift = axes[a] * deltaIndex;
            View.Center += n * Vec3.Dot(shift, n);
            return;
        }
    }

    /// <summary>Moves the plane (along its normal) so it passes through <paramref name="point"/>.</summary>
    public void MovePlaneThrough(Vec3 point)
    {
        var n = View.Normal;
        View.Center += n * Vec3.Dot(point - View.Center, n);
        SnapToVoxelPlane();
    }

    public Color OrientationColor => Plane switch
    {
        PlaneKind.Axial => Color.FromRgb(0xE5, 0x48, 0x4D),
        PlaneKind.Coronal => Color.FromRgb(0x46, 0xA7, 0x58),
        PlaneKind.Sagittal => Color.FromRgb(0xF5, 0xD9, 0x0A),
        _ => OrientationNames.PlaneName(View.Normal) switch
        {
            "Axial" => Color.FromRgb(0xE5, 0x48, 0x4D),
            "Coronal" => Color.FromRgb(0x46, 0xA7, 0x58),
            "Sagittal" => Color.FromRgb(0xF5, 0xD9, 0x0A),
            _ => Color.FromRgb(0x9E, 0x8C, 0xFC),
        },
    };

    public string PlaneLabel => Volume is null ? (Set?.Kind == DisplaySetKind.Frames ? "2D" : "") :
        Is3D ? "3D" : Plane == PlaneKind.Acquisition ? OrientationNames.PlaneName(View.Normal) : Plane.ToString();
}
