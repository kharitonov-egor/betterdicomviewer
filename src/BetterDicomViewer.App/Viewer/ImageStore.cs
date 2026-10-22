using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Segmentation;

namespace BetterDicomViewer.App.Viewer;

/// <summary>
/// Owns decoded volumes, lazy frame sources and label maps. Volumes are leased while shown; unleased volumes are
/// evicted least-recently-used first when the memory budget is exceeded.
/// </summary>
public sealed class ImageStore
{
    private readonly Dictionary<string, ImageVolume> _volumes = [];
    private readonly Dictionary<string, FrameSetSource> _frames = [];
    private readonly Dictionary<string, LabelMap> _labels = [];
    private readonly Dictionary<string, long> _lastUse = [];
    private long _clock;

    public ImageStore()
    {
        var total = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        BudgetBytes = Math.Clamp(total * 45 / 100, 1L << 30, 12L << 30);
    }

    public long BudgetBytes { get; set; }

    public long VolumeBytes => _volumes.Values.Sum(v => v.Data.ByteLength) + _frames.Values.Sum(f => f.CachedBytes);

    public event Action? Changed;

    public ImageVolume AcquireVolume(DisplaySet set, int prioritySlice)
    {
        if (!_volumes.TryGetValue(set.Id, out var v))
        {
            EnsureBudget(set.EstimatedBytes(VoxelType.Float32));
            v = VolumeLoader.Start(set, prioritySlice);
            _volumes[set.Id] = v;
            v.Completion.ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
        }
        v.Acquire();
        _lastUse[set.Id] = ++_clock;
        Changed?.Invoke();
        return v;
    }

    public FrameSetSource AcquireFrames(DisplaySet set)
    {
        if (!_frames.TryGetValue(set.Id, out var f))
        {
            EnsureBudget(0);
            f = new FrameSetSource(set);
            _frames[set.Id] = f;
            FrameSourceCreated?.Invoke(f);
        }
        f.Acquire();
        _lastUse[set.Id] = ++_clock;
        return f;
    }

    public event Action<FrameSetSource>? FrameSourceCreated;

    public void Release(FrameSetSource frames)
    {
        frames.Release();
        _lastUse[frames.Set.Id] = ++_clock;
    }

    public void Release(ImageVolume volume)
    {
        volume.Release();
        _lastUse[volume.Source.Id] = ++_clock;
    }

    public IEnumerable<ImageVolume> LoadedVolumes => _volumes.Values;

    public ImageVolume? FindVolume(string displaySetId) => _volumes.GetValueOrDefault(displaySetId);

    public LabelMap GetOrCreateLabels(ImageVolume volume)
    {
        if (_labels.TryGetValue(volume.Source.Id, out var map)) return map;
        map = new LabelMap(volume.Nx, volume.Ny, volume.Nz, volume.IndexToWorld, volume.Source.Id, volume.Source.Series.SeriesInstanceUid);
        var path = SegmentationPaths.For(volume.Source);
        if (!LabelMapIo.TryLoadNrrd(path, map) || map.Segments.Count == 0)
        {
            map.Segments.Clear();
            map.AddSegment();
            map.IsDirty = false;
        }
        _labels[volume.Source.Id] = map;
        return map;
    }

    public LabelMap? FindLabels(string displaySetId) => _labels.GetValueOrDefault(displaySetId);

    public IEnumerable<LabelMap> LabelMaps => _labels.Values;

    private void EnsureBudget(long incoming)
    {
        while (VolumeBytes + incoming > BudgetBytes)
        {
            var victim = _volumes.Values
                .Where(v => v.Leases <= 0 && !_labels.ContainsKey(v.Source.Id))
                .OrderBy(v => _lastUse.GetValueOrDefault(v.Source.Id))
                .FirstOrDefault();
            if (victim is null) break;
            _volumes.Remove(victim.Source.Id);
            victim.Dispose();
        }
        // Unleased 2D frame sets beyond the eight most recent are dropped; each holds up to 512 MB of decoded frames.
        foreach (var f in _frames.Where(kv => kv.Value.Leases <= 0).OrderByDescending(kv => _lastUse.GetValueOrDefault(kv.Key)).Skip(8).ToList())
        {
            _frames.Remove(f.Key);
            f.Value.Dispose();
        }
    }

    public void ClearAll()
    {
        foreach (var v in _volumes.Values) v.Dispose();
        foreach (var f in _frames.Values) f.Dispose();
        foreach (var l in _labels.Values) l.Dispose();
        _volumes.Clear();
        _frames.Clear();
        _labels.Clear();
        Changed?.Invoke();
    }
}

public static class SegmentationPaths
{
    public static string For(DisplaySet set)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(set.Id)))[..24];
        return Path.Combine(Core.AppPaths.SegmentationsDir, key + ".seg.nrrd");
    }
}
