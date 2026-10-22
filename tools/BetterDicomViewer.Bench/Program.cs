using System.Diagnostics;
using System.Text.Json;
using BetterDicomViewer.Bench;

var command = args.Length > 0 ? args[0] : "help";
var roots = args.Length > 1 ? args[1..] : LocalSettings.DataRoots();

switch (command)
{
    case "scan":
        await Benchmarks.ScanAsync(roots);
        break;
    case "open":
        await Benchmarks.OpenSeriesAsync(roots);
        break;
    case "all":
        await Benchmarks.ScanAsync(roots);
        await Benchmarks.OpenSeriesAsync(roots);
        break;
    default:
        Console.WriteLine("usage: BetterDicomViewer.Bench <scan|open|all> [root ...]");
        Console.WriteLine("Roots default to dataRoots in local.settings.json (gitignored).");
        break;
}

namespace BetterDicomViewer.Bench
{
    internal static class LocalSettings
    {
        public static string[] DataRoots()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "local.settings.json"))) dir = dir.Parent;
            if (dir is null) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "local.settings.json")));
            return doc.RootElement.TryGetProperty("dataRoots", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : [];
        }
    }

    internal static class Report
    {
        public static void Line(string label, string value) => Console.WriteLine($"{label,-44} {value}");

        public static string Ms(TimeSpan t) => $"{t.TotalMilliseconds,9:0.0} ms";

        public static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0,9:0.0} MB";

        public static long PeakWorkingSet()
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.PeakWorkingSet64;
        }
    }
}
