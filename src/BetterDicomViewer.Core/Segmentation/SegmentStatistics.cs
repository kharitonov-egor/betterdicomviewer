using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Pet;

namespace BetterDicomViewer.Core.Segmentation;

public sealed class SegmentStats
{
    public byte Label { get; init; }
    public string Name { get; init; } = "";
    public long VoxelCount { get; init; }
    public double VolumeMl { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string Unit { get; init; } = "";
    public double? PetSuvMax { get; init; }
    public double? PetSuvMean { get; init; }
    public double? PetMax { get; init; }
    public string? PetUnit { get; init; }
}

public static class SegmentStatistics
{
    /// <summary>Volume and intensity statistics per segment, plus PET statistics sampled at voxel centres when a PET volume is supplied.</summary>
    public static List<SegmentStats> Compute(LabelMap map, ImageVolume baseVolume, ImageVolume? pet)
    {
        var result = new List<SegmentStats>();
        var acc = new Dictionary<byte, (long N, double Sum, double Sum2, double Min, double Max, double PSum, double PMax, long PN, double SuvSum, double SuvMax)>();
        var petW2I = pet?.WorldToIndex;
        var suv = pet?.Suv;
        unsafe
        {
            var p = map.Pointer;
            for (var z = 0; z < map.Nz; z++)
            for (var y = 0; y < map.Ny; y++)
            for (var x = 0; x < map.Nx; x++)
            {
                var label = p[(z * map.Ny + y) * map.Nx + x];
                if (label == 0) continue;
                var v = baseVolume.Data.GetValue(x, y, z);
                acc.TryGetValue(label, out var a);
                if (a.N == 0)
                {
                    a.Min = double.MaxValue;
                    a.Max = double.MinValue;
                    a.PMax = double.MinValue;
                    a.SuvMax = double.MinValue;
                }
                a.N++;
                a.Sum += v;
                a.Sum2 += v * v;
                a.Min = Math.Min(a.Min, v);
                a.Max = Math.Max(a.Max, v);
                if (pet is not null && petW2I is { } w2i)
                {
                    var world = map.IndexToPatient.TransformPoint(new Vec3(x, y, z));
                    var pi = w2i.TransformPoint(world);
                    var px = (int)Math.Round(pi.X);
                    var py = (int)Math.Round(pi.Y);
                    var pz = (int)Math.Round(pi.Z);
                    if (px >= 0 && py >= 0 && pz >= 0 && px < pet.Nx && py < pet.Ny && pz < pet.Nz)
                    {
                        var pv = pet.Data.GetValue(px, py, pz);
                        a.PN++;
                        a.PSum += pv;
                        a.PMax = Math.Max(a.PMax, pv);
                        if (suv?.Factor(pz) is { } f)
                        {
                            a.SuvSum += pv * f;
                            a.SuvMax = Math.Max(a.SuvMax, pv * f);
                        }
                    }
                }
                acc[label] = a;
            }
        }

        foreach (var seg in map.Segments)
        {
            acc.TryGetValue(seg.Label, out var a);
            var mean = a.N > 0 ? a.Sum / a.N : (double?)null;
            var sd = a.N > 1 ? Math.Sqrt(Math.Max(0, (a.Sum2 - a.N * mean!.Value * mean.Value) / (a.N - 1))) : (double?)null;
            var suvOk = suv is { IsAvailable: true } && a.PN > 0;
            result.Add(new SegmentStats
            {
                Label = seg.Label,
                Name = seg.Name,
                VoxelCount = a.N,
                VolumeMl = a.N * map.VoxelVolumeMm3 / 1000.0,
                Mean = mean,
                StdDev = sd,
                Min = a.N > 0 ? a.Min : null,
                Max = a.N > 0 ? a.Max : null,
                Unit = baseVolume.ValueUnit,
                PetSuvMax = suvOk ? a.SuvMax : null,
                PetSuvMean = suvOk ? a.SuvSum / a.PN : null,
                PetMax = a.PN > 0 ? a.PMax : null,
                PetUnit = pet?.ValueUnit,
            });
        }
        return result;
    }
}
