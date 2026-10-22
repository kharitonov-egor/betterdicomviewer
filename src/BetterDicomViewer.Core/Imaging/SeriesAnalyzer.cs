using System.Globalization;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.Core.Imaging;

/// <summary>
/// Splits a series into display sets. Slices are ordered by their position along the plane normal
/// (never by file name), localizers and differently oriented images are separated, and uneven spacing is resampled with a warning.
/// </summary>
public static class SeriesAnalyzer
{
    public const double UniformSpacingTolerance = 0.02;
    private const double OrientationTolerance = 1e-3;

    private sealed record Placed(FrameRef Frame, Vec3 Position, Vec3 Row, Vec3 Col, double RowSpacing, double ColSpacing);

    public static List<DisplaySet> Analyze(SeriesKey key, IReadOnlyList<InstanceInfo> instances)
    {
        var result = new List<DisplaySet>();
        var images = instances.Where(i => i.HasPixelData && !i.IsProvisional && SupportPolicy.UnsupportedReason(i) is null).ToList();
        var counter = 0;
        string NextId() => $"{key.SeriesInstanceUid}#{counter++}";

        foreach (var mf in images.Where(i => i.IsMultiFrame))
        {
            var frames = Enumerable.Range(0, mf.NumberOfFrames).Select(f => new FrameRef(mf, f)).ToList();
            var placed = mf.PerFrame is { } pf && pf.Length == mf.NumberOfFrames ? PlaceFrames(frames, mf) : null;
            if (placed is not null && TryBuildVolume(key, NextId(), placed, mf.Modality, mf.SamplesPerPixel, out var volume))
            {
                result.Add(volume!);
                continue;
            }
            var label = mf.SamplesPerPixel >= 3 || mf.FrameTimeMs is not null || mf.CineRate is not null
                ? $"Cine, {mf.NumberOfFrames} frames"
                : $"{mf.NumberOfFrames} frames";
            if (mf.InstanceNumber is { } n) label += $" (#{n})";
            result.Add(new DisplaySet
            {
                Id = NextId(),
                Series = key,
                Label = label,
                Kind = DisplaySetKind.Frames,
                Frames = frames,
                Rows = mf.Rows,
                Columns = mf.Columns,
                SamplesPerPixel = mf.SamplesPerPixel,
                Modality = mf.Modality,
            });
        }

        var singles = images.Where(i => !i.IsMultiFrame).ToList();
        foreach (var group in singles.GroupBy(GroupKey).OrderByDescending(g => g.Count()))
        {
            var list = group.ToList();
            var first = list[0];
            var placed = PlaceSingles(list);
            if (placed is not null && placed.Count >= 3 && TryBuildVolumes(key, NextId, placed, first.Modality, first.SamplesPerPixel, result))
                continue;

            var ordered = list.OrderBy(i => i.InstanceNumber ?? int.MaxValue)
                .ThenBy(i => i.AcquisitionTime ?? i.ContentTime ?? "", StringComparer.Ordinal)
                .ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(i => new FrameRef(i, 0))
                .ToList();
            var label = first.IsLocalizer ? (list.Count == 1 ? "Localizer" : $"Localizers, {list.Count} images")
                : list.Count == 1 ? "1 image" : $"{list.Count} images";
            if (placed is not null && placed.Count == list.Count && !first.IsLocalizer)
                label = $"{OrientationNames.PlaneName(Vec3.Cross(placed[0].Row, placed[0].Col))}, {label}";
            result.Add(new DisplaySet
            {
                Id = NextId(),
                Series = key,
                Label = label,
                Kind = DisplaySetKind.Frames,
                Frames = ordered,
                Rows = first.Rows,
                Columns = first.Columns,
                SamplesPerPixel = first.SamplesPerPixel,
                Modality = first.Modality,
            });
        }

        return result
            .OrderBy(d => d.Kind == DisplaySetKind.Volume ? 0 : 1)
            .ThenBy(d => d.Frames.Any(f => f.Instance.IsLocalizer) ? 1 : 0)
            .ThenByDescending(d => d.SliceCount)
            .ToList();
    }

