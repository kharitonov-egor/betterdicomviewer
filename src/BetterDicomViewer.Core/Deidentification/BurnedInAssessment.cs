using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Deidentification;

/// <summary>
/// Flags images that declare or are likely to contain identifying text in the pixel data. Tag removal cannot clean these,
/// so the export either skips them, flags them, or applies user-drawn redaction rectangles.
/// </summary>
public static class BurnedInAssessment
{
    private static readonly HashSet<string> TextProneModalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "ES", "XC", "OT", "SC", "DOC", "GM", "SM", "IVUS", "OP", "OPT",
    };

    public static (bool Suspect, string Reason) Assess(InstanceInfo i)
    {
        var declared = i.BurnedInAnnotation?.Trim().ToUpperInvariant();
        if (declared == "YES") return (true, "Burned In Annotation is YES.");
        if (declared == "NO") return (false, "Burned In Annotation is NO.");

        if (SopClassNames.IsSecondaryCapture(i.SopClassUid))
            return (true, "Secondary capture without Burned In Annotation; screenshots usually contain text.");
        if (i.SopClassUid is "1.2.840.10008.5.1.4.1.1.6.1" or "1.2.840.10008.5.1.4.1.1.3.1" or "1.2.840.10008.5.1.4.1.1.6.2")
            return (true, "Ultrasound image without Burned In Annotation; ultrasound images usually show patient details in the header band.");
        if (i.Modality is not null && TextProneModalities.Contains(i.Modality))
            return (true, $"{i.Modality} image without Burned In Annotation.");
        if (i.ImageType.Any(t => t.Equals("SECONDARY", StringComparison.OrdinalIgnoreCase) || t.Contains("SCREEN", StringComparison.OrdinalIgnoreCase)))
            return (true, "Derived or secondary image (possible screen capture) without Burned In Annotation.");
        return (false, "No burned-in annotation declared; modality is not text-prone.");
    }

    public static bool IsExportableObject(InstanceInfo i, out string reason)
    {
        if (!i.HasPixelData)
        {
            reason = $"{SopClassNames.Describe(i.SopClassUid)} is not an image; its free text and references are not de-identified by this tool.";
            return false;
        }
        reason = "";
        return true;
    }
}
