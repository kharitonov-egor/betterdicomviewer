using System.Diagnostics;
using BetterDicomViewer.Core;
using BetterDicomViewer.Core.Library;

namespace BetterDicomViewer.Bench;

/// <summary>Benchmarks print counts and timings only; no patient identifiers are written to the console.</summary>
internal static partial class Benchmarks
{
    private static string IsolatedAppData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bdv-bench-appdata");
        Directory.CreateDirectory(dir);
        AppPaths.Root = dir;
        return dir;
    }

    public static async Task ScanAsync(string[] roots)
    {
        IsolatedAppData();
        foreach (var root in roots)
        {
            Console.WriteLine($"== scan {Anonymize(root)}");
            IndexCache.Delete(LibraryScanner.ResolveRoot(root).Root);

            var lib = new DicomLibrary();
            var sw = Stopwatch.StartNew();
            var report = await LibraryScanner.ScanAsync(lib, root, null);
            sw.Stop();
            var snapshot = lib.Snapshot();
            Report.Line("cold (no index cache) total", Report.Ms(sw.Elapsed));
            Report.Line("cold first results (DICOMDIR or headers)", Report.Ms(report.FirstResultsAfter));
            Report.Line("files seen / DICOM / non-DICOM", $"{report.FilesSeen} / {report.DicomFiles} / {report.NonDicomFiles}");
            Report.Line("studies / series", $"{snapshot.Count} / {snapshot.Sum(s => s.Series.Count)}");
            Report.Line("displayable / listed-only series", $"{snapshot.Sum(s => s.Series.Count(x => x.IsDisplayable))} / {snapshot.Sum(s => s.Series.Count(x => !x.IsDisplayable))}");
            Report.Line("failures", report.Failures.Count.ToString());
            foreach (var f in report.Failures.Take(5)) Console.WriteLine("   " + Anonymize(f));

            var lib2 = new DicomLibrary();
            sw.Restart();
            var warm = await LibraryScanner.ScanAsync(lib2, root, null);
            sw.Stop();
            Report.Line("warm (index cache) total", Report.Ms(sw.Elapsed));
            Report.Line("warm first results", Report.Ms(warm.FirstResultsAfter));
            Report.Line("warm reused from cache / re-parsed", $"{warm.ReusedFromCache} / {warm.Parsed}");
            Report.Line("peak working set", Report.Mb(Report.PeakWorkingSet()));
        }
    }

    private static string Anonymize(string text)
    {
        foreach (var root in LocalSettings.DataRoots())
            text = text.Replace(root, "<data-root>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
