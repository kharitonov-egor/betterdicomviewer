using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;

namespace BetterDicomViewer.Core.Imaging;

public sealed record WindowPreset(string Name, double Center, double Width);

/// <summary>A display set decoded into one voxel buffer (index space) with its index-to-patient transform.</summary>
public sealed class ImageVolume : IDisposable
{
    private readonly bool[] _loaded;
    private int _loadedCount;
    private readonly Lock _rangeGate = new();
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private int _leases;

    public ImageVolume(DisplaySet source, VoxelBuffer data, Affine indexToWorld, bool hasPatientGeometry)
    {
        Source = source;
        Data = data;
        IndexToWorld = indexToWorld;
        WorldToIndex = indexToWorld.Inverse();
        HasPatientGeometry = hasPatientGeometry;
        _loaded = new bool[data.Nz];
        var spacing = new Vec3(indexToWorld.Column0.Length, indexToWorld.Column1.Length, indexToWorld.Column2.Length);
        Spacing = spacing;
    }

    public DisplaySet Source { get; }
    public VoxelBuffer Data { get; }
    public Affine IndexToWorld { get; }
    public Affine WorldToIndex { get; }
    public bool HasPatientGeometry { get; }
    public Vec3 Spacing { get; }
    public int Nx => Data.Nx;
    public int Ny => Data.Ny;
    public int Nz => Data.Nz;
    public bool IsColor => Data.Components >= 3;
    public string ValueUnit { get; init; } = "";
    public bool IsMonochrome1 { get; init; }
    public IReadOnlyList<WindowPreset> DicomWindows { get; init; } = [];
    public List<string> Warnings { get; } = [];
    public Pet.SuvInfo? Suv { get; set; }
    public Task Completion { get; set; } = Task.CompletedTask;
    public string? LoadError { get; set; }

    public int LoadedCount => Volatile.Read(ref _loadedCount);
    public bool IsComplete => LoadedCount >= Nz;
    public double Progress => Nz == 0 ? 1 : (double)LoadedCount / Nz;
    public bool IsSliceLoaded(int k) => (uint)k < (uint)_loaded.Length && Volatile.Read(ref _loaded[k]);

    public (double Min, double Max) ValueRange
    {
        get
        {
            lock (_rangeGate) return double.IsFinite(_min) ? (_min, _max) : (0, 1);
        }
    }

    public event Action<ImageVolume, int>? SliceLoaded;

    internal void MarkLoaded(int k, double min, double max)
    {
        lock (_rangeGate)
        {
            if (min < _min) _min = min;
            if (max > _max) _max = max;
        }
        if (!Volatile.Read(ref _loaded[k]))
        {
            Volatile.Write(ref _loaded[k], true);
            Interlocked.Increment(ref _loadedCount);
        }
        Data.MarkModified();
        SliceLoaded?.Invoke(this, k);
    }

    public Vec3 IndexToWorldPoint(Vec3 index) => IndexToWorld.TransformPoint(index);
    public Vec3 WorldToIndexPoint(Vec3 world) => WorldToIndex.TransformPoint(world);

    public bool ContainsIndex(Vec3 idx) =>
        idx.X >= -0.5 && idx.Y >= -0.5 && idx.Z >= -0.5 && idx.X <= Nx - 0.5 && idx.Y <= Ny - 0.5 && idx.Z <= Nz - 0.5;

    /// <summary>Nearest-voxel modality value at a world point, or null outside the volume or on an unloaded slice.</summary>
    public double? SampleNearest(Vec3 world)
    {
        if (IsColor) return null;
        var idx = WorldToIndexPoint(world);
        var x = (int)Math.Round(idx.X);
        var y = (int)Math.Round(idx.Y);
        var z = (int)Math.Round(idx.Z);
        if (x < 0 || y < 0 || z < 0 || x >= Nx || y >= Ny || z >= Nz || !IsSliceLoaded(z)) return null;
        return Data.GetValue(x, y, z);
    }

    public CancellationTokenSource LoadCancellation { get; } = new();

    public int Leases => Volatile.Read(ref _leases);
    public void Acquire() => Interlocked.Increment(ref _leases);
    public void Release() => Interlocked.Decrement(ref _leases);

    public void Dispose()
    {
        LoadCancellation.Cancel();
        try
        {
            Completion.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
        Data.Dispose();
    }
}
