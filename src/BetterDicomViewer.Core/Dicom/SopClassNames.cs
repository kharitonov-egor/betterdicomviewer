using System.Collections.Concurrent;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Dicom;

public static class SopClassNames
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Describe(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return "Unknown object";
        return Cache.GetOrAdd(uid, static u =>
        {
            try
            {
                var name = DicomUID.Parse(u).Name;
                if (string.IsNullOrWhiteSpace(name) || name == "Unknown") return u;
                return name.EndsWith(" Storage", StringComparison.Ordinal) ? name[..^" Storage".Length] : name;
            }
            catch (Exception)
            {
                return u;
            }
        });
    }

    public static bool IsSecondaryCapture(string? uid) =>
        uid is not null && uid.StartsWith("1.2.840.10008.5.1.4.1.1.7", StringComparison.Ordinal);

    public static bool IsPresentationState(string? uid) =>
        uid is not null && uid.StartsWith("1.2.840.10008.5.1.4.1.1.11.", StringComparison.Ordinal);
}

public static class SupportPolicy
{
    private static readonly HashSet<int> SupportedBitsAllocated = [1, 8, 16, 32];

    public static string? UnsupportedReason(IReadOnlyList<InstanceInfo> instances)
    {
        if (instances.Count == 0) return "Empty series";
        var images = instances.Where(i => i.HasPixelData).ToList();
        if (images.Count == 0)
        {
            var kinds = string.Join(", ", instances.Select(i => SopClassNames.Describe(i.SopClassUid)).Distinct());
            return $"Not an image object ({kinds}). Listed for completeness; not displayed.";
        }

        var undecodable = images
            .Where(i => !i.IsProvisional && !DicomRuntime.CanDecode(i.TransferSyntaxUid))
            .Select(i => DicomRuntime.DescribeTransferSyntax(i.TransferSyntaxUid))
            .Distinct()
            .ToList();
        if (undecodable.Count > 0 && undecodable.Count == images.Count(i => !i.IsProvisional))
            return $"Unsupported transfer syntax: {string.Join(", ", undecodable)}";

        var parsed = images.Where(i => !i.IsProvisional).ToList();
        if (parsed.Count > 0 && parsed.All(i => !SupportedBitsAllocated.Contains(i.BitsAllocated)))
            return $"Unsupported pixel format: {parsed[0].BitsAllocated} bits allocated";
        if (parsed.Count > 0 && parsed.All(i => i.SamplesPerPixel is not (1 or 3)))
            return $"Unsupported pixel format: {parsed[0].SamplesPerPixel} samples per pixel";
        return null;
    }

    public static string? UnsupportedReason(InstanceInfo instance)
    {
        if (!instance.HasPixelData) return $"Not an image object ({SopClassNames.Describe(instance.SopClassUid)})";
        if (!DicomRuntime.CanDecode(instance.TransferSyntaxUid))
            return $"Unsupported transfer syntax: {DicomRuntime.DescribeTransferSyntax(instance.TransferSyntaxUid)}";
        if (!SupportedBitsAllocated.Contains(instance.BitsAllocated)) return $"Unsupported bits allocated: {instance.BitsAllocated}";
        if (instance.SamplesPerPixel is not (1 or 3)) return $"Unsupported samples per pixel: {instance.SamplesPerPixel}";
        return null;
    }
}
