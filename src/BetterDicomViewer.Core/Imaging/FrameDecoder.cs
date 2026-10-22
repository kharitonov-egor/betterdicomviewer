using System.Buffers.Binary;
using System.Text;
using BetterDicomViewer.Core.Dicom;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO;

namespace BetterDicomViewer.Core.Imaging;

public sealed class UnsupportedPixelDataException(string message) : Exception(message);

/// <summary>Uncompressed little-endian pixel bytes for one frame plus the layout needed to interpret them.</summary>
public sealed class RawFrame
{
    public required byte[] Data { get; init; }
    public required int Rows { get; init; }
    public required int Columns { get; init; }
    public required int BitsAllocated { get; init; }
    public required int BitsStored { get; init; }
    public required int HighBit { get; init; }
    public required bool Signed { get; init; }
    public required int SamplesPerPixel { get; init; }
    public required string Photometric { get; init; }
    public required int PlanarConfiguration { get; init; }
    public ushort[]? PaletteRed { get; init; }
    public ushort[]? PaletteGreen { get; init; }
    public ushort[]? PaletteBlue { get; init; }
    public int PaletteFirstMapped { get; init; }
    public int PaletteBits { get; init; } = 16;

    public int PixelCount => Rows * Columns;
}

public static class FrameDecoder
{
    public static DicomDataset LoadDataset(string path)
    {
        DicomRuntime.EnsureInitialized();
        var file = DicomFile.Open(path, Encoding.UTF8, null, FileReadOption.ReadLargeOnDemand, 256 * 1024);
        return file.Dataset;
    }

    public static RawFrame Decode(DicomDataset ds, int frame)
    {
        DicomRuntime.EnsureInitialized();
        if (!ds.Contains(DicomTag.PixelData))
            throw new UnsupportedPixelDataException("The object has no pixel data.");

        var syntax = ds.InternalTransferSyntax;
        var pixelData = DicomPixelData.Create(ds);
        var photometric = ds.Str(DicomTag.PhotometricInterpretation) ?? "MONOCHROME2";
        byte[] bytes;

        if (syntax.IsEncapsulated)
        {
            if (!DicomRuntime.CanDecode(syntax.UID.UID))
                throw new UnsupportedPixelDataException($"Transfer syntax not supported: {syntax.UID.Name}");
            var inputParams = IsJpegProcess(syntax) ? new DicomJpegParams { ConvertColorspaceToRGB = true } : null;
            var transcoder = new DicomTranscoder(syntax, DicomTransferSyntax.ExplicitVRLittleEndian, inputParams, null);
            var buffer = transcoder.DecodeFrame(ds, frame);
            bytes = buffer.Data;
            photometric = PhotometricAfterDecode(syntax, photometric, pixelData.SamplesPerPixel);
        }
        else
        {
            var buffer = pixelData.GetFrame(frame);
            bytes = buffer.Data;
            if (syntax.Endian == Endian.Big && pixelData.BitsAllocated == 16)
                SwapBytes16(bytes);
        }

        ushort[]? r = null, g = null, b = null;
        var first = 0;
        var paletteBits = 16;
        if (photometric.StartsWith("PALETTE", StringComparison.OrdinalIgnoreCase))
        {
            (r, first, paletteBits) = ReadPalette(ds, DicomTag.RedPaletteColorLookupTableDescriptor, DicomTag.RedPaletteColorLookupTableData);
            (g, _, _) = ReadPalette(ds, DicomTag.GreenPaletteColorLookupTableDescriptor, DicomTag.GreenPaletteColorLookupTableData);
            (b, _, _) = ReadPalette(ds, DicomTag.BluePaletteColorLookupTableDescriptor, DicomTag.BluePaletteColorLookupTableData);
            if (r is null || g is null || b is null)
                throw new UnsupportedPixelDataException("Palette color lookup tables are missing or segmented (not supported).");
        }

        var bitsAllocated = pixelData.BitsAllocated;
        var bitsStored = pixelData.BitsStored;
        // Some codecs decode 12-bit JPEG into 16-bit containers; keep the declared stored-bit layout.
        return new RawFrame
        {
            Data = bytes,
            Rows = pixelData.Height,
            Columns = pixelData.Width,
            BitsAllocated = bitsAllocated,
            BitsStored = bitsStored == 0 ? bitsAllocated : bitsStored,
            HighBit = pixelData.HighBit == 0 && bitsStored > 1 ? bitsStored - 1 : pixelData.HighBit,
            Signed = pixelData.PixelRepresentation == PixelRepresentation.Signed,
            SamplesPerPixel = pixelData.SamplesPerPixel,
            Photometric = photometric,
            PlanarConfiguration = syntax.IsEncapsulated ? 0 : (int)pixelData.PlanarConfiguration,
            PaletteRed = r,
            PaletteGreen = g,
            PaletteBlue = b,
            PaletteFirstMapped = first,
            PaletteBits = paletteBits,
        };
    }

