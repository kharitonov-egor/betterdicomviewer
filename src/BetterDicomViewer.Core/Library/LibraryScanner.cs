using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Library;

public sealed record ScanProgress(string Phase, int Processed, int Total, int DicomFiles);

public sealed class ScanReport
{
    public required string Root { get; init; }
    public int FilesSeen { get; set; }
    public int DicomFiles { get; set; }
    public int NonDicomFiles { get; set; }
    public int ReusedFromCache { get; set; }
    public int Parsed { get; set; }
    public bool UsedDicomDir { get; set; }
    public List<string> Failures { get; } = [];
    public TimeSpan FirstResultsAfter { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class ScanOptions
{
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public bool UseCache { get; init; } = true;
}

/// <summary>Recursive, cancellable discovery of DICOM files (including extensionless ones) with a bounded worker pool.</summary>
public static class LibraryScanner
{
    private static readonly HashSet<string> NeverDicomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdf", ".htm", ".html", ".xml", ".xsl", ".txt", ".inf", ".ico", ".jpg", ".jpeg", ".gif", ".bmp",
        ".png", ".chm", ".config", ".lic", ".db", ".scc", ".ini", ".css", ".js", ".zip", ".cab", ".msi", ".json", ".log",
        ".manifest", ".sys", ".bat", ".cmd", ".ps1", ".lnk", ".url", ".mp4", ".avi", ".wav", ".mp3",
    };

    public static (string Root, string? DicomDir) ResolveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            return (Path.GetDirectoryName(full)!, DicomDirReader.IsDicomDirFile(full) ? full : null);
        var dicomDir = Path.Combine(full, "DICOMDIR");
        return (full, File.Exists(dicomDir) ? dicomDir : null);
    }

