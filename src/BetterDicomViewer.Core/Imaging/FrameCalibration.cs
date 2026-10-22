using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Imaging;

/// <summary>
/// How pixel distances on a 2D image map to physical units, following PS3.3 10.7.1.3 for projection radiographs
/// and the Sequence of Ultrasound Regions for ultrasound. Uncalibrated images measure in pixels.
/// </summary>
public sealed class FrameCalibration
{
    public double? ColumnSpacingMm { get; init; }
    public double? RowSpacingMm { get; init; }
    public string Source { get; init; } = "None";
    public string? Caveat { get; init; }
    public UltrasoundRegion[]? Regions { get; init; }

    public bool HasUniformSpacing => ColumnSpacingMm is > 0 && RowSpacingMm is > 0;

    public static FrameCalibration For(InstanceInfo i, int frame = 0)
    {
        var perFrame = i.PerFrame is { } pf && frame < pf.Length ? pf[frame].PixelSpacing : null;
        var ps = perFrame ?? i.PixelSpacing;
        if (ps is not null)
        {
            var type = i.PixelSpacingCalibrationType;
            var projection = i.Modality is "CR" or "DX" or "MG" or "RF" or "XA" or "IO" or "PX";
            return new FrameCalibration
            {
                RowSpacingMm = ps[0],
                ColumnSpacingMm = ps[1],
                Source = string.IsNullOrEmpty(type) ? "Pixel Spacing" : $"Pixel Spacing ({type})",
                Caveat = projection && string.IsNullOrEmpty(type) && i.ImagerPixelSpacing is not null
                    ? "Projection image: calibration plane is not specified."
                    : null,
            };
        }

        if (i.ImagerPixelSpacing is { } ips)
        {
            if (i.EstimatedRadiographicMagnificationFactor is > 0 and var m)
            {
                return new FrameCalibration
                {
                    RowSpacingMm = ips[0] / m,
                    ColumnSpacingMm = ips[1] / m,
                    Source = "Imager Pixel Spacing / magnification factor",
                    Caveat = "Corrected with the estimated radiographic magnification factor.",
                };
            }
            return new FrameCalibration
            {
                RowSpacingMm = ips[0],
                ColumnSpacingMm = ips[1],
                Source = "Imager Pixel Spacing",
                Caveat = "Measured at the detector plane; not corrected for geometric magnification.",
            };
        }

        if (i.UltrasoundRegions is { Length: > 0 } regions && regions.Any(r => r.IsSpatialCentimetres))
        {
            return new FrameCalibration
            {
                Source = "Ultrasound region calibration",
                Regions = regions.Where(r => r.IsSpatialCentimetres).ToArray(),
                Caveat = "Valid only when all points lie in the same calibrated region.",
            };
        }

        return new FrameCalibration { Source = "None", Caveat = "No pixel spacing: measurements are in pixels." };
    }

    /// <summary>Physical size of one pixel at a location, in mm, or null when uncalibrated there.</summary>
    public (double Dx, double Dy)? SpacingAt(double x, double y)
    {
        if (HasUniformSpacing) return (ColumnSpacingMm!.Value, RowSpacingMm!.Value);
        if (Regions is null) return null;
        foreach (var r in Regions)
            if (r.Contains(x, y)) return (r.PhysicalDeltaX * 10.0, r.PhysicalDeltaY * 10.0);
        return null;
    }

    /// <summary>Spacing valid for every point, or null if the points span regions or are uncalibrated.</summary>
    public (double Dx, double Dy)? SpacingFor(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0) return null;
        if (HasUniformSpacing) return (ColumnSpacingMm!.Value, RowSpacingMm!.Value);
        if (Regions is null) return null;
        foreach (var r in Regions)
            if (points.All(p => r.Contains(p.X, p.Y))) return (r.PhysicalDeltaX * 10.0, r.PhysicalDeltaY * 10.0);
        return null;
    }
}
