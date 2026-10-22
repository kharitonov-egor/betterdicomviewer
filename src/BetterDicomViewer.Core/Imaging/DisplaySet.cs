using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.Core.Imaging;

public enum DisplaySetKind
{
    /// <summary>Parallel slices with consistent patient geometry; supports MPR, oblique reslicing and 3D.</summary>
    Volume,

    /// <summary>Independent frames (radiographs, cine loops, secondary captures, mixed geometry). 2D only.</summary>
    Frames,
}

public sealed record FrameRef(InstanceInfo Instance, int FrameIndex);

/// <summary>For resampled volumes: output slice k blends source slices A and B with weight on B.</summary>
public readonly record struct SliceSource(int SourceA, int SourceB, double WeightB);

public sealed class VolumeGeometry
{
    public required int Nx { get; init; }
    public required int Ny { get; init; }
    public required int Nz { get; init; }

    /// <summary>Maps voxel index (column i, row j, slice k) to DICOM patient coordinates (LPS, mm).</summary>
    public required Affine IndexToPatient { get; init; }

    public required Vec3 RowDirection { get; init; }
    public required Vec3 ColumnDirection { get; init; }
    public required Vec3 Normal { get; init; }

    /// <summary>Column spacing (along a row), row spacing (along a column), slice spacing, in mm.</summary>
    public required Vec3 Spacing { get; init; }

    public required IReadOnlyList<SliceSource> SliceSources { get; init; }
    public bool IsResampled { get; init; }
    public bool HasGantryTilt { get; init; }
    public double MaxSpacingDeviationMm { get; init; }

    /// <summary>+1 when ascending instance numbers run along +Normal, -1 when they run against it.</summary>
    public int AcquisitionDirection { get; init; } = 1;

    public Affine PatientToIndex => IndexToPatient.Inverse();

    public Vec3 Center => IndexToPatient.TransformPoint(new Vec3((Nx - 1) / 2.0, (Ny - 1) / 2.0, (Nz - 1) / 2.0));

    public IEnumerable<Vec3> Corners()
    {
        foreach (var k in new[] { 0, Nz - 1 })
        foreach (var j in new[] { 0, Ny - 1 })
        foreach (var i in new[] { 0, Nx - 1 })
            yield return IndexToPatient.TransformPoint(new Vec3(i, j, k));
    }

    public double DiagonalMm
    {
        get
        {
            var a = IndexToPatient.TransformPoint(Vec3.Zero);
            var b = IndexToPatient.TransformPoint(new Vec3(Nx - 1, Ny - 1, Nz - 1));
            return Vec3.Distance(a, b);
        }
    }
}

public sealed class DisplaySet
{
    public required string Id { get; init; }
    public required SeriesKey Series { get; init; }
    public required string Label { get; init; }
    public required DisplaySetKind Kind { get; init; }
    public required IReadOnlyList<FrameRef> Frames { get; init; }
    public required int Rows { get; init; }
    public required int Columns { get; init; }
    public required int SamplesPerPixel { get; init; }
    public string? Modality { get; init; }
    public VolumeGeometry? Geometry { get; init; }
    public List<string> Warnings { get; } = [];

    public InstanceInfo Representative => Frames[Frames.Count / 2].Instance;
    public bool IsColor => SamplesPerPixel >= 3;
    public bool IsVolume => Kind == DisplaySetKind.Volume && Geometry is not null;
    public int SliceCount => Geometry?.Nz ?? Frames.Count;
    public string? FrameOfReferenceUid => Representative.FrameOfReferenceUid;

    public long EstimatedBytes(VoxelType type) => (long)Rows * Columns * SliceCount * SamplesPerPixel * type.Size();

    public string OrientationName => Geometry is null ? "Image" : OrientationNames.PlaneName(Geometry.Normal);
}

public static class OrientationNames
{
    public static string PlaneName(Vec3 normal)
    {
        var n = normal.Normalized();
        if (Math.Abs(n.Z) > 0.85) return "Axial";
        if (Math.Abs(n.Y) > 0.85) return "Coronal";
        if (Math.Abs(n.X) > 0.85) return "Sagittal";
        return "Oblique";
    }
}
