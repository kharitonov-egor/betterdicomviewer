using System.Globalization;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Pet;

namespace BetterDicomViewer.Core.Annotations;

/// <summary>The data an annotation is measured against.</summary>
public sealed class MeasurementContext
{
    public required VoxelBuffer Data { get; init; }
    public required Affine IndexToWorld { get; init; }
    public bool PatientSpace { get; init; }
    public string ValueUnit { get; init; } = "";
    public FrameCalibration? Calibration { get; init; }
    public SuvInfo? Suv { get; init; }
    public Func<int, bool>? IsSliceLoaded { get; init; }

    /// <summary>Optional co-registered volume (PET under a fused CT ROI) measured with the same ROI.</summary>
    public MeasurementContext? Secondary { get; init; }

    public Affine WorldToIndex => IndexToWorld.Inverse();
}

public sealed class RoiStats
{
    public int Count { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public string Unit { get; init; } = "";
    public double? SuvMax { get; init; }
    public double? SuvMean { get; init; }
    public double? SuvPeak { get; init; }
    public string? SuvUnavailableReason { get; init; }
}

public sealed class MeasurementResult
{
    public double? Length { get; set; }
    public double? AngleDegrees { get; set; }
    public double? Area { get; set; }
    public string LengthUnit { get; set; } = "mm";
    public string AreaUnit { get; set; } = "mm²";
    public string? CalibrationNote { get; set; }
    public RoiStats? Stats { get; set; }
    public RoiStats? SecondaryStats { get; set; }
    public double? ProbeValue { get; set; }
    public double? ProbeSuv { get; set; }
    public string ValueUnit { get; set; } = "";

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public IEnumerable<string> Lines()
    {
        if (Length is { } l) yield return F($"{l:0.0} {LengthUnit}");
        if (AngleDegrees is { } a) yield return F($"{a:0.0}°");
        if (Area is { } ar) yield return F($"Area {ar:0.0} {AreaUnit}");
        if (Stats is { } s)
        {
            if (s.Count > 0)
            {
                yield return F($"Mean {s.Mean:0.0} ± {s.StdDev:0.0} {s.Unit}").TrimEnd();
                yield return F($"Min {s.Min:0.0}  Max {s.Max:0.0}");
            }
            if (s.SuvMax is { } sm) yield return F($"SUVmax {sm:0.00}  SUVmean {s.SuvMean:0.00}") + (s.SuvPeak is { } sp ? F($"  SUVpeak {sp:0.00}") : "");
        }
        if (SecondaryStats is { } p)
        {
            if (p.SuvMax is { } psm) yield return F($"PET SUVmax {psm:0.00}  SUVmean {p.SuvMean:0.00}") + (p.SuvPeak is { } pp ? F($"  SUVpeak {pp:0.00}") : "");
            else if (p.Count > 0) yield return F($"PET max {p.Max:0} {p.Unit} (SUV unavailable)");
        }
        if (ProbeValue is { } v) yield return F($"{v:0.##} {ValueUnit}").TrimEnd();
        if (ProbeSuv is { } ps) yield return F($"SUV {ps:0.00}");
        if (CalibrationNote is not null) yield return CalibrationNote;
    }
}

public static class MeasurementCalculator
{
    /// <summary>Radius of a 1 cm³ sphere in mm, used for SUVpeak.</summary>
    public const double SuvPeakRadiusMm = 6.2035;

