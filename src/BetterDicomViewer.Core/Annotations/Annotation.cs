using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Annotations;

public enum AnnotationKind
{
    Length,
    Angle,
    Ellipse,
    Rectangle,
    Freehand,
    Arrow,
    Text,
    Probe,
}

/// <summary>
/// A measurement or annotation stored in the world coordinates of its display set: DICOM patient coordinates (mm)
/// for volumes, scaled pixel coordinates for 2D images. ROI shapes also store the plane axes they were drawn in.
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnnotationKind Kind { get; set; }
    public string StudyInstanceUid { get; set; } = "";
    public string SeriesInstanceUid { get; set; } = "";
    public string DisplaySetId { get; set; } = "";
    public string? FrameOfReferenceUid { get; set; }
    public bool PatientSpace { get; set; }

    /// <summary>Frame index for 2D image sets; null for volume annotations.</summary>
    public int? FrameIndex { get; set; }

    public List<Vec3> Points { get; set; } = [];
    public Vec3 PlaneRight { get; set; } = Vec3.UnitX;
    public Vec3 PlaneDown { get; set; } = Vec3.UnitY;
    public string Label { get; set; } = "";
    public string? Text { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public Vec3 PlaneNormal => Vec3.Cross(PlaneRight, PlaneDown).Normalized();

    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Points = [.. Points];
        return c;
    }

    public static string KindName(AnnotationKind k) => k switch
    {
        AnnotationKind.Length => "Length",
        AnnotationKind.Angle => "Angle",
        AnnotationKind.Ellipse => "Ellipse ROI",
        AnnotationKind.Rectangle => "Rectangle ROI",
        AnnotationKind.Freehand => "Freehand ROI",
        AnnotationKind.Arrow => "Arrow",
        AnnotationKind.Text => "Text",
        AnnotationKind.Probe => "Point",
        _ => k.ToString(),
    };

    public bool IsRoi => Kind is AnnotationKind.Ellipse or AnnotationKind.Rectangle or AnnotationKind.Freehand;

    /// <summary>Outline of an ROI as a polygon in world space (ellipses are sampled).</summary>
    public List<Vec3> Outline(int ellipseSegments = 72)
    {
        switch (Kind)
        {
            case AnnotationKind.Rectangle when Points.Count >= 2:
            {
                var (u0, v0) = ToPlane(Points[0]);
                var (u1, v1) = ToPlane(Points[1]);
                return [FromPlane(u0, v0), FromPlane(u1, v0), FromPlane(u1, v1), FromPlane(u0, v1)];
            }
            case AnnotationKind.Ellipse when Points.Count >= 2:
            {
                var (u0, v0) = ToPlane(Points[0]);
                var (u1, v1) = ToPlane(Points[1]);
                var cu = (u0 + u1) / 2;
                var cv = (v0 + v1) / 2;
                var ru = Math.Abs(u1 - u0) / 2;
                var rv = Math.Abs(v1 - v0) / 2;
                var list = new List<Vec3>(ellipseSegments);
                for (var i = 0; i < ellipseSegments; i++)
                {
                    var t = 2 * Math.PI * i / ellipseSegments;
                    list.Add(FromPlane(cu + ru * Math.Cos(t), cv + rv * Math.Sin(t)));
                }
                return list;
            }
            case AnnotationKind.Freehand:
                return [.. Points];
            default:
                return [.. Points];
        }
    }

    /// <summary>In-plane coordinates relative to the first point.</summary>
    public (double U, double V) ToPlane(Vec3 p)
    {
        var d = p - Points[0];
        return (Vec3.Dot(d, PlaneRight), Vec3.Dot(d, PlaneDown));
    }

    public Vec3 FromPlane(double u, double v) => Points[0] + PlaneRight * u + PlaneDown * v;
}