    public static async Task<ScanReport> ScanAsync(
        DicomLibrary library,
        string path,
        IProgress<ScanProgress>? progress,
        ScanOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        var sw = Stopwatch.StartNew();
        var (root, dicomDir) = ResolveRoot(path);
        var report = new ScanReport { Root = root };
        library.AddRoot(root);
        var firstResultsReported = false;

        void MarkFirstResults()
        {
            if (firstResultsReported) return;
            firstResultsReported = true;
            report.FirstResultsAfter = sw.Elapsed;
        }

        var cache = options.UseCache ? IndexCache.Load(root) : null;
        var cached = cache?.Instances.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                     ?? new Dictionary<string, InstanceInfo>(StringComparer.OrdinalIgnoreCase);
        var knownNonDicom = new HashSet<string>(cache?.NonDicomFiles ?? [], StringComparer.OrdinalIgnoreCase);

        if (cached.Count > 0)
        {
            library.AddOrUpdate(cached.Values.ToList());
            MarkFirstResults();
        }
        else if (dicomDir is not null)
        {
            try
            {
                progress?.Report(new ScanProgress("Reading DICOMDIR", 0, 0, 0));
                var provisional = await Task.Run(() => DicomDirReader.Read(dicomDir), ct).ConfigureAwait(false);
                library.AddOrUpdate(provisional);
                report.UsedDicomDir = true;
                MarkFirstResults();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Failures.Add($"DICOMDIR could not be read: {ex.Message}");
            }
        }

        progress?.Report(new ScanProgress("Listing files", 0, 0, 0));
        var files = await Task.Run(() => EnumerateFiles(root, ct), ct).ConfigureAwait(false);
        report.FilesSeen = files.Count;

        var toParse = new List<(string Path, long Size, DateTime Mtime)>();
        var reused = new List<InstanceInfo>();
        var nonDicom = new ConcurrentBag<string>();
        foreach (var f in files)
        {
            if (DicomDirReader.IsDicomDirFile(f.Path))
            {
                nonDicom.Add(f.Path);
                continue;
            }
            if (cached.TryGetValue(f.Path, out var hit) && hit.FileSize == f.Size && hit.LastWriteUtc == f.Mtime && !hit.IsProvisional)
            {
                reused.Add(hit);
                continue;
            }
            if (knownNonDicom.Contains(f.Path) || NeverDicomExtensions.Contains(Path.GetExtension(f.Path)))
            {
                nonDicom.Add(f.Path);
                continue;
            }
            toParse.Add(f);
        }
        report.ReusedFromCache = reused.Count;

        var present = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var vanished = cached.Keys.Where(p => !present.Contains(p)).ToList();
        library.RemovePaths(vanished);

        var results = new ConcurrentQueue<InstanceInfo>();
        var failures = new ConcurrentBag<string>();
        var processed = 0;
        var dicomCount = reused.Count;
        var pending = new List<InstanceInfo>();
        var lastFlush = Stopwatch.StartNew();

        void Flush(bool force)
        {
            if (!force && lastFlush.ElapsedMilliseconds < 250 && results.Count < 512) return;
            pending.Clear();
            while (results.TryDequeue(out var r)) pending.Add(r);
            if (pending.Count > 0)
            {
                library.AddOrUpdate(pending.ToList());
                MarkFirstResults();
            }
            lastFlush.Restart();
        }

        try
        {
            var parseTask = Parallel.ForEachAsync(toParse, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct }, (f, token) =>
            {
                token.ThrowIfCancellationRequested();
                var probe = HeaderReader.Probe(f.Path);
                if (probe == ProbeResult.NotDicom)
                {
                    nonDicom.Add(f.Path);
                }
                else
                {
                    try
                    {
                        var info = HeaderReader.Read(f.Path);
                        if (info.SopClassUid == HeaderReader.DicomDirSopClassUid)
                        {
                            nonDicom.Add(f.Path);
                        }
                        else
                        {
                            results.Enqueue(info);
                            Interlocked.Increment(ref dicomCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (probe == ProbeResult.Part10) failures.Add($"{f.Path}: {ex.Message}");
                        else nonDicom.Add(f.Path);
                    }
                }
                Interlocked.Increment(ref processed);
                return ValueTask.CompletedTask;
            });

            while (!parseTask.IsCompleted)
            {
                await Task.WhenAny(parseTask, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
                Flush(false);
                progress?.Report(new ScanProgress("Reading headers", Volatile.Read(ref processed), toParse.Count, Volatile.Read(ref dicomCount)));
            }
            await parseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }

        Flush(true);
        if (reused.Count > 0 && cached.Count == 0) library.AddOrUpdate(reused);

        report.Parsed = toParse.Count;
        report.DicomFiles = dicomCount;
        report.NonDicomFiles = nonDicom.Count;
        report.Failures.AddRange(failures);
        report.Elapsed = sw.Elapsed;

        if (!report.Cancelled)
        {
            // Drop DICOMDIR placeholders that never resolved to a readable file.
            var provisionalLeft = library.Snapshot()
                .SelectMany(s => s.Series)
                .SelectMany(s => s.Instances)
                .Where(i => i.IsProvisional && AppPaths.IsSameOrInside(i.FilePath, root))
                .Select(i => i.FilePath)
                .ToList();
            library.RemovePaths(provisionalLeft);

            var all = library.Snapshot()
                .SelectMany(s => s.Series)
                .SelectMany(s => s.Instances)
                .Where(i => !i.IsProvisional && AppPaths.IsSameOrInside(i.FilePath, root))
                .ToList();
            try
            {
                IndexCache.Save(root, new IndexCacheFile
                {
                    Root = root,
                    ScannedUtc = DateTime.UtcNow,
                    Instances = all,
                    NonDicomFiles = nonDicom.ToList(),
                });
            }
            catch (Exception ex)
            {
                report.Failures.Add($"Index cache could not be saved: {ex.Message}");
            }
        }

        progress?.Report(new ScanProgress("Done", report.Parsed, report.Parsed, report.DicomFiles));
        return report;
    }

    private static List<(string Path, long Size, DateTime Mtime)> EnumerateFiles(string root, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Device | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var enumerable = new FileSystemEnumerable<(string, long, DateTime)>(
            root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
            opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        };
        var list = new List<(string, long, DateTime)>();
        foreach (var item in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(item);
        }
        return list;
    }

    /// <summary>Parses headers for specific files immediately (used when the user opens a series before the background scan reaches it).</summary>
    public static async Task<List<InstanceInfo>> ParseNowAsync(DicomLibrary library, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var results = new ConcurrentBag<InstanceInfo>();
        await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, (p, _) =>
        {
            var info = HeaderReader.TryRead(p);
            if (info is not null) results.Add(info);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        var list = results.ToList();
        library.AddOrUpdate(list);
        return list;
    }
}
