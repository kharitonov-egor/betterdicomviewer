using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BetterDicomViewer.Core.Dicom;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Deidentification;

public enum DateHandling
{
    /// <summary>Basic profile: dates and times are removed or replaced with dummy values.</summary>
    Remove,

    /// <summary>Retain Longitudinal Temporal Information with Modified Dates: every date shifts by the same number of days.</summary>
    Shift,

    /// <summary>Retain Longitudinal Temporal Information with Full Dates.</summary>
    KeepFull,
}

public enum BurnedInPolicy
{
    /// <summary>Objects that declare or may contain burned-in text are not exported.</summary>
    Exclude,

    /// <summary>Export them unchanged and list them in the report.</summary>
    IncludeAndFlag,

    /// <summary>Black out user-defined rectangles in the pixel data, then export and list them.</summary>
    Redact,
}

public sealed record RedactionRegion(int X, int Y, int Width, int Height);

public sealed class DeidentificationOptions
{
    public string PatientName { get; set; } = "ANONYMOUS";
    public string PatientId { get; set; } = "ANON0001";
    public DateHandling Dates { get; set; } = DateHandling.Remove;
    public int DateShiftDays { get; set; } = -365;
    public bool RetainPatientCharacteristics { get; set; }
    public bool RetainDeviceIdentity { get; set; }
    public bool RetainInstitutionIdentity { get; set; }

    /// <summary>Non-standard: keeps study/series descriptions verbatim. They can contain identifying text, so the report flags it.</summary>
    public bool RetainDescriptions { get; set; }

    public BurnedInPolicy BurnedIn { get; set; } = BurnedInPolicy.Exclude;

    /// <summary>Redaction rectangles keyed by "{Columns}x{Rows}" image size, applied to every frame of that size.</summary>
    public Dictionary<string, List<RedactionRegion>> Redactions { get; set; } = [];

    public static string SizeKey(int columns, int rows) => $"{columns}x{rows}";
}

/// <summary>
/// Applies the PS3.15 Annex E Basic Application Level Confidentiality Profile to datasets, recursing into sequences,
/// removing all private attributes and remapping UIDs consistently across every dataset processed by this instance.
/// </summary>
public sealed class Deidentifier
{
    private readonly ConfidentialityProfile _profile;
    private readonly Dictionary<string, string> _uidMap = new(StringComparer.Ordinal);
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    public Deidentifier(DeidentificationOptions options)
    {
        Options = options;
        _profile = new ConfidentialityProfile(options);
    }

    public DeidentificationOptions Options { get; }
    public int UidsRemapped => _uidMap.Count;
    public IReadOnlyDictionary<string, string> UidMap => _uidMap;

    public string MapUid(string original)
    {
        lock (_uidMap)
        {
            if (_uidMap.TryGetValue(original, out var mapped)) return mapped;
            // 2.25 UIDs derived from a salted hash: consistent within one export, unlinkable across exports.
            var hash = HMACSHA256.HashData(_salt, Encoding.ASCII.GetBytes(original));
            var bytes = new byte[17];
            Array.Copy(hash, bytes, 16);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            var value = new BigInteger(bytes.AsSpan(0, 17), isUnsigned: true, isBigEndian: true);
            mapped = "2.25." + value.ToString(CultureInfo.InvariantCulture);
            _uidMap[original] = mapped;
            return mapped;
        }
    }

    /// <summary>Returns a de-identified copy. The source dataset is not modified.</summary>
    public DicomDataset Process(DicomDataset source)
    {
        var ds = source.Clone();
        ProcessInPlace(ds, depth: 0);

        ds.AddOrUpdate(DicomTag.PatientName, Options.PatientName);
        ds.AddOrUpdate(DicomTag.PatientID, Options.PatientId);
        ds.AddOrUpdate(DicomTag.PatientIdentityRemoved, "YES");
        ds.AddOrUpdate(DicomTag.DeidentificationMethod, MethodDescriptions());
        ds.AddOrUpdate(new DicomSequence(DicomTag.DeidentificationMethodCodeSequence, MethodCodes().ToArray()));
        return ds;
    }