    public static MeasurementResult Compute(Annotation a, MeasurementContext ctx)
    {
        var r = new MeasurementResult { ValueUnit = ctx.ValueUnit };
        switch (a.Kind)
        {
            case AnnotationKind.Length when a.Points.Count >= 2:
                (r.Length, r.LengthUnit, r.CalibrationNote) = Distance(a.Points[0], a.Points[1], ctx);
                break;
            case AnnotationKind.Angle when a.Points.Count >= 3:
            {
                var v1 = Physical(a.Points[0] - a.Points[1], a.Points[1], ctx);
                var v2 = Physical(a.Points[2] - a.Points[1], a.Points[1], ctx);
                r.AngleDegrees = Vec3.AngleBetween(v1, v2) * 180 / Math.PI;
                break;
            }
            case AnnotationKind.Ellipse or AnnotationKind.Rectangle or AnnotationKind.Freehand when a.Points.Count >= 2:
                ComputeRoi(a, ctx, r);
                break;
            case AnnotationKind.Probe when a.Points.Count >= 1:
            {
                var (value, slice) = Sample(ctx, a.Points[0]);
                r.ProbeValue = value;
                if (value is { } v && ctx.Suv?.Factor(slice) is { } f) r.ProbeSuv = v * f;
                break;
            }
        }
        return r;
    }

