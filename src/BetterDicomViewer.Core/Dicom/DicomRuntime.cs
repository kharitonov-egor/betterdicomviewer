using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;

namespace BetterDicomViewer.Core.Dicom;

/// <summary>One-time fo-dicom configuration with the fo-dicom.Codecs native transcoder (libijg, CharLS, OpenJPEG, OpenJPH).</summary>
public static class DicomRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    private static readonly HashSet<string> NativeCodecSyntaxes =
    [
        DicomUID.RLELossless.UID,
        DicomUID.JPEGBaseline8Bit.UID,
        DicomUID.JPEGExtended12Bit.UID,
        DicomUID.JPEGLossless.UID,
        DicomUID.JPEGLosslessSV1.UID,
        DicomUID.JPEGLSLossless.UID,
        DicomUID.JPEGLSNearLossless.UID,
        DicomUID.JPEG2000Lossless.UID,
        DicomUID.JPEG2000.UID,
        "1.2.840.10008.1.2.4.201",
        "1.2.840.10008.1.2.4.202",
        "1.2.840.10008.1.2.4.203",
    ];

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<NativeTranscoderManager>())
                .SkipValidation()
                .Build();
            _initialized = true;
        }
    }

    /// <summary>True when pixel data in this transfer syntax can be decoded by the bundled codecs.</summary>
    public static bool CanDecode(string transferSyntaxUid)
    {
        if (string.IsNullOrEmpty(transferSyntaxUid)) return true;
        var syntax = DicomTransferSyntax.Lookup(DicomUID.Parse(transferSyntaxUid));
        if (!syntax.IsEncapsulated) return true;
        return NativeCodecSyntaxes.Contains(transferSyntaxUid);
    }

    private static readonly Dictionary<string, string> ShortNames = new()
    {
        ["1.2.840.10008.1.2"] = "Implicit VR Little Endian",
        ["1.2.840.10008.1.2.1"] = "Explicit VR Little Endian",
        ["1.2.840.10008.1.2.1.99"] = "Deflated Explicit VR Little Endian",
        ["1.2.840.10008.1.2.2"] = "Explicit VR Big Endian",
        ["1.2.840.10008.1.2.5"] = "RLE Lossless",
        ["1.2.840.10008.1.2.4.50"] = "JPEG Baseline (lossy)",
        ["1.2.840.10008.1.2.4.51"] = "JPEG Extended (lossy)",
        ["1.2.840.10008.1.2.4.57"] = "JPEG Lossless",
        ["1.2.840.10008.1.2.4.70"] = "JPEG Lossless SV1",
        ["1.2.840.10008.1.2.4.80"] = "JPEG-LS Lossless",
        ["1.2.840.10008.1.2.4.81"] = "JPEG-LS Near-Lossless",
        ["1.2.840.10008.1.2.4.90"] = "JPEG 2000 Lossless",
        ["1.2.840.10008.1.2.4.91"] = "JPEG 2000",
        ["1.2.840.10008.1.2.4.201"] = "HTJ2K Lossless",
        ["1.2.840.10008.1.2.4.202"] = "HTJ2K Lossless RPCL",
        ["1.2.840.10008.1.2.4.203"] = "HTJ2K",
        ["1.2.840.10008.1.2.4.100"] = "MPEG2 video",
        ["1.2.840.10008.1.2.4.102"] = "MPEG-4 AVC/H.264 video",
        ["1.2.840.10008.1.2.4.107"] = "HEVC/H.265 video",
    };

    public static string DescribeTransferSyntax(string transferSyntaxUid)
    {
        if (string.IsNullOrEmpty(transferSyntaxUid)) return "Unknown transfer syntax";
        if (ShortNames.TryGetValue(transferSyntaxUid, out var shortName)) return shortName;
        try
        {
            var uid = DicomUID.Parse(transferSyntaxUid);
            return string.IsNullOrEmpty(uid.Name) || uid.Name == "Unknown" ? transferSyntaxUid : uid.Name;
        }
        catch (Exception)
        {
            return transferSyntaxUid;
        }
    }
}
