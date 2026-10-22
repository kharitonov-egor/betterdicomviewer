using System.Globalization;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Deidentification;

/// <summary>One row of PS3.15 Table E.1-1. Tag patterns may contain 'x' wildcards, e.g. 60xx3000.</summary>
internal sealed record ProfileEntry(
    string Name,
    string Tag,
    string Basic,
    string RetainSafePrivate,
    string RetainUids,
    string RetainDeviceIdentity,
    string RetainInstitutionIdentity,
    string RetainPatientCharacteristics,
    string RetainLongitudinalFullDates,
    string RetainLongitudinalModifiedDates,
    string CleanDescriptors,
    string CleanStructuredContent,
    string CleanGraphics);

public enum ProfileAction
{
    Keep,
    Remove,
    ZeroLength,
    Dummy,
    ReplaceUid,
    ShiftDate,
}

/// <summary>Resolves the action for a tag under the Basic Application Level Confidentiality Profile and the chosen options.</summary>
public sealed class ConfidentialityProfile
{
    private readonly Dictionary<uint, ProfileEntry> _exact = [];
    private readonly List<(uint Mask, uint Value, ProfileEntry Entry)> _patterns = [];

    // Attributes that must stay present (Type 1 or 2) in common image IODs; used to resolve X/Z, X/D and X/Z/D.
    private static readonly HashSet<uint> Type1 =
    [
        Key(DicomTag.StudyInstanceUID), Key(DicomTag.SeriesInstanceUID), Key(DicomTag.SOPInstanceUID), Key(DicomTag.FrameOfReferenceUID),
    ];

    private static readonly HashSet<uint> Type2 =
    [
        Key(DicomTag.StudyDate), Key(DicomTag.StudyTime), Key(DicomTag.AccessionNumber), Key(DicomTag.ReferringPhysicianName),
        Key(DicomTag.StudyID), Key(DicomTag.PatientName), Key(DicomTag.PatientID), Key(DicomTag.PatientBirthDate),
        Key(DicomTag.PatientSex), Key(DicomTag.ContentDate), Key(DicomTag.ContentTime), Key(DicomTag.AcquisitionDate),
        Key(DicomTag.AcquisitionTime), Key(DicomTag.SeriesDate), Key(DicomTag.SeriesTime), Key(DicomTag.InstanceCreationDate),
        Key(DicomTag.InstanceCreationTime), Key(DicomTag.SeriesNumber), Key(DicomTag.InstanceNumber),
    ];

    public ConfidentialityProfile(DeidentificationOptions options)
    {
        Options = options;
        foreach (var e in ConfidentialityProfileTable.Entries)
        {
            if (e.Tag.Contains('x'))
            {
                var mask = 0u;
                var value = 0u;
                for (var i = 0; i < 8; i++)
                {
                    mask <<= 4;
                    value <<= 4;
                    if (e.Tag[i] != 'x')
                    {
                        mask |= 0xF;
                        value |= uint.Parse(e.Tag[i].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }
                }
                _patterns.Add((mask, value, e));
            }
            else
            {
                _exact[uint.Parse(e.Tag, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = e;
            }
        }
    }

    public DeidentificationOptions Options { get; }
    public static string Edition => ConfidentialityProfileTable.Edition;
    public int EntryCount => ConfidentialityProfileTable.Entries.Length;

    private static uint Key(DicomTag t) => ((uint)t.Group << 16) | t.Element;

    internal ProfileEntry? Find(DicomTag tag)
    {
        var key = Key(tag);
        if (_exact.TryGetValue(key, out var e)) return e;
        foreach (var (mask, value, entry) in _patterns)
        {
            // Repeating groups (50xx, 60xx) only cover even groups in their range.
            if ((key & mask) == value && (tag.Group & 1) == 0) return entry;
        }
        return null;
    }

    /// <summary>Returns the action for a public attribute. Private attributes are handled by the caller (always removed).</summary>
    public ProfileAction Resolve(DicomTag tag)
    {
        var entry = Find(tag);
        if (entry is null) return ProfileAction.Keep;

        // Options are applied in order of precedence; any option column that says K or C overrides Basic.
        string? optionAction = null;
        if (Options.RetainPatientCharacteristics && entry.RetainPatientCharacteristics.Length > 0) optionAction = entry.RetainPatientCharacteristics;
        if (Options.RetainDeviceIdentity && entry.RetainDeviceIdentity.Length > 0) optionAction = entry.RetainDeviceIdentity;
        if (Options.RetainInstitutionIdentity && entry.RetainInstitutionIdentity.Length > 0) optionAction = entry.RetainInstitutionIdentity;
        if (Options.Dates == DateHandling.KeepFull && entry.RetainLongitudinalFullDates.Length > 0) optionAction = entry.RetainLongitudinalFullDates;
        if (Options.Dates == DateHandling.Shift && entry.RetainLongitudinalModifiedDates.Length > 0) optionAction = entry.RetainLongitudinalModifiedDates == "C" ? "SHIFT" : entry.RetainLongitudinalModifiedDates;
        if (Options.RetainDescriptions && entry.CleanDescriptors.Length > 0) optionAction = "K";

        if (optionAction is not null)
        {
            return optionAction switch
            {
                "K" => ProfileAction.Keep,
                "SHIFT" => ProfileAction.ShiftDate,
                _ => ResolveBasic(optionAction, tag),
            };
        }
        return ResolveBasic(entry.Basic, tag);
    }

    private static ProfileAction ResolveBasic(string action, DicomTag tag)
    {
        var key = Key(tag);
        return action switch
        {
            "X" => ProfileAction.Remove,
            "Z" => ProfileAction.ZeroLength,
            "D" => ProfileAction.Dummy,
            "U" => ProfileAction.ReplaceUid,
            "K" => ProfileAction.Keep,
            "C" => ProfileAction.Remove,
            "Z/D" => Type1.Contains(key) ? ProfileAction.Dummy : ProfileAction.ZeroLength,
            "X/Z" => Type2.Contains(key) || Type1.Contains(key) ? ProfileAction.ZeroLength : ProfileAction.Remove,
            "X/D" => Type1.Contains(key) || Type2.Contains(key) ? ProfileAction.Dummy : ProfileAction.Remove,
            "X/Z/D" => Type1.Contains(key) ? ProfileAction.Dummy : Type2.Contains(key) ? ProfileAction.ZeroLength : ProfileAction.Remove,
            // Keep the reference sequence and let nested UID attributes be remapped, so references stay valid inside the export.
            "X/Z/U*" => ProfileAction.Keep,
            _ => ProfileAction.Remove,
        };
    }
}
