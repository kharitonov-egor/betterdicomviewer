using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;

namespace BetterDicomViewer.Core.Segmentation;

public sealed class Segment
{
    public byte Label { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Opaque RGB colour packed as 0xRRGGBB.</summary>
    public uint Color { get; set; }

    public bool Visible { get; set; } = true;
}

/// <summary>Changed voxels of one edit, for undo and redo.</summary>
public sealed class LabelEdit
{
    public required int[] Indices { get; init; }
    public required byte[] Before { get; init; }
    public required byte[] After { get; init; }
    public int Count => Indices.Length;
}

/// <summary>A uint8 label map on the same voxel grid as its base volume. Label 0 is background.</summary>
public sealed class LabelMap : IDisposable
{
    public static readonly uint[] DefaultColors =
    [
        0xE5484D, 0x46A758, 0x3E63DD, 0xF5D90A, 0xD6409F, 0x12A594, 0xF76B15, 0x8E4EC6, 0x70B8FF, 0xAD7F58,
    ];

    public LabelMap(int nx, int ny, int nz, Affine indexToPatient, string displaySetId, string seriesInstanceUid)
    {
        Labels = new VoxelBuffer(nx, ny, nz, 1, VoxelType.UInt8);
        IndexToPatient = indexToPatient;
        DisplaySetId = displaySetId;
        SeriesInstanceUid = seriesInstanceUid;
    }

    public VoxelBuffer Labels { get; }
    public Affine IndexToPatient { get; }
    public string DisplaySetId { get; }
    public string SeriesInstanceUid { get; }
    public List<Segment> Segments { get; } = [];
    public byte ActiveLabel { get; set; }
    public bool IsDirty { get; set; }
    public int Nx => Labels.Nx;
    public int Ny => Labels.Ny;
    public int Nz => Labels.Nz;

    public event Action? Changed;

    public Segment AddSegment(string? name = null)
    {
        byte label = 1;
        while (Segments.Any(s => s.Label == label) && label < 255) label++;
        var seg = new Segment
        {
            Label = label,
            Name = name ?? $"Segment {label}",
            Color = DefaultColors[(label - 1) % DefaultColors.Length],
        };
        Segments.Add(seg);
        ActiveLabel = label;
        RaiseChanged();
        return seg;
    }

    public void RaiseChanged()
    {
        Labels.MarkModified();
        IsDirty = true;
        Changed?.Invoke();
    }

    public unsafe byte* Pointer => (byte*)Labels.Pointer;

    public void Apply(LabelEdit edit, bool undo)
    {
        unsafe
        {
            var p = Pointer;
            var values = undo ? edit.Before : edit.After;
            for (var i = 0; i < edit.Indices.Length; i++) p[edit.Indices[i]] = values[i];
        }
        RaiseChanged();
    }

    /// <summary>
    /// Paints voxels within <paramref name="radiusMm"/> of <paramref name="center"/>. With a non-zero plane normal the brush is a disk
    /// of half-thickness <paramref name="halfThicknessMm"/> in that plane; otherwise it is a sphere.
    /// </summary>
    public LabelEdit? Paint(Vec3 center, double radiusMm, Vec3 planeNormal, double halfThicknessMm, byte value, Func<long, bool>? constraint = null, byte? onlyOver = null)
    {
        var w2i = IndexToPatient.Inverse();
        var c = w2i.TransformPoint(center);
        var sx = IndexToPatient.Column0.Length;
        var sy = IndexToPatient.Column1.Length;
        var sz = IndexToPatient.Column2.Length;
        var reach = Math.Max(radiusMm, halfThicknessMm);
        var x0 = Math.Max(0, (int)Math.Floor(c.X - reach / sx) - 1);
        var x1 = Math.Min(Nx - 1, (int)Math.Ceiling(c.X + reach / sx) + 1);
        var y0 = Math.Max(0, (int)Math.Floor(c.Y - reach / sy) - 1);
        var y1 = Math.Min(Ny - 1, (int)Math.Ceiling(c.Y + reach / sy) + 1);
        var z0 = Math.Max(0, (int)Math.Floor(c.Z - reach / sz) - 1);
        var z1 = Math.Min(Nz - 1, (int)Math.Ceiling(c.Z + reach / sz) + 1);
        var disk = planeNormal.LengthSquared > 0.5;
        var n = planeNormal.Normalized();
        var r2 = radiusMm * radiusMm;

        var indices = new List<int>();
        var before = new List<byte>();
        unsafe
        {
            var p = Pointer;
            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var d = IndexToPatient.TransformPoint(new Vec3(x, y, z)) - center;
                if (disk)
                {
                    var along = Vec3.Dot(d, n);
                    if (Math.Abs(along) > halfThicknessMm) continue;
                    var inPlane = d - n * along;
                    if (inPlane.LengthSquared > r2) continue;
                }
                else if (d.LengthSquared > r2)
                {
                    continue;
                }
                var idx = (z * Ny + y) * Nx + x;
                var old = p[idx];
                if (old == value) continue;
                if (onlyOver is { } over && old != over) continue;
                if (constraint is not null && !constraint(idx)) continue;
                indices.Add(idx);
                before.Add(old);
                p[idx] = value;
            }
        }
        return Finish(indices, before, value);
    }

