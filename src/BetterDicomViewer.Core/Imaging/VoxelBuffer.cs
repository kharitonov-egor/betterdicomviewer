using System.Runtime.InteropServices;

namespace BetterDicomViewer.Core.Imaging;

/// <summary>Scalar type codes shared with the native bridge (VTK numbering).</summary>
public enum VoxelType
{
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    Float32 = 10,
}

public static class VoxelTypeExtensions
{
    public static int Size(this VoxelType t) => t switch
    {
        VoxelType.UInt8 => 1,
        VoxelType.Int16 or VoxelType.UInt16 => 2,
        VoxelType.Int32 or VoxelType.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
}

/// <summary>
/// Unmanaged, 64-byte aligned voxel storage shared with the native reslicer without copying.
/// <see cref="Version"/> increments on every write so native wrappers know to refresh.
/// </summary>
public sealed unsafe class VoxelBuffer : IDisposable
{
    private void* _data;
    private long _version;

    public VoxelBuffer(int nx, int ny, int nz, int components, VoxelType type)
    {
        if (nx <= 0 || ny <= 0 || nz <= 0 || components <= 0) throw new ArgumentOutOfRangeException(nameof(nx));
        Nx = nx;
        Ny = ny;
        Nz = nz;
        Components = components;
        Type = type;
        ElementCount = (long)nx * ny * nz * components;
        ByteLength = ElementCount * type.Size();
        _data = NativeMemory.AlignedAlloc((nuint)ByteLength, 64);
        NativeMemory.Clear(_data, (nuint)ByteLength);
        GC.AddMemoryPressure(ByteLength);
    }

    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    public int Components { get; }
    public VoxelType Type { get; }
    public long ElementCount { get; }
    public long ByteLength { get; }
    public long SliceElements => (long)Nx * Ny * Components;
    public long SliceBytes => SliceElements * Type.Size();
    public bool IsDisposed => _data == null;

    public long Version => Interlocked.Read(ref _version);

    public void* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data == null, this);
            return _data;
        }
    }

    public void MarkModified() => Interlocked.Increment(ref _version);

    public Span<T> AsSpan<T>() where T : unmanaged
    {
        if (sizeof(T) != Type.Size()) throw new InvalidOperationException($"Buffer holds {Type}, not {typeof(T).Name}.");
        if (ElementCount > int.MaxValue) throw new InvalidOperationException("Buffer too large for a single span; use SliceSpan.");
        return new Span<T>(Pointer, (int)ElementCount);
    }

    public Span<T> SliceSpan<T>(int k) where T : unmanaged
    {
        if (sizeof(T) != Type.Size()) throw new InvalidOperationException($"Buffer holds {Type}, not {typeof(T).Name}.");
        if ((uint)k >= (uint)Nz) throw new ArgumentOutOfRangeException(nameof(k));
        return new Span<T>((byte*)Pointer + k * SliceBytes, (int)SliceElements);
    }

    public void Fill<T>(T value) where T : unmanaged
    {
        for (var k = 0; k < Nz; k++) SliceSpan<T>(k).Fill(value);
        MarkModified();
    }

    /// <summary>Reads one scalar (component 0) as double. Intended for statistics and probes, not bulk rendering.</summary>
    public double GetValue(int x, int y, int z)
    {
        var idx = ((long)z * Ny + y) * Nx + x;
        idx *= Components;
        var p = (byte*)Pointer;
        return Type switch
        {
            VoxelType.UInt8 => p[idx],
            VoxelType.Int16 => ((short*)p)[idx],
            VoxelType.UInt16 => ((ushort*)p)[idx],
            VoxelType.Int32 => ((int*)p)[idx],
            VoxelType.Float32 => ((float*)p)[idx],
            _ => 0,
        };
    }

    public void Dispose()
    {
        if (_data == null) return;
        NativeMemory.AlignedFree(_data);
        _data = null;
        GC.RemoveMemoryPressure(ByteLength);
        GC.SuppressFinalize(this);
    }

    ~VoxelBuffer()
    {
        if (_data != null) NativeMemory.AlignedFree(_data);
    }
}
