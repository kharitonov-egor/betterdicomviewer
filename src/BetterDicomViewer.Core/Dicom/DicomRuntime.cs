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

    public static string DescribeTransferSyntax(string transferSyntaxUid)
    {
        if (string.IsNullOrEmpty(transferSyntaxUid)) return "Unknown transfer syntax";
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
