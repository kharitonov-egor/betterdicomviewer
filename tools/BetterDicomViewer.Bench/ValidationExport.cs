using System.Text.Json;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.Bench;

internal static partial class Benchmarks
{
    /// <summary>
    /// Writes, for each volume, the source files, this app's index-to-patient matrix and values at random voxels, so an
    /// independent reader (tools/validation/compare_simpleitk.py) can check geometry, ordering, rescale and decoding.
    /// </summary>
    public static async Task ExportValidationAsync(string[] roots, string outDir)
    {
        IsolatedAppData();
        Directory.CreateDirectory(outDir);
        var rng = new Random(1234);
        var n = 0;
        foreach (var root in roots)
        {
            var lib = new DicomLibrary();
            await LibraryScanner.ScanAsync(lib, root, null);
            foreach (var series in lib.Snapshot().SelectMany(s => s.Series).Where(s => s.IsDisplayable && s.Modality is "CT" or "PT"))
            {
                foreach (var set in SeriesAnalyzer.Analyze(series.Key, series.Instances).Where(d => d.IsVolume && !d.IsColor && !d.Geometry!.IsResampled))
                {
                    using var volume = VolumeLoader.Start(set, 0);
                    await volume.Completion;
                    var samples = new List<double[]>();
                    for (var s = 0; s < 400; s++)
                    {
                        int i = rng.Next(volume.Nx), j = rng.Next(volume.Ny), k = rng.Next(volume.Nz);
                        var p = volume.IndexToWorld.TransformPoint(new Vec3(i, j, k));
                        samples.Add([p.X, p.Y, p.Z, volume.Data.GetValue(i, j, k)]);
                    }
                    var m = volume.IndexToWorld.ToRowMajor4x4();
                    var doc = new
                    {
                        modality = series.Modality,
                        voxelType = volume.Data.Type.ToString(),
                        dims = new[] { volume.Nx, volume.Ny, volume.Nz },
                        indexToPatient = m,
                        files = set.Frames.Select(f => f.Instance.FilePath).ToArray(),
                        samples,
                    };
                    await File.WriteAllTextAsync(Path.Combine(outDir, $"volume-{n++:D3}.json"), JsonSerializer.Serialize(doc));
                }
            }
        }
        Console.WriteLine($"wrote {n} volume descriptions to {outDir}");
    }
}
