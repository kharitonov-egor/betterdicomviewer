using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Synthetic;

namespace BetterDicomViewer.Tests;

public class LibraryTests
{
    private static Dictionary<string, (long, DateTime)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));

    [Fact]
    public async Task FindsExtensionlessFilesUsesDicomDirAndNeverWritesSource()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("disc");
        var studyUid = FellowOakDicom.DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var study = SyntheticDicom.WriteSeries(Path.Combine(root, "A1", "B1"), new SeriesOptions { Slices = 5, Extensionless = true, StudyInstanceUid = studyUid });
        var second = SyntheticDicom.WriteSeries(Path.Combine(root, "A1", "B2"), new SeriesOptions { Slices = 3, Extensionless = false, SeriesNumber = 2, StudyInstanceUid = studyUid });
        SyntheticDicom.WriteDicomDir(root, study.Concat(second));
        File.WriteAllText(Path.Combine(root, "README.TXT"), "not dicom");
        File.WriteAllBytes(Path.Combine(root, "A1", "NOTDICOM"), new byte[300]);
        var before = Snapshot(root);

        var lib = new DicomLibrary();
        var report = await LibraryScanner.ScanAsync(lib, root, null, new ScanOptions { UseCache = false }, TestContext.Current.CancellationToken);

        Assert.True(report.UsedDicomDir);
        Assert.Equal(8, report.DicomFiles);
        Assert.Equal(3, report.NonDicomFiles);
        Assert.Empty(report.Failures);
        var series = lib.Snapshot().Single().Series;
        Assert.Equal(2, series.Count);
        Assert.All(series, s => Assert.False(s.IsProvisional));
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public async Task WarmScanReusesIndexCacheAndDetectsChanges()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("folder");
        var files = SyntheticDicom.WriteSeries(root, new SeriesOptions { Slices = 6 });
        var ct = TestContext.Current.CancellationToken;
        IndexCache.Delete(root);

        await LibraryScanner.ScanAsync(new DicomLibrary(), root, null, null, ct);
        var lib = new DicomLibrary();
        var warm = await LibraryScanner.ScanAsync(lib, root, null, null, ct);
        Assert.Equal(6, warm.ReusedFromCache);
        Assert.Equal(0, warm.Parsed);

        File.Delete(files[0]);
        var lib2 = new DicomLibrary();
        var changed = await LibraryScanner.ScanAsync(lib2, root, null, null, ct);
        Assert.Equal(5, lib2.Snapshot().Single().Series.Single().Instances.Count);
        Assert.Equal(5, changed.ReusedFromCache);
        IndexCache.Delete(root);
    }

    [Fact]
    public async Task NonImageObjectsAreListedWithReason()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("pr");
        var ds = new FellowOakDicom.DicomDataset
        {
            { FellowOakDicom.DicomTag.SOPClassUID, FellowOakDicom.DicomUID.GrayscaleSoftcopyPresentationStateStorage },
            { FellowOakDicom.DicomTag.SOPInstanceUID, FellowOakDicom.DicomUIDGenerator.GenerateDerivedFromUUID() },
            { FellowOakDicom.DicomTag.StudyInstanceUID, "1.2.3.4" },
            { FellowOakDicom.DicomTag.SeriesInstanceUID, "1.2.3.4.5" },
            { FellowOakDicom.DicomTag.Modality, "PR" },
        };
        await new FellowOakDicom.DicomFile(ds).SaveAsync(Path.Combine(root, "PS0001"));
        var lib = new DicomLibrary();
        await LibraryScanner.ScanAsync(lib, root, null, new ScanOptions { UseCache = false }, TestContext.Current.CancellationToken);
        var s = lib.Snapshot().Single().Series.Single();
        Assert.False(s.IsDisplayable);
        Assert.Contains("Presentation State", s.UnsupportedReason);
    }

    [Fact]
    public async Task ScanCanBeCancelled()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("many");
        SyntheticDicom.WriteSeries(root, new SeriesOptions { Slices = 40, Columns = 16, Rows = 16 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await LibraryScanner.ScanAsync(new DicomLibrary(), root, null, new ScanOptions { UseCache = false }, cts.Token)
            .ContinueWith(t => t.IsCanceled ? null : t.Result, TestContext.Current.CancellationToken);
        Assert.True(report is null || report.Cancelled);
    }
}
