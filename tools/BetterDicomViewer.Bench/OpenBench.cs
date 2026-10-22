using System.Diagnostics;
using BetterDicomViewer.Core.Export;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;
using BetterDicomViewer.Imaging.Native;

namespace BetterDicomViewer.Bench;

internal static partial class Benchmarks
{
    public static string? SnapshotDir { get; set; } = Environment.GetEnvironmentVariable("BDV_BENCH_SNAPSHOTS");

    public static async Task OpenSeriesAsync(string[] roots)
    {
        IsolatedAppData();
        Console.WriteLine($"native: {NativeImaging.Version}");
        foreach (var root in roots)
        {
            var lib = new DicomLibrary();
            await LibraryScanner.ScanAsync(lib, root, null);
            var series = lib.Snapshot().SelectMany(s => s.Series).Where(s => s.IsDisplayable).ToList();

            var candidates = new List<(string Label, DisplaySet Set)>();
            foreach (var s in series)
            {
                foreach (var d in SeriesAnalyzer.Analyze(s.Key, s.Instances))
                    candidates.Add(($"{s.Modality} '{s.Description}' / {d.Label}", d));
            }

            var ct = candidates.Where(c => c.Set.IsVolume && c.Set.Modality == "CT").OrderByDescending(c => c.Set.SliceCount).FirstOrDefault();
            var pt = candidates.Where(c => c.Set.IsVolume && c.Set.Modality == "PT").OrderByDescending(c => c.Set.SliceCount).FirstOrDefault();
            Console.WriteLine($"== open {Anonymize(root)}: {candidates.Count} display sets, {candidates.Count(c => c.Set.IsVolume)} volumes");
            foreach (var c in candidates.Where(c => c.Set.Warnings.Count > 0).Take(8))
                Console.WriteLine($"   warning [{c.Label}]: {string.Join(" | ", c.Set.Warnings)}");

            if (ct.Set is not null) await BenchVolume(ct.Label, ct.Set);
            if (pt.Set is not null) await BenchVolume(pt.Label, pt.Set);

            var cine = candidates.Where(c => c.Set.Kind == DisplaySetKind.Frames && c.Set.SliceCount > 20).OrderByDescending(c => c.Set.SliceCount).FirstOrDefault();
            if (cine.Set is not null) await BenchFrames(cine.Label, cine.Set);
            var big2d = candidates.Where(c => c.Set.Kind == DisplaySetKind.Frames && c.Set.Rows > 2000).FirstOrDefault();
            if (big2d.Set is not null) await BenchFrames(big2d.Label, big2d.Set);
        }
        Report.Line("peak working set", Report.Mb(Report.PeakWorkingSet()));
    }

    private static async Task BenchVolume(string label, DisplaySet set)
    {
        Console.WriteLine($"-- volume {label}: {set.Geometry!.Nx}x{set.Geometry.Ny}x{set.Geometry.Nz}, spacing {set.Geometry.Spacing}");
        var sw = Stopwatch.StartNew();
        var mid = set.Geometry.Nz / 2;
        var volume = VolumeLoader.Start(set, mid);
        while (!volume.IsSliceLoaded(mid) && !volume.Completion.IsCompleted) await Task.Delay(1);
        var firstSlice = sw.Elapsed;

        using var renderer = new SliceRenderer();
        var view = new SliceView { Width = 1024, Height = 1024 };
        var g = set.Geometry;
        view.SetPlane(g.RowDirection, g.ColumnDirection);
        view.Center = g.IndexToPatient.TransformPoint(new Vec3((g.Nx - 1) / 2.0, (g.Ny - 1) / 2.0, mid));
        view.FitTo(g.Corners(), g.Center);
        var display = new GrayDisplay();
        if (volume.ValueUnit == "HU") { display.Center = 40; display.Width = 400; }
        else
        {
            var (lo, hi) = volume.ValueRange;
            display.Center = (lo + hi) / 2;
            display.Width = Math.Max(hi - lo, 1);
        }
        var layer = new BaseLayer { Data = volume.Data, WorldToIndex = volume.WorldToIndex, Display = display, Background = volume.ValueUnit == "HU" ? -1024 : 0 };
        var pixels = new uint[view.Width * view.Height];
        unsafe
        {
            fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
        }
        var firstImage = sw.Elapsed;
        await volume.Completion;
        var full = sw.Elapsed;
        Report.Line("first slice decoded", Report.Ms(firstSlice));
        Report.Line("first image rendered (1024x1024)", Report.Ms(firstImage));
        Report.Line("full volume decoded", Report.Ms(full));
        Report.Line("volume memory", Report.Mb(volume.Data.ByteLength));
        if (volume.LoadError is not null) Console.WriteLine("   load error: " + volume.LoadError);
        if (volume.Suv is { } suv)
        {
            Console.WriteLine($"   SUV available: {suv.IsAvailable}; reference: {suv.ReferenceTimeSource}; decay {suv.DecayTimeS / 60:0.0} min");
            foreach (var p in suv.Problems) Console.WriteLine("   SUV problem: " + p);
            foreach (var n in suv.Notes) Console.WriteLine("   SUV note: " + n);
        }

        Save(pixels, view, $"{set.Modality}-axial");

        var times = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            view.Center = g.IndexToPatient.TransformPoint(new Vec3((g.Nx - 1) / 2.0, (g.Ny - 1) / 2.0, i % g.Nz));
            var t = Stopwatch.StartNew();
            unsafe
            {
                fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
            }
            times.Add(t.Elapsed.TotalMilliseconds);
        }
        Report.Line("axial scroll frame p50 / p95", $"{Percentile(times, 50):0.00} / {Percentile(times, 95):0.00} ms (reslice {renderer.LastResliceMs:0.00}, compose {renderer.LastComposeMs:0.00})");

