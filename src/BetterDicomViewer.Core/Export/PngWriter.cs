using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BetterDicomViewer.Core.Export;

/// <summary>Minimal RGBA PNG encoder for headless exports (benchmarks, tests). The WPF app uses the system encoder.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes 32-bit BGRA pixels (as produced by the renderers) to a PNG file.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<uint> bgra, int width, int height)
    {
        using var fs = File.Create(path);
        WriteBgra(fs, bgra, width, height);
    }

    public static void WriteBgra(Stream stream, ReadOnlySpan<uint> bgra, int width, int height)
    {
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(stream, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    var p = bgra[y * width + x];
                    row[1 + x * 4] = (byte)(p >> 16);
                    row[2 + x * 4] = (byte)(p >> 8);
                    row[3 + x * 4] = (byte)p;
                    row[4 + x * 4] = (byte)(p >> 24);
                }
                z.Write(row);
            }
        }
        WriteChunk(stream, "IDAT", raw.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