    private static (double, string, string?) Distance(Vec3 p0, Vec3 p1, MeasurementContext ctx)
    {
        if (ctx.PatientSpace) return (Vec3.Distance(p0, p1), "mm", null);
        var w2i = ctx.WorldToIndex;
        var a = w2i.TransformPoint(p0);
        var b = w2i.TransformPoint(p1);
        var spacing = ctx.Calibration?.SpacingFor([(a.X, a.Y), (b.X, b.Y)]);
        if (spacing is { } s)
        {
            var dx = (b.X - a.X) * s.Dx;
            var dy = (b.Y - a.Y) * s.Dy;
            return (Math.Sqrt(dx * dx + dy * dy), "mm", ctx.Calibration!.Caveat);
        }
        return (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)), "px",
            ctx.Calibration?.Regions is not null ? "Points span uncalibrated or different ultrasound regions." : "Uncalibrated: no pixel spacing.");
    }

    private static Vec3 Physical(Vec3 worldDelta, Vec3 at, MeasurementContext ctx)
    {
        if (ctx.PatientSpace) return worldDelta;
        var w2i = ctx.WorldToIndex;
        var d = w2i.TransformVector(worldDelta);
        var origin = w2i.TransformPoint(at);
        var s = ctx.Calibration?.SpacingAt(origin.X, origin.Y);
        return s is { } sp ? new Vec3(d.X * sp.Dx, d.Y * sp.Dy, 0) : new Vec3(d.X, d.Y, 0);
    }

    private static (double? Value, int Slice) Sample(MeasurementContext ctx, Vec3 world)
    {
        if (ctx.Data.Components >= 3) return (null, 0);
        var idx = ctx.WorldToIndex.TransformPoint(world);
        var x = (int)Math.Round(idx.X);
        var y = (int)Math.Round(idx.Y);
        var z = (int)Math.Round(idx.Z);
        if (x < 0 || y < 0 || z < 0 || x >= ctx.Data.Nx || y >= ctx.Data.Ny || z >= ctx.Data.Nz) return (null, z);
        if (ctx.IsSliceLoaded is { } loaded && !loaded(z)) return (null, z);
        return (ctx.Data.GetValue(x, y, z), z);
    }

    private static void ComputeRoi(Annotation a, MeasurementContext ctx, MeasurementResult r)
    {
        var outline = a.Outline();
        var poly = outline.Select(a.ToPlane).ToList();
        var (u0, v0) = a.ToPlane(a.Points[0]);
        var (u1, v1) = a.ToPlane(a.Points[1]);

        if (ctx.PatientSpace)
        {
            r.Area = a.Kind switch
            {
                AnnotationKind.Ellipse => Math.PI * Math.Abs(u1 - u0) / 2 * Math.Abs(v1 - v0) / 2,
                AnnotationKind.Rectangle => Math.Abs(u1 - u0) * Math.Abs(v1 - v0),
                _ => PolygonArea(poly),
            };
        }
        else
        {
            var w2i = ctx.WorldToIndex;
            var pixelPoly = outline.Select(p => { var i = w2i.TransformPoint(p); return (i.X, i.Y); }).ToList();
            var spacing = ctx.Calibration?.SpacingFor(pixelPoly);
            var pixelArea = a.Kind == AnnotationKind.Ellipse
                ? Math.PI * PixelExtent(a, ctx).Rx * PixelExtent(a, ctx).Ry
                : PolygonArea(pixelPoly);
            if (spacing is { } s)
            {
                r.Area = pixelArea * s.Dx * s.Dy;
                r.AreaUnit = "mm²";
                r.CalibrationNote = ctx.Calibration!.Caveat;
            }
            else
            {
                r.Area = pixelArea;
                r.AreaUnit = "px²";
                r.CalibrationNote = "Uncalibrated: no pixel spacing.";
            }
        }

        r.Stats = RoiStatistics(a, ctx, poly);
        if (ctx.Secondary is { } pet) r.SecondaryStats = RoiStatistics(a, pet, poly);
    }

    private static (double Rx, double Ry) PixelExtent(Annotation a, MeasurementContext ctx)
    {
        var w2i = ctx.WorldToIndex;
        var p0 = w2i.TransformPoint(a.Points[0]);
        var p1 = w2i.TransformPoint(a.Points[1]);
        return (Math.Abs(p1.X - p0.X) / 2, Math.Abs(p1.Y - p0.Y) / 2);
    }

    private static RoiStats? RoiStatistics(Annotation a, MeasurementContext ctx, List<(double U, double V)> poly)
    {
        if (ctx.Data.Components >= 3) return null;
        var i2w = ctx.IndexToWorld;
        var spacing = Math.Min(i2w.Column0.Length, i2w.Column1.Length);
        if (ctx.PatientSpace) spacing = Math.Min(spacing, i2w.Column2.Length);
        var step = Math.Max(spacing * 0.5, 0.05);

        var minU = poly.Min(p => p.U);
        var maxU = poly.Max(p => p.U);
        var minV = poly.Min(p => p.V);
        var maxV = poly.Max(p => p.V);
        var (u0, v0) = a.ToPlane(a.Points[0]);
        var (u1, v1) = a.ToPlane(a.Points[1]);
        var cu = (u0 + u1) / 2;
        var cv = (v0 + v1) / 2;
        var ru = Math.Abs(u1 - u0) / 2;
        var rv = Math.Abs(v1 - v0) / 2;

        bool Inside(double u, double v) => a.Kind switch
        {
            AnnotationKind.Ellipse => ru > 0 && rv > 0 && Math.Pow((u - cu) / ru, 2) + Math.Pow((v - cv) / rv, 2) <= 1,
            AnnotationKind.Rectangle => u >= minU && u <= maxU && v >= minV && v <= maxV,
            _ => PointInPolygon(poly, u, v),
        };

        var w2i = ctx.WorldToIndex;
        var data = ctx.Data;
        var seen = new HashSet<long>();
        var voxels = new List<(int X, int Y, int Z)>();
        var maxSamples = 4_000_000;
        var samples = 0;
        for (var u = minU + step / 2; u <= maxU && samples < maxSamples; u += step)
        for (var v = minV + step / 2; v <= maxV && samples < maxSamples; v += step)
        {
            samples++;
            if (!Inside(u, v)) continue;
            var idx = w2i.TransformPoint(a.FromPlane(u, v));
            var x = (int)Math.Round(idx.X);
            var y = (int)Math.Round(idx.Y);
            var z = (int)Math.Round(idx.Z);
            if (x < 0 || y < 0 || z < 0 || x >= data.Nx || y >= data.Ny || z >= data.Nz) continue;
            if (ctx.IsSliceLoaded is { } loaded && !loaded(z)) continue;
            var key = ((long)z * data.Ny + y) * data.Nx + x;
            if (seen.Add(key)) voxels.Add((x, y, z));
        }

        if (voxels.Count == 0) return new RoiStats { Count = 0, Unit = ctx.ValueUnit };
        double sum = 0, sum2 = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var (x, y, z) in voxels)
        {
            var val = data.GetValue(x, y, z);
            sum += val;
            sum2 += val * val;
            if (val < min) min = val;
            if (val > max) max = val;
        }
        var n = voxels.Count;
        var mean = sum / n;
        var sd = n > 1 ? Math.Sqrt(Math.Max(0, (sum2 - n * mean * mean) / (n - 1))) : 0;

        double? suvMax = null, suvMean = null, suvPeak = null;
        string? suvReason = null;
        if (ctx.Suv is { } suv)
        {
            if (suv.IsAvailable)
            {
                double sSum = 0, sMax = double.MinValue;
                var ranked = new List<(double Suv, int X, int Y, int Z)>(n);
                foreach (var (x, y, z) in voxels)
                {
                    var s = data.GetValue(x, y, z) * (suv.Factor(z) ?? 0);
                    sSum += s;
                    if (s > sMax) sMax = s;
                    ranked.Add((s, x, y, z));
                }
                suvMax = sMax;
                suvMean = sSum / n;
                suvPeak = SuvPeak(ctx, suv, ranked);
            }
            else
            {
                suvReason = string.Join(" ", suv.Problems);
            }
        }

        return new RoiStats
        {
            Count = n,
            Mean = mean,
            StdDev = sd,
            Min = min,
            Max = max,
            Unit = ctx.ValueUnit,
            SuvMax = suvMax,
            SuvMean = suvMean,
            SuvPeak = suvPeak,
            SuvUnavailableReason = suvReason,
        };
    }

    /// <summary>Highest mean SUV in a 1 cm³ sphere centred on one of the hottest ROI voxels (PERCIST-style peak).</summary>
    private static double? SuvPeak(MeasurementContext ctx, SuvInfo suv, List<(double Suv, int X, int Y, int Z)> ranked)
    {
        var i2w = ctx.IndexToWorld;
        var sx = i2w.Column0.Length;
        var sy = i2w.Column1.Length;
        var sz = i2w.Column2.Length;
        if (sx <= 0 || sy <= 0 || sz <= 0) return null;
        var rx = (int)Math.Ceiling(SuvPeakRadiusMm / sx);
        var ry = (int)Math.Ceiling(SuvPeakRadiusMm / sy);
        var rz = (int)Math.Ceiling(SuvPeakRadiusMm / sz);
        var data = ctx.Data;
        double? best = null;
        foreach (var c in ranked.OrderByDescending(r => r.Suv).Take(25))
        {
            double sum = 0;
            var count = 0;
            for (var dz = -rz; dz <= rz; dz++)
            for (var dy = -ry; dy <= ry; dy++)
            for (var dx = -rx; dx <= rx; dx++)
            {
                var x = c.X + dx;
                var y = c.Y + dy;
                var z = c.Z + dz;
                if (x < 0 || y < 0 || z < 0 || x >= data.Nx || y >= data.Ny || z >= data.Nz) continue;
                var d = i2w.TransformVector(new Vec3(dx, dy, dz)).Length;
                if (d > SuvPeakRadiusMm) continue;
                sum += data.GetValue(x, y, z) * (suv.Factor(z) ?? 0);
                count++;
            }
            if (count == 0) continue;
            var mean = sum / count;
            if (best is null || mean > best) best = mean;
        }
        return best;
    }

    public static double PolygonArea(IReadOnlyList<(double X, double Y)> p)
    {
        double s = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var j = (i + 1) % p.Count;
            s += p[i].X * p[j].Y - p[j].X * p[i].Y;
        }
        return Math.Abs(s) / 2;
    }

    public static bool PointInPolygon(IReadOnlyList<(double U, double V)> poly, double u, double v)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].V > v) != (poly[j].V > v) &&
                u < (poly[j].U - poly[i].U) * (v - poly[i].V) / (poly[j].V - poly[i].V) + poly[i].U)
                inside = !inside;
        }
        return inside;
    }
}