        foreach (var (name, right, down) in new[]
                 {
                     ("coronal", Vec3.UnitX, -Vec3.UnitZ),
                     ("sagittal", Vec3.UnitY, -Vec3.UnitZ),
                     ("oblique", new Vec3(1, 0.5, 0).Normalized(), new Vec3(0, 0.3, -1).Normalized()),
                 })
        {
            view.SetPlane(right, down);
            view.Center = g.Center;
            view.FitTo(g.Corners(), g.Center);
            times.Clear();
            for (var i = 0; i < 100; i++)
            {
                view.Center = g.Center + view.Normal * ((i - 50) * 1.0);
                var t = Stopwatch.StartNew();
                unsafe
                {
                    fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
                }
                times.Add(t.Elapsed.TotalMilliseconds);
            }
            Report.Line($"{name} frame p50 / p95", $"{Percentile(times, 50):0.00} / {Percentile(times, 95):0.00} ms");
            view.Center = g.Center;
            unsafe
            {
                fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
            }
            Save(pixels, view, $"{set.Modality}-{name}");
        }

        view.SetPlane(Vec3.UnitX, -Vec3.UnitZ);
        view.Center = g.Center;
        view.FitTo(g.Corners(), g.Center);
        view.SlabMode = SlabMode.Max;
        view.SlabThicknessMm = 30;
        times.Clear();
        for (var i = 0; i < 30; i++)
        {
            var t = Stopwatch.StartNew();
            unsafe
            {
                fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
            }
            times.Add(t.Elapsed.TotalMilliseconds);
        }
        Report.Line("coronal 30 mm MIP slab p50", $"{Percentile(times, 50):0.00} ms");
        volume.Dispose();
    }

    private static async Task BenchFrames(string label, DisplaySet set)
    {
        Console.WriteLine($"-- frames {label}: {set.Columns}x{set.Rows}, {set.SliceCount} frames, color={set.IsColor}");
        using var source = new FrameSetSource(set);
        var sw = Stopwatch.StartNew();
        var first = await source.GetAsync(0);
        Report.Line("first frame decoded", Report.Ms(sw.Elapsed));
        if (first is null) return;
        using var renderer = new SliceRenderer();
        var view = new SliceView { Width = 1024, Height = 768 };
        var corners = new[] { Vec3.Zero, source.IndexToWorld.TransformPoint(new Vec3(first.Nx - 1, first.Ny - 1, 0)) };
        view.Center = (corners[0] + corners[1]) / 2;
        view.FitTo(corners, view.Center);
        var display = new GrayDisplay { Center = 128, Width = 256 };
        if (source.DicomWindows.Count > 0) { display.Center = source.DicomWindows[0].Center; display.Width = source.DicomWindows[0].Width; }
        var pixels = new uint[view.Width * view.Height];
        var times = new List<double>();
        for (var k = 0; k < Math.Min(set.SliceCount, 60); k++)
        {
            var t = Stopwatch.StartNew();
            var buffer = await source.GetAsync(k);
            if (buffer is null) continue;
            var layer = new BaseLayer { Data = buffer, WorldToIndex = source.IndexToWorld.Inverse(), Display = display };
            unsafe
            {
                fixed (uint* p = pixels) renderer.Render(view, layer, null, null, p);
            }
            times.Add(t.Elapsed.TotalMilliseconds);
        }
        Report.Line("decode+render per frame p50 / p95", $"{Percentile(times, 50):0.00} / {Percentile(times, 95):0.00} ms");
        Save(pixels, view, $"frames-{set.Modality}-{set.Columns}x{set.Rows}");
    }

    private static void Save(uint[] pixels, SliceView view, string name)
    {
        if (string.IsNullOrEmpty(SnapshotDir)) return;
        Directory.CreateDirectory(SnapshotDir);
        PngWriter.WriteBgra(Path.Combine(SnapshotDir, name + ".png"), pixels, view.Width, view.Height);
    }

    private static double Percentile(List<double> values, int p)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count == 0 ? 0 : s[Math.Clamp((int)Math.Round(p / 100.0 * (s.Count - 1)), 0, s.Count - 1)];
    }
}