    /// <summary>6-connected flood fill from the seed through voxels whose intensity lies in [lo, hi].</summary>
    public LabelEdit? RegionGrow(Vec3 seedWorld, double lo, double hi, VoxelBuffer intensity, byte value, int maxVoxels, int? onlySlice, out bool truncated)
    {
        truncated = false;
        var s = IndexToPatient.Inverse().TransformPoint(seedWorld);
        var sx = (int)Math.Round(s.X);
        var sy = (int)Math.Round(s.Y);
        var sz = (int)Math.Round(s.Z);
        if (sx < 0 || sy < 0 || sz < 0 || sx >= Nx || sy >= Ny || sz >= Nz) return null;
        var seedValue = intensity.GetValue(sx, sy, sz);
        if (seedValue < lo || seedValue > hi) return null;

        // Candidate mask first (typed, tight loops), then a flood fill over flat indices.
        var total = Nx * Ny * Nz;
        var candidate = InRangeMask(intensity, lo, hi, onlySlice);
        var plane = Nx * Ny;
        var queue = new int[Math.Min(total, Math.Max(maxVoxels, 1) + 1)];
        int head = 0, tail = 0;
        var start = (sz * Ny + sy) * Nx + sx;
        candidate[start] = 0;
        queue[tail++] = start;
        var indices = new List<int>();
        var before = new List<byte>();
        var overflow = false;
        unsafe
        {
            var p = Pointer;
            while (head < tail)
            {
                var idx = queue[head++];
                if (p[idx] != value)
                {
                    indices.Add(idx);
                    before.Add(p[idx]);
                    p[idx] = value;
                }
                var x = idx % Nx;
                var y = idx / Nx % Ny;
                var z = idx / plane;
                void Visit(int n)
                {
                    if (candidate[n] == 0) return;
                    candidate[n] = 0;
                    if (tail >= queue.Length)
                    {
                        overflow = true;
                        return;
                    }
                    queue[tail++] = n;
                }
                if (x > 0) Visit(idx - 1);
                if (x < Nx - 1) Visit(idx + 1);
                if (y > 0) Visit(idx - Nx);
                if (y < Ny - 1) Visit(idx + Nx);
                if (z > 0) Visit(idx - plane);
                if (z < Nz - 1) Visit(idx + plane);
            }
        }
        truncated = overflow;
        return Finish(indices, before, value);
    }

    private byte[] InRangeMask(VoxelBuffer intensity, double lo, double hi, int? onlySlice)
    {
        var plane = Nx * Ny;
        var mask = new byte[Nx * Ny * Nz];
        var z0 = onlySlice ?? 0;
        var z1 = onlySlice ?? Nz - 1;
        Parallel.For(z0, z1 + 1, z =>
        {
            var off = z * plane;
            switch (intensity.Type)
            {
                case VoxelType.Int16:
                {
                    var src = intensity.SliceSpan<short>(z);
                    for (var i = 0; i < plane; i++) mask[off + i] = src[i] >= lo && src[i] <= hi ? (byte)1 : (byte)0;
                    break;
                }
                case VoxelType.UInt16:
                {
                    var src = intensity.SliceSpan<ushort>(z);
                    for (var i = 0; i < plane; i++) mask[off + i] = src[i] >= lo && src[i] <= hi ? (byte)1 : (byte)0;
                    break;
                }
                case VoxelType.Float32:
                {
                    var src = intensity.SliceSpan<float>(z);
                    for (var i = 0; i < plane; i++) mask[off + i] = src[i] >= lo && src[i] <= hi ? (byte)1 : (byte)0;
                    break;
                }
                case VoxelType.UInt8:
                {
                    var src = intensity.SliceSpan<byte>(z);
                    for (var i = 0; i < plane; i++) mask[off + i] = src[i] >= lo && src[i] <= hi ? (byte)1 : (byte)0;
                    break;
                }
                default:
                    for (var i = 0; i < plane; i++)
                    {
                        var v = intensity.GetValue(i % Nx, i / Nx, z);
                        mask[off + i] = v >= lo && v <= hi ? (byte)1 : (byte)0;
                    }
                    break;
            }
        });
        return mask;
    }

    /// <summary>Assigns every voxel with intensity in [lo, hi] (optionally only unlabelled voxels).</summary>
    public LabelEdit? ThresholdFill(double lo, double hi, VoxelBuffer intensity, byte value, bool onlyEmpty)
    {
        var mask = InRangeMask(intensity, lo, hi, null);
        var indices = new List<int>();
        var before = new List<byte>();
        unsafe
        {
            var p = Pointer;
            for (var idx = 0; idx < mask.Length; idx++)
            {
                if (mask[idx] == 0 || p[idx] == value || (onlyEmpty && p[idx] != 0)) continue;
                indices.Add(idx);
                before.Add(p[idx]);
                p[idx] = value;
            }
        }
        return Finish(indices, before, value);
    }