    private static bool IsJpegProcess(DicomTransferSyntax syntax)
    {
        var uid = syntax.UID.UID;
        return uid is "1.2.840.10008.1.2.4.50" or "1.2.840.10008.1.2.4.51" or "1.2.840.10008.1.2.4.57" or "1.2.840.10008.1.2.4.70";
    }

    private static string PhotometricAfterDecode(DicomTransferSyntax syntax, string photometric, int samples)
    {
        if (samples < 3) return photometric;
        var uid = syntax.UID.UID;
        if (IsJpegProcess(syntax) && photometric.StartsWith("YBR", StringComparison.OrdinalIgnoreCase)) return "RGB";
        if (uid is "1.2.840.10008.1.2.4.90" or "1.2.840.10008.1.2.4.91" or "1.2.840.10008.1.2.4.201" or "1.2.840.10008.1.2.4.202" or "1.2.840.10008.1.2.4.203"
            && photometric is "YBR_ICT" or "YBR_RCT")
            return "RGB";
        if (photometric == "YBR_FULL_422") return "YBR_FULL";
        return photometric;
    }

    private static (ushort[]? Lut, int FirstMapped, int Bits) ReadPalette(DicomDataset ds, DicomTag descriptorTag, DicomTag dataTag)
    {
        if (!ds.Contains(descriptorTag) || !ds.Contains(dataTag)) return (null, 0, 16);
        var descriptor = ds.GetValues<int>(descriptorTag);
        if (descriptor.Length < 3) return (null, 0, 16);
        var entries = descriptor[0] == 0 ? 65536 : descriptor[0];
        var first = descriptor[1];
        var bits = descriptor[2];
        var raw = ds.GetDicomItem<DicomElement>(dataTag).Buffer.Data;
        var lut = new ushort[entries];
        if (bits == 8 && raw.Length >= entries && raw.Length < entries * 2)
        {
            for (var i = 0; i < entries; i++) lut[i] = raw[i];
        }
        else
        {
            for (var i = 0; i < entries && i * 2 + 1 < raw.Length; i++) lut[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2));
        }
        return (lut, first, bits);
    }

    private static void SwapBytes16(byte[] bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
    }

    /// <summary>Stored value of pixel <paramref name="i"/> (grayscale), with bit masking and sign extension applied.</summary>
    public static int StoredValue(RawFrame f, int i)
    {
        switch (f.BitsAllocated)
        {
            case 8:
            {
                int v = f.Data[i];
                return f.Signed ? (sbyte)(byte)v : v;
            }
            case 16:
            {
                int raw = BinaryPrimitives.ReadUInt16LittleEndian(f.Data.AsSpan(i * 2));
                return ExtractBits(raw, f.BitsStored, f.HighBit, f.Signed);
            }
            case 32:
            {
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(f.Data.AsSpan(i * 4));
                return f.Signed ? (int)raw : (int)Math.Min(raw, int.MaxValue);
            }
            case 1:
                return (f.Data[i >> 3] >> (i & 7)) & 1;
            default:
                throw new UnsupportedPixelDataException($"{f.BitsAllocated}-bit pixel data is not supported.");
        }
    }

    public static int ExtractBits(int raw, int bitsStored, int highBit, bool signed)
    {
        var shift = highBit + 1 - bitsStored;
        if (shift < 0) shift = 0;
        var value = raw >> shift;
        var mask = bitsStored >= 32 ? -1 : (1 << bitsStored) - 1;
        value &= mask;
        if (signed && bitsStored < 32 && (value & (1 << (bitsStored - 1))) != 0) value -= 1 << bitsStored;
        return value;
    }

    /// <summary>Writes modality values (stored * slope + intercept) as float.</summary>
    public static void ToFloat(RawFrame f, double slope, double intercept, Span<float> dst)
    {
        EnsureGray(f);
        var n = f.PixelCount;
        if (f.BitsAllocated == 16 && !f.Signed && f.BitsStored == 16 && f.HighBit == 15)
        {
            var src = f.Data.AsSpan();
            for (var i = 0; i < n; i++) dst[i] = (float)(BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]) * slope + intercept);
            return;
        }
        for (var i = 0; i < n; i++) dst[i] = (float)(StoredValue(f, i) * slope + intercept);
    }

    /// <summary>Writes modality values rounded into int16, clamping out-of-range values. Returns the number clamped.</summary>
    public static int ToInt16(RawFrame f, double slope, double intercept, Span<short> dst)
    {
        EnsureGray(f);
        var n = f.PixelCount;
        var clamped = 0;
        var integral = slope == 1.0 && intercept == Math.Round(intercept);
        var offset = (int)intercept;
        if (f.BitsAllocated == 16 && integral)
        {
            var simple = f.BitsStored == 16 && f.HighBit == 15;
            var src = f.Data.AsSpan();
            for (var i = 0; i < n; i++)
            {
                int raw = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]);
                var stored = simple ? (f.Signed ? (short)raw : raw) : ExtractBits(raw, f.BitsStored, f.HighBit, f.Signed);
                var v = stored + offset;
                if (v > short.MaxValue) { v = short.MaxValue; clamped++; }
                else if (v < short.MinValue) { v = short.MinValue; clamped++; }
                dst[i] = (short)v;
            }
            return clamped;
        }
        for (var i = 0; i < n; i++)
        {
            var v = Math.Round(StoredValue(f, i) * slope + intercept);
            if (v > short.MaxValue) { v = short.MaxValue; clamped++; }
            else if (v < short.MinValue) { v = short.MinValue; clamped++; }
            dst[i] = (short)v;
        }
        return clamped;
    }

    public static void ToUInt8(RawFrame f, Span<byte> dst)
    {
        EnsureGray(f);
        var n = f.PixelCount;
        if (f.BitsAllocated == 8 && !f.Signed)
        {
            f.Data.AsSpan(0, n).CopyTo(dst);
            return;
        }
        for (var i = 0; i < n; i++) dst[i] = (byte)Math.Clamp(StoredValue(f, i), 0, 255);
    }

    /// <summary>Converts color frames (RGB, YBR_FULL, YBR_FULL_422, PALETTE COLOR) to interleaved 8-bit RGB.</summary>
    public static void ToRgb24(RawFrame f, Span<byte> dst)
    {
        var n = f.PixelCount;
        var p = f.Photometric.ToUpperInvariant();
        if (p.StartsWith("PALETTE"))
        {
            var shift = f.PaletteBits > 8 ? 8 : 0;
            var last = f.PaletteRed!.Length - 1;
            for (var i = 0; i < n; i++)
            {
                var idx = Math.Clamp(StoredValue(f, i) - f.PaletteFirstMapped, 0, last);
                dst[i * 3] = (byte)(f.PaletteRed[idx] >> shift);
                dst[i * 3 + 1] = (byte)(f.PaletteGreen![idx] >> shift);
                dst[i * 3 + 2] = (byte)(f.PaletteBlue![idx] >> shift);
            }
            return;
        }

        if (f.SamplesPerPixel < 3)
        {
            for (var i = 0; i < n; i++)
            {
                var v = (byte)Math.Clamp(StoredValue(f, i), 0, 255);
                dst[i * 3] = dst[i * 3 + 1] = dst[i * 3 + 2] = v;
            }
            return;
        }

        var src = f.Data;
        var wide = f.BitsAllocated == 16;
        byte Sample(int index) => wide ? (byte)(BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(index * 2)) >> Math.Max(0, f.BitsStored - 8)) : src[index];

        if (p == "YBR_FULL_422" && !wide && src.Length >= n * 2)
        {
            // Uncompressed 4:2:2: each pair of pixels is stored as Y1 Y2 Cb Cr.
            for (var i = 0; i + 1 < n; i += 2)
            {
                var o = i * 2;
                var y1 = src[o];
                var y2 = src[o + 1];
                var cb = src[o + 2];
                var cr = src[o + 3];
                YbrToRgb(y1, cb, cr, dst[(i * 3)..]);
                YbrToRgb(y2, cb, cr, dst[((i + 1) * 3)..]);
            }
            return;
        }

        var planar = f.PlanarConfiguration == 1;
        for (var i = 0; i < n; i++)
        {
            byte c0, c1, c2;
            if (planar)
            {
                c0 = Sample(i);
                c1 = Sample(n + i);
                c2 = Sample(2 * n + i);
            }
            else
            {
                c0 = Sample(i * 3);
                c1 = Sample(i * 3 + 1);
                c2 = Sample(i * 3 + 2);
            }
            if (p is "YBR_FULL" or "YBR_FULL_422")
            {
                YbrToRgb(c0, c1, c2, dst[(i * 3)..]);
            }
            else
            {
                dst[i * 3] = c0;
                dst[i * 3 + 1] = c1;
                dst[i * 3 + 2] = c2;
            }
        }
    }

    /// <summary>ITU-R BT.601 full-range conversion as specified for YBR_FULL in PS3.3 C.7.6.3.1.2.</summary>
    public static void YbrToRgb(byte y, byte cb, byte cr, Span<byte> rgb)
    {
        var yy = (double)y;
        var cbb = cb - 128.0;
        var crr = cr - 128.0;
        rgb[0] = ClampByte(yy + 1.402 * crr);
        rgb[1] = ClampByte(yy - 0.344136 * cbb - 0.714136 * crr);
        rgb[2] = ClampByte(yy + 1.772 * cbb);
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static void EnsureGray(RawFrame f)
    {
        if (f.SamplesPerPixel != 1)
            throw new UnsupportedPixelDataException("Expected a single-sample (grayscale) frame.");
    }
}