    private static string GroupKey(InstanceInfo i)
    {
        var iop = i.ImageOrientationPatient is { } o
            ? string.Join(",", o.Select(v => Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture)))
            : "none";
        var ps = i.PixelSpacing is { } p
            ? $"{p[0].ToString("0.0000", CultureInfo.InvariantCulture)},{p[1].ToString("0.0000", CultureInfo.InvariantCulture)}"
            : "none";
        return $"{i.Rows}x{i.Columns}|{i.SamplesPerPixel}|{i.BitsAllocated}|{(i.IsLocalizer ? "L" : "-")}|{iop}|{ps}|{i.FrameOfReferenceUid}";
    }

    private static List<Placed>? PlaceSingles(List<InstanceInfo> list)
    {
        var placed = new List<Placed>(list.Count);
        foreach (var i in list)
        {
            if (i.ImagePositionPatient is not { } ipp || i.ImageOrientationPatient is not { } iop || i.PixelSpacing is not { } ps) return null;
            var row = new Vec3(iop[0], iop[1], iop[2]).Normalized();
            var col = new Vec3(iop[3], iop[4], iop[5]).Normalized();
            if (row.LengthSquared < 0.5 || col.LengthSquared < 0.5 || Math.Abs(Vec3.Dot(row, col)) > 0.01) return null;
            placed.Add(new Placed(new FrameRef(i, 0), new Vec3(ipp[0], ipp[1], ipp[2]), row, col, ps[0], ps[1]));
        }
        return placed;
    }

    private static List<Placed>? PlaceFrames(List<FrameRef> frames, InstanceInfo mf)
    {
        var placed = new List<Placed>(frames.Count);
        foreach (var f in frames)
        {
            var g = mf.PerFrame![f.FrameIndex];
            var ipp = g.ImagePositionPatient;
            var iop = g.ImageOrientationPatient ?? mf.ImageOrientationPatient;
            var ps = g.PixelSpacing ?? mf.PixelSpacing;
            if (ipp is null || iop is null || ps is null) return null;
            var row = new Vec3(iop[0], iop[1], iop[2]).Normalized();
            var col = new Vec3(iop[3], iop[4], iop[5]).Normalized();
            placed.Add(new Placed(f, new Vec3(ipp[0], ipp[1], ipp[2]), row, col, ps[0], ps[1]));
        }
        return placed;
    }

    private static bool TryBuildVolumes(SeriesKey key, Func<string> nextId, List<Placed> placed, string? modality, int spp, List<DisplaySet> output)
    {
        var normal = Vec3.Cross(placed[0].Row, placed[0].Col).Normalized();
        var distinct = placed.Select(p => Math.Round(Vec3.Dot(p.Position, normal), 2)).Distinct().Count();
        if (distinct == placed.Count)
        {
            if (!TryBuildVolume(key, nextId(), placed, modality, spp, out var v)) return false;
            output.Add(v!);
            if (v!.Geometry!.IsResampled)
            {
                output.Add(new DisplaySet
                {
                    Id = nextId(),
                    Series = key,
                    Label = $"Native slices, {placed.Count} images",
                    Kind = DisplaySetKind.Frames,
                    Frames = v.Frames,
                    Rows = v.Rows,
                    Columns = v.Columns,
                    SamplesPerPixel = spp,
                    Modality = modality,
                });
            }
            return true;
        }

        // Repeated positions: separate acquisitions or phases that share a series. Try splitting by acquisition number.
        var byAcq = placed.GroupBy(p => p.Frame.Instance.AcquisitionNumber ?? -1).ToList();
        if (byAcq.Count > 1 && byAcq.All(g => g.Select(p => Math.Round(Vec3.Dot(p.Position, normal), 2)).Distinct().Count() == g.Count() && g.Count() >= 3))
        {
            var built = new List<DisplaySet>();
            foreach (var g in byAcq.OrderBy(g => g.Key))
            {
                if (!TryBuildVolume(key, nextId(), g.ToList(), modality, spp, out var v)) return false;
                v!.Warnings.Add($"Acquisition {g.Key} of {byAcq.Count} in this series.");
                built.Add(v);
            }
            output.AddRange(built);
            return true;
        }
        return false;
    }

    private static bool TryBuildVolume(SeriesKey key, string id, List<Placed> placed, string? modality, int spp, out DisplaySet? set)
    {
        set = null;
        if (placed.Count < 2) return false;
        var p0 = placed[0];
        var normal = Vec3.Cross(p0.Row, p0.Col).Normalized();
        foreach (var p in placed)
        {
            if (Vec3.Distance(p.Row, p0.Row) > OrientationTolerance * 10 || Vec3.Distance(p.Col, p0.Col) > OrientationTolerance * 10) return false;
            if (Math.Abs(p.RowSpacing - p0.RowSpacing) > 1e-3 || Math.Abs(p.ColSpacing - p0.ColSpacing) > 1e-3) return false;
        }

        var sorted = placed.OrderBy(p => Vec3.Dot(p.Position, normal)).ToList();
        var d = sorted.Select(p => Vec3.Dot(p.Position, normal)).ToArray();
        var gaps = new double[d.Length - 1];
        for (var i = 0; i < gaps.Length; i++) gaps[i] = d[i + 1] - d[i];
        if (gaps.Any(g => g < 1e-3)) return false;

        var sortedGaps = gaps.OrderBy(g => g).ToArray();
        var median = sortedGaps[sortedGaps.Length / 2];

        // Sparse stacks (a few slices far apart, e.g. saved key images) are not volumes.
        var thickness = p0.Frame.Instance.SliceThickness ?? (p0.Frame.Instance.PerFrame?[p0.Frame.FrameIndex].SliceThickness);
        var maxContiguousGap = thickness is > 0 ? Math.Max(2.5 * thickness.Value + 1.0, 4 * Math.Max(p0.RowSpacing, p0.ColSpacing))
            : 12 * Math.Max(p0.RowSpacing, p0.ColSpacing) + 2.0;
        if (median > maxContiguousGap) return false;
        var maxDeviation = gaps.Max(g => Math.Abs(g - median));
        var uniform = maxDeviation <= Math.Max(UniformSpacingTolerance * median, 0.01);

        // In-plane offsets must follow a straight line (allowing gantry tilt) or the stack is not a volume.
        var first = sorted[0].Position;
        var last = sorted[^1].Position;
        var span = d[^1] - d[0];
        var minPixel = Math.Min(p0.RowSpacing, p0.ColSpacing);
        foreach (var (p, dist) in sorted.Zip(d))
        {
            var t = (dist - d[0]) / span;
            var expected = Vec3.Lerp(first, last, t);
            if (Vec3.Distance(expected, p.Position) > Math.Max(0.5 * minPixel, 0.05)) return false;
        }

        var totalStep = (last - first) / span;
        var tiltVector = totalStep - normal * Vec3.Dot(totalStep, normal);
        var hasTilt = tiltVector.Length > 0.01;

        int nz;
        double spacing;
        List<SliceSource> sources;
        if (uniform)
        {
            nz = sorted.Count;
            spacing = span / (nz - 1);
            sources = Enumerable.Range(0, nz).Select(k => new SliceSource(k, k, 0)).ToList();
        }
        else
        {
            spacing = median;
            nz = (int)Math.Floor(span / spacing + 1e-6) + 1;
            sources = new List<SliceSource>(nz);
            var a = 0;
            for (var k = 0; k < nz; k++)
            {
                var target = d[0] + k * spacing;
                while (a < d.Length - 2 && d[a + 1] < target) a++;
                var b = Math.Min(a + 1, d.Length - 1);
                var w = b == a ? 0 : Math.Clamp((target - d[a]) / (d[b] - d[a]), 0, 1);
                sources.Add(new SliceSource(a, b, w));
            }
        }

        var step = totalStep * spacing;
        var colStep = p0.Row * p0.ColSpacing;
        var rowStep = p0.Col * p0.RowSpacing;
        var indexToPatient = Affine.FromColumns(colStep, rowStep, step, first);

        var instanceNumbers = sorted.Select(p => p.Frame.Instance.InstanceNumber ?? 0).ToArray();
        var direction = instanceNumbers.Length > 1 && instanceNumbers[^1] < instanceNumbers[0] ? -1 : 1;

        var inst = p0.Frame.Instance;
        set = new DisplaySet
        {
            Id = id,
            Series = key,
            Label = $"{OrientationNames.PlaneName(normal)}, {sorted.Count} {(inst.IsMultiFrame ? "frames" : "images")}",
            Kind = DisplaySetKind.Volume,
            Frames = sorted.Select(p => p.Frame).ToList(),
            Rows = inst.Rows,
            Columns = inst.Columns,
            SamplesPerPixel = spp,
            Modality = modality,
            Geometry = new VolumeGeometry
            {
                Nx = inst.Columns,
                Ny = inst.Rows,
                Nz = nz,
                IndexToPatient = indexToPatient,
                RowDirection = p0.Row,
                ColumnDirection = p0.Col,
                Normal = normal,
                Spacing = new Vec3(p0.ColSpacing, p0.RowSpacing, spacing),
                SliceSources = sources,
                IsResampled = !uniform,
                HasGantryTilt = hasTilt,
                MaxSpacingDeviationMm = maxDeviation,
                AcquisitionDirection = direction,
            },
        };

        if (!uniform)
        {
            var biggest = gaps.Max();
            var smallest = gaps.Min();
            set.Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Uneven slice spacing ({smallest:0.###} to {biggest:0.###} mm, median {median:0.###} mm). Resampled to {spacing:0.###} mm for this volume. The original slices are listed separately as Native slices."));
            if (biggest > 2.5 * median) set.Warnings.Add("Large gap between slices: some slices may be missing.");
        }
        if (hasTilt)
            set.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Gantry tilt or sheared stack detected ({Math.Atan(tiltVector.Length) * 180 / Math.PI:0.#}°). Geometry uses the sheared slice positions."));
        return true;
    }
}
