using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Segmentation;

/// <summary>
/// Label map persistence. NRRD files use LPS space with per-segment key/value fields in the style of 3D Slicer's
/// .seg.nrrd; NIfTI-1 files use RAS, as that format requires.
/// </summary>
public static class LabelMapIo
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void SaveNrrd(LabelMap map, string path)
    {
        var a = map.IndexToPatient;
        var sb = new StringBuilder();
        sb.Append("NRRD0004\n");
        sb.Append("# Better DICOM Viewer segmentation\n");
        sb.Append("type: uint8\n");
        sb.Append("dimension: 3\n");
        sb.Append("space: left-posterior-superior\n");
        sb.Append(Ci, $"sizes: {map.Nx} {map.Ny} {map.Nz}\n");
        sb.Append(Ci, $"space directions: {V(a.Column0)} {V(a.Column1)} {V(a.Column2)}\n");
        sb.Append("kinds: domain domain domain\n");
        sb.Append("endian: little\n");
        sb.Append("encoding: gzip\n");
        sb.Append(Ci, $"space origin: {V(a.Translation)}\n");
        sb.Append(Ci, $"BDV_DisplaySet:={map.DisplaySetId}\n");
        for (var i = 0; i < map.Segments.Count; i++)
        {
            var s = map.Segments[i];
            sb.Append(Ci, $"Segment{i}_ID:=Segment_{s.Label}\n");
            sb.Append(Ci, $"Segment{i}_Name:={s.Name.Replace('\n', ' ')}\n");
            sb.Append(Ci, $"Segment{i}_Color:={((s.Color >> 16) & 0xFF) / 255.0:0.####} {((s.Color >> 8) & 0xFF) / 255.0:0.####} {(s.Color & 0xFF) / 255.0:0.####}\n");
            sb.Append(Ci, $"Segment{i}_LabelValue:={s.Label}\n");
            sb.Append(Ci, $"Segment{i}_Layer:=0\n");
        }
        sb.Append('\n');

        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            var header = Encoding.ASCII.GetBytes(sb.ToString());
            fs.Write(header);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: true);
            unsafe
            {
                var span = new ReadOnlySpan<byte>(map.Pointer, map.Nx * map.Ny * map.Nz);
                gz.Write(span);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads a label map saved by <see cref="SaveNrrd"/> if its grid matches the expected dimensions and geometry.</summary>
    public static bool TryLoadNrrd(string path, LabelMap target)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        var headerBytes = new List<byte>();
        int prev = -1, b;
        while ((b = fs.ReadByte()) >= 0)
        {
            headerBytes.Add((byte)b);
            if (b == '\n' && prev == '\n') break;
            prev = b;
        }
        var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split('\n');
        if (!lines[0].StartsWith("NRRD", StringComparison.Ordinal)) return false;
        var fields = new Dictionary<string, string>();
        var kv = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var k = line.IndexOf(":=", StringComparison.Ordinal);
            if (k > 0)
            {
                kv[line[..k]] = line[(k + 2)..];
                continue;
            }
            var c = line.IndexOf(": ", StringComparison.Ordinal);
            if (c > 0) fields[line[..c]] = line[(c + 2)..];
        }
        var sizes = fields.GetValueOrDefault("sizes")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, Ci)).ToArray();
        if (sizes is null || sizes.Length != 3 || sizes[0] != target.Nx || sizes[1] != target.Ny || sizes[2] != target.Nz) return false;
        if (fields.GetValueOrDefault("type") is not ("uint8" or "unsigned char" or "uchar")) return false;
        var origin = ParseVector(fields.GetValueOrDefault("space origin"));
        if (origin is null || Vec3.Distance(origin.Value, target.IndexToPatient.Translation) > 0.01) return false;

        Stream data = fields.GetValueOrDefault("encoding") is "gzip" or "gz" ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        unsafe
        {
            var span = new Span<byte>(target.Pointer, target.Nx * target.Ny * target.Nz);
            data.ReadExactly(span);
        }

        target.Segments.Clear();
        for (var i = 0; kv.ContainsKey($"Segment{i}_LabelValue"); i++)
        {
            var color = kv.GetValueOrDefault($"Segment{i}_Color")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => (uint)Math.Round(double.Parse(s, Ci) * 255)).ToArray();
            target.Segments.Add(new Segment
            {
                Label = byte.Parse(kv[$"Segment{i}_LabelValue"], Ci),
                Name = kv.GetValueOrDefault($"Segment{i}_Name") ?? $"Segment {i + 1}",
                Color = color is { Length: 3 } ? (color[0] << 16) | (color[1] << 8) | color[2] : LabelMap.DefaultColors[i % LabelMap.DefaultColors.Length],
            });
        }
        target.ActiveLabel = target.Segments.FirstOrDefault()?.Label ?? 0;
        target.Labels.MarkModified();
        target.IsDirty = false;
        return true;
    }

    private static Vec3? ParseVector(string? s)
    {
        if (s is null) return null;
        var parts = s.Trim().Trim('(', ')').Split(',');
        if (parts.Length != 3) return null;
        return new Vec3(double.Parse(parts[0], Ci), double.Parse(parts[1], Ci), double.Parse(parts[2], Ci));
    }

    private static string V(Vec3 v) => string.Create(Ci, $"({v.X:0.########},{v.Y:0.########},{v.Z:0.########})");

    /// <summary>Writes a gzip-compressed NIfTI-1 file. The sform converts LPS to RAS by negating x and y.</summary>
    public static void SaveNifti(LabelMap map, string path)
    {
        var header = new byte[352];
        var h = header.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 348);
        BinaryPrimitives.WriteInt16LittleEndian(h[40..], 3);
        BinaryPrimitives.WriteInt16LittleEndian(h[42..], (short)map.Nx);
        BinaryPrimitives.WriteInt16LittleEndian(h[44..], (short)map.Ny);
        BinaryPrimitives.WriteInt16LittleEndian(h[46..], (short)map.Nz);
        for (var i = 4; i <= 7; i++) BinaryPrimitives.WriteInt16LittleEndian(h[(40 + i * 2)..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[70..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[72..], 8);
        var a = map.IndexToPatient;
        BinaryPrimitives.WriteSingleLittleEndian(h[76..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(h[80..], (float)a.Column0.Length);
        BinaryPrimitives.WriteSingleLittleEndian(h[84..], (float)a.Column1.Length);
        BinaryPrimitives.WriteSingleLittleEndian(h[88..], (float)a.Column2.Length);
        BinaryPrimitives.WriteSingleLittleEndian(h[108..], 352f);
        BinaryPrimitives.WriteSingleLittleEndian(h[112..], 1f);
        h[123] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(h[252..], 0);
        BinaryPrimitives.WriteInt16LittleEndian(h[254..], 2);
        float[] row0 = [(float)-a.M00, (float)-a.M01, (float)-a.M02, (float)-a.Tx];
        float[] row1 = [(float)-a.M10, (float)-a.M11, (float)-a.M12, (float)-a.Ty];
        float[] row2 = [(float)a.M20, (float)a.M21, (float)a.M22, (float)a.Tz];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(h[(280 + i * 4)..], row0[i]);
            BinaryPrimitives.WriteSingleLittleEndian(h[(296 + i * 4)..], row1[i]);
            BinaryPrimitives.WriteSingleLittleEndian(h[(312 + i * 4)..], row2[i]);
        }
        Encoding.ASCII.GetBytes("Better DICOM Viewer labels").CopyTo(h[148..]);
        Encoding.ASCII.GetBytes("n+1\0").CopyTo(h[344..]);

        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(header);
        unsafe
        {
            gz.Write(new ReadOnlySpan<byte>(map.Pointer, map.Nx * map.Ny * map.Nz));
        }
    }
}
