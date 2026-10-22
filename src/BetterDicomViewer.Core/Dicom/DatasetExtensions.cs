using System.Globalization;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Dicom;

/// <summary>Tolerant accessors: malformed values in real-world files return null instead of throwing.</summary>
public static class DatasetExtensions
{
    public static string? Str(this DicomDataset ds, DicomTag tag)
    {
        try
        {
            if (!ds.Contains(tag)) return null;
            var values = ds.GetValues<string>(tag);
            if (values.Length == 0) return null;
            var joined = values.Length == 1 ? values[0] : string.Join('\\', values);
            joined = joined.Trim().TrimEnd('\0').Trim();
            return joined.Length == 0 ? null : joined;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string[] Strs(this DicomDataset ds, DicomTag tag)
    {
        try
        {
            if (!ds.Contains(tag)) return [];
            return ds.GetValues<string>(tag).Select(v => v.Trim()).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static double? Dbl(this DicomDataset ds, DicomTag tag)
    {
        var values = ds.Dbls(tag);
        return values is { Length: > 0 } ? values[0] : null;
    }

    public static double[]? Dbls(this DicomDataset ds, DicomTag tag)
    {
        if (!ds.Contains(tag)) return null;
        try
        {
            var values = ds.GetValues<double>(tag);
            if (values.Length > 0 && values.All(double.IsFinite)) return values;
        }
        catch (Exception)
        {
        }

        var raw = ds.Strs(tag);
        if (raw.Length == 0) return null;
        var parsed = new List<double>(raw.Length);
        foreach (var r in raw)
        {
            if (double.TryParse(r.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                parsed.Add(v);
            else
                return null;
        }
        return parsed.Count > 0 ? parsed.ToArray() : null;
    }

    public static int? Int(this DicomDataset ds, DicomTag tag)
    {
        if (!ds.Contains(tag)) return null;
        try
        {
            var values = ds.GetValues<int>(tag);
            if (values.Length > 0) return values[0];
        }
        catch (Exception)
        {
        }
        var s = ds.Str(tag);
        if (s is null) return null;
        var first = s.Split('\\')[0].Trim();
        if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < int.MaxValue) return (int)Math.Round(d);
        return null;
    }

    public static DicomDataset? FirstItem(this DicomDataset ds, DicomTag sequenceTag)
    {
        try
        {
            if (!ds.Contains(sequenceTag)) return null;
            var seq = ds.GetSequence(sequenceTag);
            return seq.Items.Count > 0 ? seq.Items[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<DicomDataset> Items(this DicomDataset ds, DicomTag sequenceTag)
    {
        try
        {
            if (!ds.Contains(sequenceTag)) return [];
            return ds.GetSequence(sequenceTag).Items.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Finds a private element by creator string and element offset (low byte), e.g. Philips (7053,xx00).</summary>
    public static double? PrivateDouble(this DicomDataset ds, ushort group, string creator, byte offset)
    {
        foreach (var item in ds)
        {
            if (item.Tag.Group != group || item.Tag.Element < 0x0010 || item.Tag.Element > 0x00FF) continue;
            if (item is not DicomElement creatorElement) continue;
            string value;
            try
            {
                value = creatorElement.Get<string>().Trim();
            }
            catch (Exception)
            {
                continue;
            }
            if (!string.Equals(value, creator, StringComparison.OrdinalIgnoreCase)) continue;
            var tag = new DicomTag(group, (ushort)((item.Tag.Element << 8) | offset));
            return ds.Dbl(tag);
        }
        return null;
    }
}