    private void ProcessInPlace(DicomDataset ds, int depth)
    {
        var items = ds.ToList();
        foreach (var item in items)
        {
            var tag = item.Tag;
            if (tag.IsPrivate)
            {
                ds.Remove(tag);
                continue;
            }
            if (tag.Element == 0x0000) continue;
            if (tag == DicomTag.PixelData || tag.Group == 0x7FE0 || tag.Group == 0x0002) continue;

            var action = _profile.Resolve(tag);
            switch (action)
            {
                case ProfileAction.Remove:
                    ds.Remove(tag);
                    break;
                case ProfileAction.ZeroLength:
                    if (item is DicomSequence) ds.AddOrUpdate(new DicomSequence(tag));
                    else ReplaceWithEmpty(ds, item);
                    break;
                case ProfileAction.Dummy:
                    if (item is DicomSequence) ds.AddOrUpdate(new DicomSequence(tag));
                    else ReplaceWithDummy(ds, item);
                    break;
                case ProfileAction.ReplaceUid:
                    if (item is DicomElement uidElement && uidElement.ValueRepresentation == DicomVR.UI)
                    {
                        var values = ds.GetValues<string>(tag).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => MapUid(v.Trim())).ToArray();
                        ds.AddOrUpdate(DicomVR.UI, tag, values);
                    }
                    else if (item is DicomSequence uidSeq)
                    {
                        foreach (var child in uidSeq.Items) ProcessInPlace(child, depth + 1);
                    }
                    break;
                case ProfileAction.ShiftDate:
                    ShiftDate(ds, item);
                    break;
                case ProfileAction.Keep:
                    if (item is DicomSequence seq)
                        foreach (var child in seq.Items) ProcessInPlace(child, depth + 1);
                    break;
            }
        }
    }

    private static void ReplaceWithEmpty(DicomDataset ds, DicomItem item)
    {
        if (item is DicomElement e) ds.AddOrUpdate(e.ValueRepresentation, item.Tag, Array.Empty<string>());
    }

    private void ReplaceWithDummy(DicomDataset ds, DicomItem item)
    {
        if (item is not DicomElement e) return;
        var vr = e.ValueRepresentation;
        string? dummy = vr.Code switch
        {
            "PN" => "ANONYMOUS",
            "LO" or "LT" or "ST" or "UT" or "SH" or "UC" => "ANONYMIZED",
            "CS" => "ANON",
            "AE" => "ANON",
            "DA" => "19000101",
            "TM" => "000000",
            "DT" => "19000101000000",
            "AS" => "000D",
            "DS" or "IS" => "0",
            "UI" => MapUid(SafeString(ds, item.Tag) ?? Guid.NewGuid().ToString()),
            "UR" => "",
            _ => null,
        };
        if (dummy is null)
        {
            ds.Remove(item.Tag);
            return;
        }
        if (vr == DicomVR.DA && Options.Dates == DateHandling.Shift && SafeString(ds, item.Tag) is { } original)
            dummy = ShiftDa(original) ?? dummy;
        ds.AddOrUpdate(vr, item.Tag, dummy);
    }

    private void ShiftDate(DicomDataset ds, DicomItem item)
    {
        if (item is not DicomElement e) return;
        var value = SafeString(ds, item.Tag);
        if (string.IsNullOrEmpty(value)) return;
        if (e.ValueRepresentation == DicomVR.DA)
        {
            var shifted = ShiftDa(value);
            if (shifted is null) ds.Remove(item.Tag);
            else ds.AddOrUpdate(DicomVR.DA, item.Tag, shifted);
        }
        else if (e.ValueRepresentation == DicomVR.DT)
        {
            if (value.Length >= 8 && ShiftDa(value[..8]) is { } d) ds.AddOrUpdate(DicomVR.DT, item.Tag, d + value[8..]);
            else ds.Remove(item.Tag);
        }
        // TM values carry no date and are kept by the Modified Dates option.
    }

    private string? ShiftDa(string da)
    {
        var d = DicomDates.ParseDate(da);
        return d?.AddDays(Options.DateShiftDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    private static string? SafeString(DicomDataset ds, DicomTag tag)
    {
        try
        {
            return ds.GetString(tag);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>De-identification Method (0012,0063) values; LO allows 64 characters per value.</summary>
    public string[] MethodDescriptions()
    {
        var parts = new List<string> { "PS3.15 E.1 Basic Appl. Level Confidentiality Profile", $"Better DICOM Viewer, table {ConfidentialityProfileTable.Edition.Split(' ')[2]}" };
        if (Options.RetainPatientCharacteristics) parts.Add("Retain Patient Characteristics Option");
        if (Options.RetainDeviceIdentity) parts.Add("Retain Device Identity Option");
        if (Options.RetainInstitutionIdentity) parts.Add("Retain Institution Identity Option");
        if (Options.Dates == DateHandling.Shift) parts.Add("Retain Longitudinal Temporal Info Modified Dates Option");
        if (Options.Dates == DateHandling.KeepFull) parts.Add("Retain Longitudinal Temporal Info Full Dates Option");
        if (Options.RetainDescriptions) parts.Add("Descriptions retained without cleaning");
        return parts.Select(p => p.Length <= 64 ? p : p[..64]).ToArray();
    }

    private IEnumerable<DicomDataset> MethodCodes()
    {
        yield return Code("113100", "Basic Application Confidentiality Profile");
        if (Options.RetainPatientCharacteristics) yield return Code("113108", "Retain Patient Characteristics Option");
        if (Options.RetainDeviceIdentity) yield return Code("113109", "Retain Device Identity Option");
        if (Options.RetainInstitutionIdentity) yield return Code("113112", "Retain Institution Identity Option");
        if (Options.Dates == DateHandling.Shift) yield return Code("113107", "Retain Longitudinal Temporal Information Modified Dates Option");
        if (Options.Dates == DateHandling.KeepFull) yield return Code("113106", "Retain Longitudinal Temporal Information Full Dates Option");
    }

    private static DicomDataset Code(string value, string meaning) => new()
    {
        { DicomTag.CodeValue, value },
        { DicomTag.CodingSchemeDesignator, "DCM" },
        { DicomTag.CodeMeaning, meaning },
    };
}
