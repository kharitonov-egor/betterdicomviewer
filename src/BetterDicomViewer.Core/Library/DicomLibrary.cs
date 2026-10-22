using BetterDicomViewer.Core.Dicom;

namespace BetterDicomViewer.Core.Library;

public sealed record SeriesKey(string StudyInstanceUid, string SeriesInstanceUid);

public sealed class LibrarySeries
{
    public required SeriesKey Key { get; init; }
    public required IReadOnlyList<InstanceInfo> Instances { get; init; }
    public string? Modality { get; init; }
    public int? SeriesNumber { get; init; }
    public string? Description { get; init; }
    public string? SeriesDate { get; init; }
    public string? SeriesTime { get; init; }
    public int FrameCount { get; init; }
    public bool IsProvisional { get; init; }
    public string? UnsupportedReason { get; init; }
    public string SopClassSummary { get; init; } = "";

    public bool IsDisplayable => UnsupportedReason is null;
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? $"Series {SeriesNumber?.ToString() ?? "?"}" : Description!;
}

public sealed class LibraryStudy
{
    public required string StudyInstanceUid { get; init; }
    public string? PatientName { get; init; }
    public string? PatientId { get; init; }
    public string? PatientBirthDate { get; init; }
    public string? PatientSex { get; init; }
    public string? StudyDate { get; init; }
    public string? StudyTime { get; init; }
    public string? Description { get; init; }
    public string? AccessionNumber { get; init; }
    public required IReadOnlyList<LibrarySeries> Series { get; init; }

    public IEnumerable<string> Modalities => Series.Select(s => s.Modality ?? "?").Distinct();
}

public sealed class LibraryChangedEventArgs(int added, int removed) : EventArgs
{
    public int Added { get; } = added;
    public int Removed { get; } = removed;
}

/// <summary>Thread-safe in-memory index of instances, grouped into studies and series on demand.</summary>
public sealed class DicomLibrary
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InstanceInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private int _version;

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public int Version => Volatile.Read(ref _version);

    public IReadOnlyCollection<string> Roots
    {
        get
        {
            lock (_gate) return _roots.ToArray();
        }
    }

    public int InstanceCount
    {
        get
        {
            lock (_gate) return _byPath.Count;
        }
    }

    public void AddRoot(string root)
    {
        lock (_gate) _roots.Add(Path.GetFullPath(root));
    }

    public void AddOrUpdate(IReadOnlyCollection<InstanceInfo> items)
    {
        if (items.Count == 0) return;
        lock (_gate)
        {
            foreach (var item in items)
            {
                if (item.IsProvisional && _byPath.TryGetValue(item.FilePath, out var existing) && !existing.IsProvisional)
                    continue;
                _byPath[item.FilePath] = item;
            }
            _version++;
        }
        Changed?.Invoke(this, new LibraryChangedEventArgs(items.Count, 0));
    }

    public void RemovePaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var removed = 0;
        lock (_gate)
        {
            foreach (var p in paths)
                if (_byPath.Remove(p)) removed++;
            _version++;
        }
        if (removed > 0) Changed?.Invoke(this, new LibraryChangedEventArgs(0, removed));
    }

    public void RemoveRoot(string root)
    {
        var full = Path.GetFullPath(root);
        List<string> paths;
        lock (_gate)
        {
            _roots.Remove(full);
            paths = _byPath.Keys.Where(p => AppPaths.IsSameOrInside(p, full)).ToList();
        }
        RemovePaths(paths);
    }

    public InstanceInfo? Find(string path)
    {
        lock (_gate) return _byPath.GetValueOrDefault(path);
    }

    public IReadOnlyList<InstanceInfo> InstancesOf(SeriesKey key)
    {
        lock (_gate)
        {
            return Deduplicate(_byPath.Values.Where(i => i.StudyInstanceUid == key.StudyInstanceUid && i.SeriesInstanceUid == key.SeriesInstanceUid));
        }
    }

    public List<LibraryStudy> Snapshot()
    {
        InstanceInfo[] all;
        lock (_gate) all = _byPath.Values.ToArray();

        var studies = new List<LibraryStudy>();
        foreach (var studyGroup in all.GroupBy(i => i.StudyInstanceUid))
        {
            var seriesList = new List<LibrarySeries>();
            foreach (var seriesGroup in studyGroup.GroupBy(i => i.SeriesInstanceUid))
            {
                var instances = Deduplicate(seriesGroup);
                var first = instances.FirstOrDefault(i => !i.IsProvisional) ?? instances[0];
                seriesList.Add(new LibrarySeries
                {
                    Key = new SeriesKey(studyGroup.Key, seriesGroup.Key),
                    Instances = instances,
                    Modality = first.Modality,
                    SeriesNumber = first.SeriesNumber,
                    Description = first.SeriesDescription,
                    SeriesDate = first.SeriesDate,
                    SeriesTime = first.SeriesTime,
                    FrameCount = instances.Sum(i => i.HasPixelData ? i.NumberOfFrames : 0),
                    IsProvisional = instances.Any(i => i.IsProvisional),
                    UnsupportedReason = SupportPolicy.UnsupportedReason(instances),
                    SopClassSummary = string.Join(", ", instances.Select(i => SopClassNames.Describe(i.SopClassUid)).Distinct()),
                });
            }

            var s0 = studyGroup.FirstOrDefault(i => !i.IsProvisional) ?? studyGroup.First();
            studies.Add(new LibraryStudy
            {
                StudyInstanceUid = studyGroup.Key,
                PatientName = s0.PatientName,
                PatientId = s0.PatientId,
                PatientBirthDate = s0.PatientBirthDate,
                PatientSex = s0.PatientSex,
                StudyDate = s0.StudyDate,
                StudyTime = s0.StudyTime,
                Description = s0.StudyDescription,
                AccessionNumber = s0.AccessionNumber,
                Series = seriesList
                    .OrderBy(s => s.IsDisplayable ? 0 : 1)
                    .ThenBy(s => s.SeriesNumber ?? int.MaxValue)
                    .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            });
        }

        return studies
            .OrderBy(s => s.PatientName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(s => s.StudyDate)
            .ThenByDescending(s => s.StudyTime)
            .ToList();
    }

    /// <summary>The same SOP instance may be reachable through two roots; keep one copy so stacks never contain duplicate slices.</summary>
    private static List<InstanceInfo> Deduplicate(IEnumerable<InstanceInfo> instances)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<InstanceInfo>();
        foreach (var i in instances.OrderBy(i => i.IsProvisional).ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var key = string.IsNullOrEmpty(i.SopInstanceUid) ? i.FilePath : i.SopInstanceUid;
            if (seen.Add(key)) result.Add(i);
        }
        return result;
    }
}
