using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.Bench;

internal static partial class Benchmarks
{
    /// <summary>
    /// Decodes colour frames (YBR and RGB, compressed or not) and reports how many pixels are neutral grey.
    /// Ultrasound frames are mostly grey; an unconverted YBR frame would show a strong chroma offset everywhere.
    /// </summary>
    public static async Task ColorCheckAsync(string[] roots)
    {
        IsolatedAppData();
        foreach (var root in roots)
        {
            var lib = new DicomLibrary();
            await LibraryScanner.ScanAsync(lib, root, null);
            var groups = lib.Snapshot().SelectMany(s => s.Series).SelectMany(s => s.Instances)
                .Where(i => i.HasPixelData && i.SamplesPerPixel == 3)
                .GroupBy(i => (i.PhotometricInterpretation, i.TransferSyntaxUid));
            Console.WriteLine($"== colour {Anonymize(root)}");
            foreach (var g in groups)
            {
                var inst = g.First();
                var ds = FrameDecoder.LoadDataset(inst.FilePath);
                var raw = FrameDecoder.Decode(ds, 0);
                var rgb = new byte[raw.PixelCount * 3];
                FrameDecoder.ToRgb24(raw, rgb);
                long neutral = 0, total = 0, nonBlack = 0;
                for (var i = 0; i < raw.PixelCount; i += 13)
                {
                    int r = rgb[i * 3], gg = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                    total++;
                    if (Math.Max(r, Math.Max(gg, b)) > 20) nonBlack++;
                    if (Math.Max(r, Math.Max(gg, b)) - Math.Min(r, Math.Min(gg, b)) <= 12) neutral++;
                }
                Console.WriteLine($"   {g.Key.PhotometricInterpretation,-14} {Core.Dicom.DicomRuntime.DescribeTransferSyntax(g.Key.TransferSyntaxUid),-26} {g.Count(),4} objects  decoded as {raw.Photometric,-8} neutral {100.0 * neutral / total:0.0}%  non-black {100.0 * nonBlack / total:0.0}%");
            }
        }
    }
}
