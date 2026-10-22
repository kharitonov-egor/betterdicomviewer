using BetterDicomViewer.Core.Geometry;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Imaging;

/// <summary>
/// Lazily decodes frames of a 2D display set (radiographs, cine loops, secondary captures) into small per-frame
/// buffers held in a bounded LRU cache. Frames in use by a viewer are pinned and never evicted.
/// </summary>
public sealed class FrameSetSource : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, VoxelBuffer> _frames = [];
    private readonly Dictionary<int, List<VoxelBuffer>> _levels = [];
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, int> _pins = [];
    private readonly Dictionary<int, Task<VoxelBuffer?>> _pending = [];
    private readonly Dictionary<string, (DicomDataset Dataset, Lock Gate)> _datasets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeSlots = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly CancellationTokenSource _cts = new();
    private long _bytes;
    private bool _disposed;

    public FrameSetSource(DisplaySet set, long budgetBytes = 512L * 1024 * 1024)
    {
        Set = set;
        BudgetBytes = budgetBytes;
        Type = VolumeLoader.ChooseVoxelType(set.Frames, out _);
        Components = set.IsColor ? 3 : 1;
        var rep = set.Frames[0].Instance;
        Calibration = FrameCalibration.For(rep, set.Frames[0].FrameIndex);
        var aspect = Calibration.HasUniformSpacing ? Calibration.RowSpacingMm!.Value / Calibration.ColumnSpacingMm!.Value : 1.0;
        IndexToWorld = new Affine(1, 0, 0, 0, aspect, 0, 0, 0, 1, 0, 0, 0);
        ValueUnit = VolumeLoader.ValueUnitFor(rep);
        IsMonochrome1 = string.Equals(rep.PhotometricInterpretation, "MONOCHROME1", StringComparison.OrdinalIgnoreCase);
        DicomWindows = VolumeLoader.DicomWindowPresets(rep);
        FrameIntervalMs = rep.FrameTimeMs is > 0 ? rep.FrameTimeMs.Value
            : rep.RecommendedDisplayFrameRate is > 0 ? 1000.0 / rep.RecommendedDisplayFrameRate.Value
            : rep.CineRate is > 0 ? 1000.0 / rep.CineRate.Value
            : 1000.0 / 25;
    }

    public DisplaySet Set { get; }
    public long BudgetBytes { get; }
    public VoxelType Type { get; }
    public int Components { get; }
    public FrameCalibration Calibration { get; }
    public Affine IndexToWorld { get; }
    public string ValueUnit { get; }
    public bool IsMonochrome1 { get; }
    public IReadOnlyList<WindowPreset> DicomWindows { get; }
    public double FrameIntervalMs { get; }
    public int Count => Set.Frames.Count;

    public event Action<int>? FrameReady;
    public event Action<int, string>? FrameFailed;

    public VoxelBuffer? TryGet(int k)
    {
        lock (_gate)
        {
            if (_frames.TryGetValue(k, out var buffer))
            {
                Touch(k);
                return buffer;
            }
        }
        _ = GetAsync(k);
        return null;
    }

    public Task<VoxelBuffer?> GetAsync(int k)
    {
        if ((uint)k >= (uint)Count) return Task.FromResult<VoxelBuffer?>(null);
        lock (_gate)
        {
            if (_frames.TryGetValue(k, out var buffer)) return Task.FromResult<VoxelBuffer?>(buffer);
            if (_pending.TryGetValue(k, out var task)) return task;
            var t = Task.Run(() => DecodeAsync(k));
            _pending[k] = t;
            return t;
        }
    }

    /// <summary>
    /// The pyramid level best suited to <paramref name="sourcePixelsPerScreenPixel"/>, with its index-to-world transform.
    /// Falls back to full resolution. The level shares the frame's pin.
    /// </summary>
    public (VoxelBuffer Buffer, Affine IndexToWorld)? TryGetForScale(int k, double sourcePixelsPerScreenPixel)
    {
        var full = TryGet(k);
        if (full is null) return null;
        lock (_gate)
        {
            if (sourcePixelsPerScreenPixel < 2 || !_levels.TryGetValue(k, out var levels)) return (full, IndexToWorld);
            var level = Math.Min(levels.Count, (int)Math.Floor(Math.Log2(sourcePixelsPerScreenPixel)));
            return level <= 0 ? (full, IndexToWorld) : (levels[level - 1], ImagePyramid.LevelToWorld(IndexToWorld, level));
        }
    }

    public void Prefetch(int center, int radius)
    {
        for (var d = 1; d <= radius; d++)
        {
            if (center + d < Count) _ = GetAsync(center + d);
            if (center - d >= 0) _ = GetAsync(center - d);
        }
    }

    public void Pin(int k)
    {
        lock (_gate) _pins[k] = _pins.GetValueOrDefault(k) + 1;
    }

    public void Unpin(int k)
    {
        lock (_gate)
        {
            if (!_pins.TryGetValue(k, out var n)) return;
            if (n <= 1) _pins.Remove(k);
            else _pins[k] = n - 1;
        }
    }

    private async Task<VoxelBuffer?> DecodeAsync(int k)
    {
        try
        {
            await _decodeSlots.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (_disposed) return null;
            var frame = Set.Frames[k];
            RawFrame raw;
            var path = frame.Instance.FilePath;
            if (frame.Instance.IsMultiFrame)
            {
                (DicomDataset Dataset, Lock Gate) entry;
                lock (_gate)
                {
                    if (!_datasets.TryGetValue(path, out entry))
                    {
                        entry = (FrameDecoder.LoadDataset(path), new Lock());
                        _datasets[path] = entry;
                    }
                }
                lock (entry.Gate) raw = FrameDecoder.Decode(entry.Dataset, frame.FrameIndex);
            }
            else
            {
                raw = FrameDecoder.Decode(FrameDecoder.LoadDataset(path), frame.FrameIndex);
            }

            var buffer = new VoxelBuffer(raw.Columns, raw.Rows, 1, Components, Type);
            VolumeLoader.WriteFrame(buffer, 0, raw, VolumeLoader.Rescale(frame));
            buffer.MarkModified();
            var levels = new List<VoxelBuffer>();
            var current = buffer;
            while (Math.Max(current.Nx, current.Ny) >= ImagePyramid.MinDimensionForPyramid)
            {
                current = ImagePyramid.Downsample(current);
                levels.Add(current);
            }
            lock (_gate)
            {
                _pending.Remove(k);
                if (_disposed)
                {
                    buffer.Dispose();
                    return null;
                }
                _frames[k] = buffer;
                _bytes += buffer.ByteLength;
                if (levels.Count > 0)
                {
                    _levels[k] = levels;
                    _bytes += levels.Sum(l => l.ByteLength);
                }
                Touch(k);
                Evict();
            }
            FrameReady?.Invoke(k);
            return buffer;
        }
        catch (Exception ex)
        {
            lock (_gate) _pending.Remove(k);
            FrameFailed?.Invoke(k, ex.Message);
            return null;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    private void Touch(int k)
    {
        _lru.Remove(k);
        _lru.AddFirst(k);
    }

    private void Evict()
    {
        var node = _lru.Last;
        while (_bytes > BudgetBytes && node is not null)
        {
            var prev = node.Previous;
            var k = node.Value;
            if (!_pins.ContainsKey(k) && _frames.Remove(k, out var buffer))
            {
                _bytes -= buffer.ByteLength;
                _lru.Remove(node);
                buffer.Dispose();
                if (_levels.Remove(k, out var levels))
                {
                    foreach (var l in levels)
                    {
                        _bytes -= l.ByteLength;
                        l.Dispose();
                    }
                }
            }
            node = prev;
        }
    }

    public long CachedBytes
    {
        get
        {
            lock (_gate) return _bytes;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            _disposed = true;
            foreach (var b in _frames.Values) b.Dispose();
            foreach (var l in _levels.Values.SelectMany(x => x)) l.Dispose();
            _frames.Clear();
            _levels.Clear();
            _lru.Clear();
            _datasets.Clear();
            _bytes = 0;
        }
    }
}