    /// <summary>
    /// Shape-based interpolation along the slice axis: fills unlabelled voxels between drawn slices of a segment by
    /// blending signed distance maps of the nearest labelled slices above and below.
    /// </summary>
    public LabelEdit? InterpolateSlices(byte value)
    {
        var labelled = new List<int>();
        unsafe
        {
            var p = Pointer;
            for (var z = 0; z < Nz; z++)
            {
                var off = z * Nx * Ny;
                for (var i = 0; i < Nx * Ny; i++)
                {
                    if (p[off + i] == value)
                    {
                        labelled.Add(z);
                        break;
                    }
                }
            }
        }
        if (labelled.Count < 2) return null;

        var indices = new List<int>();
        var before = new List<byte>();
        for (var g = 0; g + 1 < labelled.Count; g++)
        {
            var za = labelled[g];
            var zb = labelled[g + 1];
            if (zb - za < 2) continue;
            var da = SignedDistance(za, value);
            var db = SignedDistance(zb, value);
            for (var z = za + 1; z < zb; z++)
            {
                var t = (double)(z - za) / (zb - za);
                unsafe
                {
                    var p = Pointer;
                    var off = z * Nx * Ny;
                    for (var i = 0; i < Nx * Ny; i++)
                    {
                        if ((1 - t) * da[i] + t * db[i] <= 0) continue;
                        if (p[off + i] != 0) continue;
                        indices.Add(off + i);
                        before.Add(0);
                        p[off + i] = value;
                    }
                }
            }
        }
        return Finish(indices, before, value);
    }

    /// <summary>Approximate signed distance in mm (positive inside) using a two-pass chamfer transform.</summary>
    private float[] SignedDistance(int z, byte value)
    {
        var sx = (float)IndexToPatient.Column0.Length;
        var sy = (float)IndexToPatient.Column1.Length;
        var inside = new bool[Nx * Ny];
        unsafe
        {
            var p = Pointer;
            var off = z * Nx * Ny;
            for (var i = 0; i < Nx * Ny; i++) inside[i] = p[off + i] == value;
        }
        var dIn = Chamfer(inside, true, sx, sy);
        var dOut = Chamfer(inside, false, sx, sy);
        var result = new float[Nx * Ny];
        for (var i = 0; i < result.Length; i++) result[i] = inside[i] ? dIn[i] : -dOut[i];
        return result;
    }

    private float[] Chamfer(bool[] mask, bool measureInside, float sx, float sy)
    {
        const float inf = 1e9f;
        var d = new float[Nx * Ny];
        for (var i = 0; i < d.Length; i++) d[i] = mask[i] == measureInside ? inf : 0;
        var diag = MathF.Sqrt(sx * sx + sy * sy);
        for (var y = 0; y < Ny; y++)
        for (var x = 0; x < Nx; x++)
        {
            var i = y * Nx + x;
            if (d[i] == 0) continue;
            var v = d[i];
            if (x > 0) v = Math.Min(v, d[i - 1] + sx);
            if (y > 0) v = Math.Min(v, d[i - Nx] + sy);
            if (x > 0 && y > 0) v = Math.Min(v, d[i - Nx - 1] + diag);
            if (x < Nx - 1 && y > 0) v = Math.Min(v, d[i - Nx + 1] + diag);
            d[i] = v;
        }
        for (var y = Ny - 1; y >= 0; y--)
        for (var x = Nx - 1; x >= 0; x--)
        {
            var i = y * Nx + x;
            if (d[i] == 0) continue;
            var v = d[i];
            if (x < Nx - 1) v = Math.Min(v, d[i + 1] + sx);
            if (y < Ny - 1) v = Math.Min(v, d[i + Nx] + sy);
            if (x < Nx - 1 && y < Ny - 1) v = Math.Min(v, d[i + Nx + 1] + diag);
            if (x > 0 && y < Ny - 1) v = Math.Min(v, d[i + Nx - 1] + diag);
            d[i] = v;
        }
        return d;
    }

    /// <summary>Clears every voxel of a segment.</summary>
    public LabelEdit? ClearSegment(byte value)
    {
        var indices = new List<int>();
        var before = new List<byte>();
        unsafe
        {
            var p = Pointer;
            var total = Nx * Ny * Nz;
            for (var i = 0; i < total; i++)
            {
                if (p[i] != value) continue;
                indices.Add(i);
                before.Add(value);
                p[i] = 0;
            }
        }
        return Finish(indices, before, 0);
    }

    private LabelEdit? Finish(List<int> indices, List<byte> before, byte value)
    {
        if (indices.Count == 0) return null;
        RaiseChanged();
        var after = new byte[indices.Count];
        Array.Fill(after, value);
        return new LabelEdit { Indices = indices.ToArray(), Before = before.ToArray(), After = after };
    }

    public long Count(byte value)
    {
        long n = 0;
        unsafe
        {
            var p = Pointer;
            var total = (long)Nx * Ny * Nz;
            for (long i = 0; i < total; i++)
                if (p[i] == value) n++;
        }
        return n;
    }

    public double VoxelVolumeMm3 => Math.Abs(IndexToPatient.Determinant);

    public void Dispose() => Labels.Dispose();
}
