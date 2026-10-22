using System.Diagnostics;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Imaging;

namespace BetterDicomViewer.Bench;

internal static partial class Benchmarks
{
    /// <summary>Times offscreen GPU volume rendering of the largest CT volume, still and interactive quality.</summary>
    public static async Task VolumeRenderAsync(string[] roots)
    {
        IsolatedAppData();
        foreach (var root in roots)
        {
            var lib = new DicomLibrary();
            await LibraryScanner.ScanAsync(lib, root, null);
            var set = lib.Snapshot().SelectMany(s => s.Series).Where(s => s.IsDisplayable && s.Modality == "CT")
                .SelectMany(s => SeriesAnalyzer.Analyze(s.Key, s.Instances)).Where(d => d.IsVolume)
                .OrderByDescending(d => d.SliceCount).FirstOrDefault();
            if (set is null) continue;
            using var volume = VolumeLoader.Start(set, set.Geometry!.Nz / 2);
            await volume.Completion;
            Console.WriteLine($"== 3D {Anonymize(root)}: {volume.Nx}x{volume.Ny}x{volume.Nz}");
            using var vr = new VolumeRenderer();
            var sw = Stopwatch.StartNew();
            vr.SetVolume(volume.Data, volume.IndexToWorld);
            var preset = new TransferFunction(
                [(-500, 0.45, 0.2, 0.15), (60, 0.9, 0.6, 0.5), (300, 1, 0.92, 0.82)],
                [(-350, 0), (-100, 0.05), (60, 0.18), (250, 0.45), (800, 0.75)],
                [(0, 0.3), (40, 0.8), (150, 1)], BlendMode.Composite, true);
            var spacing = Math.Min(volume.Spacing.X, Math.Min(volume.Spacing.Y, volume.Spacing.Z));
            vr.SetTransfer(preset, spacing * 0.5);
            var g = set.Geometry;
            var pixels = new uint[900 * 900];
            foreach (var interactive in new[] { false, true })
            {
                var times = new List<double>();
                for (var i = 0; i < 12; i++)
                {
                    var dir = Vec3.Rotate(new Vec3(0, 1, 0), Vec3.UnitZ, i * 0.2);
                    vr.SetCamera(g.Center - dir * g.DiagonalMm * 1.9, g.Center, Vec3.UnitZ, 30, false, 100);
                    var t = Stopwatch.StartNew();
                    vr.Render(900, 900, interactive, pixels);
                    times.Add(t.Elapsed.TotalMilliseconds);
                }
                Report.Line(interactive ? "interactive frame (900x900) first / p50" : "still frame (900x900) first / p50",
                    $"{times[0]:0.0} / {times.Skip(1).OrderBy(x => x).ElementAt(5):0.0} ms");
            }
            var info = vr.RendererInfo.Split('\n').Where(l => l.Contains("renderer", StringComparison.OrdinalIgnoreCase) || l.Contains("version", StringComparison.OrdinalIgnoreCase)).Take(3);
            foreach (var l in info) Console.WriteLine("   " + l.Trim());
            if (!string.IsNullOrEmpty(SnapshotDir)) Core.Export.PngWriter.WriteBgra(Path.Combine(SnapshotDir, "vr.png"), pixels, 900, 900);
        }
    }
}
