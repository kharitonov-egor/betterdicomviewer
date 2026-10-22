using System.Diagnostics;
using BetterDicomViewer.Core.Dicom;
using FellowOakDicom;
using FellowOakDicom.Imaging;

namespace BetterDicomViewer.UiTests;

/// <summary>
/// Launches the real WPF application with --script against an isolated app-data folder, drives it through the same
/// pointer and command paths as user input, and checks the files it produces.
/// </summary>
public class AppSmokeTests
{
    private static string FindApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterDicomViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var candidates = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(dir!.FullName, "src", "BetterDicomViewer.App", "bin", c, "net10.0-windows", "BetterDicomViewer.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        Assert.NotEmpty(candidates);
        return candidates[0];
    }

    private static async Task<(string Log, int ExitCode)> RunScript(string appData, string script)
    {
        var log = Path.Combine(appData, "script.log");
        var psi = new ProcessStartInfo(FindApp())
        {
            UseShellExecute = false,
            ArgumentList = { "--log", log, "--script", script },
        };
        psi.Environment["BDV_APPDATA"] = appData;
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await p.WaitForExitAsync(cts.Token);
        return (File.Exists(log) ? await File.ReadAllTextAsync(log) : "", p.ExitCode);
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task EndToEndWorkflowOnSyntheticStudy()
    {
        var root = Path.Combine(Path.GetTempPath(), "bdv-ui-tests", Guid.NewGuid().ToString("N")[..8]);
        var appData = Path.Combine(root, "appdata");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(outDir);
        var deid = Path.Combine(outDir, "deid");
        var script = string.Join("; ",
            "size 1400 900",
            "demo",
            "series CT 3 mm (synthetic",
            "waitload",
            "tool Length",
            "drag 0 left 0.30 0.30 0.60 0.30",
            $"export-measurements {Path.Combine(outDir, "after-draw.csv")}",
            "key Ctrl+Z",
            $"export-measurements {Path.Combine(outDir, "after-undo.csv")}",
            "key Ctrl+Y",
            $"export-measurements {Path.Combine(outDir, "after-redo.csv")}",
            "key Ctrl+5",
            "waitload",
            "wait 2000",
            $"screenshot {Path.Combine(outDir, "mpr3d.png")}",
            "key Ctrl+6",
            "waitload",
            "wait 1500",
            $"export-screenshot {Path.Combine(outDir, "petct.png")}",
            "key Ctrl+1",
            "series US cine",
            "wait 800",
            "tool Redact",
            "drag 0 left 0.02 0.10 0.99 0.20",
            $"deid-export {deid} redact",
            "exit");

        var (log, exitCode) = await RunScript(appData, script);
        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(log), "Script errors:\n" + log);

        static int Rows(string path) => File.ReadAllLines(path).Length - 1;
        Assert.Equal(1, Rows(Path.Combine(outDir, "after-draw.csv")));
        Assert.Equal(0, Rows(Path.Combine(outDir, "after-undo.csv")));
        Assert.Equal(1, Rows(Path.Combine(outDir, "after-redo.csv")));
        Assert.Contains(",mm,", File.ReadAllLines(Path.Combine(outDir, "after-redo.csv"))[1]);

        Assert.True(new FileInfo(Path.Combine(outDir, "mpr3d.png")).Length > 100_000);
        Assert.True(new FileInfo(Path.Combine(outDir, "petct.png")).Length > 100_000);

        Assert.True(File.Exists(Path.Combine(deid, "DICOMDIR")));
        var exported = Directory.EnumerateFiles(Path.Combine(deid, "DICOM"), "*", SearchOption.AllDirectories).Single();
        DicomRuntime.EnsureInitialized();
        var ds = DicomFile.Open(exported).Dataset;
        Assert.Equal("ANONYMOUS", ds.GetString(DicomTag.PatientName));
        Assert.Equal("YES", ds.GetString(DicomTag.PatientIdentityRemoved));
        var frame = DicomPixelData.Create(ds).GetFrame(0).Data;
        var width = ds.GetSingleValue<ushort>(DicomTag.Columns);
        // Rows 8-22 held the synthetic burned-in header; every pixel there must now be black.
        for (var y = 8; y <= 22; y++)
        for (var x = 0; x < width; x++)
            Assert.Equal(0, frame[(y * width + x) * 3]);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
