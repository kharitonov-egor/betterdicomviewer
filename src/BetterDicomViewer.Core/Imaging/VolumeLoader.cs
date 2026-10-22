using System.Globalization;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Pet;
using FellowOakDicom;

namespace BetterDicomViewer.Core.Imaging;

public static class VolumeLoader
{
    public static int DefaultParallelism => Math.Max(2, Environment.ProcessorCount - 2);

    /// <summary>
    /// Chooses compact storage: CT-like integer data with unit slope stays int16 in modality units (HU); PET and
    /// non-integer rescales use float32 so no precision is lost; 8-bit unsigned without rescale stays uint8.
    /// </summary>
    public static VoxelType ChooseVoxelType(IReadOnlyList<FrameRef> frames, out bool clampRisk)
    {
        clampRisk = false;
        var first = frames[0].Instance;
        if (first.SamplesPerPixel >= 3 || first.PhotometricInterpretation?.StartsWith("PALETTE", StringComparison.OrdinalIgnoreCase) == true)
            return VoxelType.UInt8;

        var rescales = frames.Select(f => Rescale(f)).Distinct().ToList();
        var integral = rescales.All(r => r.Slope == 1.0 && r.Intercept == Math.Round(r.Intercept));
        if (string.Equals(first.Modality, "PT", StringComparison.OrdinalIgnoreCase) || !integral || first.BitsAllocated == 32)
            return VoxelType.Float32;

        if (first.BitsAllocated <= 8 && first.PixelRepresentation == 0 && rescales.All(r => r.Intercept == 0))
            return VoxelType.UInt8;

        var bits = Math.Max(1, first.BitsStored);
        var signed = first.PixelRepresentation == 1;
        double min = signed ? -(1 << (bits - 1)) : 0;
        double max = signed ? (1 << (bits - 1)) - 1 : (1 << bits) - 1;
        var lo = rescales.Min(r => min + r.Intercept);
        var hi = rescales.Max(r => max + r.Intercept);
        if (lo < short.MinValue || hi > short.MaxValue)
        {
            if (bits >= 16) clampRisk = true;
            else return VoxelType.Float32;
        }
        return VoxelType.Int16;
    }

    public static (double Slope, double Intercept) Rescale(FrameRef f)
    {
        var i = f.Instance;
        if (i.PerFrame is { } pf && f.FrameIndex < pf.Length)
        {
            var g = pf[f.FrameIndex];
            return (g.RescaleSlope ?? i.RescaleSlope, g.RescaleIntercept ?? i.RescaleIntercept);
        }
        return (i.RescaleSlope, i.RescaleIntercept);
    }

    public static IReadOnlyList<WindowPreset> DicomWindowPresets(InstanceInfo i)
    {
        var list = new List<WindowPreset>();
        if (i.WindowCenter is { } c && i.WindowWidth is { } w)
        {
            for (var k = 0; k < Math.Min(c.Length, w.Length); k++)
            {
                if (w[k] <= 0) continue;
                var name = i.WindowExplanation is { } e && k < e.Length && !string.IsNullOrWhiteSpace(e[k]) ? e[k] : $"DICOM {k + 1}";
                list.Add(new WindowPreset(name, c[k], w[k]));
            }
        }
        return list;
    }

    public static string ValueUnitFor(InstanceInfo i) =>
        i.Modality?.ToUpperInvariant() switch
        {
            "CT" => "HU",
            "PT" => i.Pet?.Units?.ToUpperInvariant() switch
            {
                "BQML" => "Bq/mL",
                "GML" => "g/mL",
                "CNTS" => "counts",
                { } u => u,
                null => "",
            },
            _ => string.Equals(i.RescaleType, "HU", StringComparison.OrdinalIgnoreCase) ? "HU" : "",
        };

    /// <summary>Allocates the volume and starts progressive decoding: the priority slice first, then alternating outward.</summary>
    public static ImageVolume Start(DisplaySet set, int prioritySlice, int? parallelism = null)
    {
        if (!set.IsVolume) throw new ArgumentException("Display set is not a volume.", nameof(set));
        var g = set.Geometry!;
        var type = ChooseVoxelType(set.Frames, out var clampRisk);
        var components = set.IsColor ? 3 : 1;
        var buffer = new VoxelBuffer(g.Nx, g.Ny, g.Nz, components, type);
        var rep = set.Representative;
        var volume = new ImageVolume(set, buffer, g.IndexToPatient, hasPatientGeometry: true)
        {
            ValueUnit = ValueUnitFor(rep),
            IsMonochrome1 = string.Equals(rep.PhotometricInterpretation, "MONOCHROME1", StringComparison.OrdinalIgnoreCase),
            DicomWindows = DicomWindowPresets(rep),
        };
        if (type == VoxelType.Int16 && volume.ValueUnit == "HU") buffer.Fill<short>(-1024);
        volume.Warnings.AddRange(set.Warnings);
        if (clampRisk) volume.Warnings.Add("Some stored values may exceed the int16 range and are clamped for display.");
        if (rep.HasModalityLutSequence) volume.Warnings.Add("A Modality LUT Sequence is present and is not applied; values use Rescale Slope/Intercept.");
        if (string.Equals(rep.Modality, "PT", StringComparison.OrdinalIgnoreCase))
            volume.Suv = SuvCalculator.Compute(set.Frames.Select(f => f.Instance).ToList());

        var ct = volume.LoadCancellation.Token;
        volume.Completion = Task.Run(() => LoadAll(volume, prioritySlice, parallelism ?? DefaultParallelism, ct), ct);
        return volume;
    }

    private static IEnumerable<int> OutwardOrder(int count, int start)
    {
        start = Math.Clamp(start, 0, count - 1);
        yield return start;
        for (var d = 1; d < count; d++)
        {
            if (start + d < count) yield return start + d;
            if (start - d >= 0) yield return start - d;
        }
    }

    private static async Task LoadAll(ImageVolume volume, int prioritySlice, int parallelism, CancellationToken ct)
    {
        var set = volume.Source;
        var g = set.Geometry!;
        var errors = 0;
        string? firstError = null;

        try
        {
            if (!g.IsResampled)
            {
                await Parallel.ForEachAsync(OutwardOrder(g.Nz, prioritySlice), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, (k, token) =>
                {
                    try
                    {
                        DecodeSliceInto(volume, set.Frames[k], k);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref errors);
                        firstError ??= ex.Message;
                        volume.MarkLoaded(k, 0, 0);
                    }
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }
            else
            {
                await LoadResampled(volume, prioritySlice, parallelism, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (errors > 0)
        {
            volume.LoadError = $"{errors} slice(s) could not be decoded: {firstError}";
            volume.Warnings.Add(volume.LoadError);
        }
    }

    private static async Task LoadResampled(ImageVolume volume, int prioritySlice, int parallelism, CancellationToken ct)
    {
        var set = volume.Source;
        var g = set.Geometry!;
        var sliceElements = (int)volume.Data.SliceElements;
        var decoded = new float[set.Frames.Count][];
        var gate = new Lock();

        float[] Source(int index)
        {
            lock (gate)
            {
                if (decoded[index] is { } cached) return cached;
            }
            var values = DecodeToFloat(set.Frames[index], sliceElements);
            lock (gate) decoded[index] = values;
            return values;
        }

        await Parallel.ForEachAsync(OutwardOrder(g.Nz, prioritySlice), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, (k, token) =>
        {
            var src = g.SliceSources[k];
            var a = Source(src.SourceA);
            var b = src.SourceB == src.SourceA ? a : Source(src.SourceB);
            var w = (float)src.WeightB;
            var blended = new float[sliceElements];
            for (var i = 0; i < sliceElements; i++) blended[i] = a[i] + (b[i] - a[i]) * w;
            WriteSlice(volume, k, blended);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static float[] DecodeToFloat(FrameRef frame, int elements)
    {
        var ds = FrameDecoder.LoadDataset(frame.Instance.FilePath);
        var raw = FrameDecoder.Decode(ds, frame.FrameIndex);
        var (slope, intercept) = Rescale(frame);
        var values = new float[elements];
        FrameDecoder.ToFloat(raw, slope, intercept, values);
        return values;
    }

    private static void WriteSlice(ImageVolume volume, int k, float[] values)
    {
        var buffer = volume.Data;
        double min = double.MaxValue, max = double.MinValue;
        switch (buffer.Type)
        {
            case VoxelType.Float32:
            {
                var dst = buffer.SliceSpan<float>(k);
                values.AsSpan().CopyTo(dst);
                break;
            }
            case VoxelType.Int16:
            {
                var dst = buffer.SliceSpan<short>(k);
                for (var i = 0; i < values.Length; i++) dst[i] = (short)Math.Clamp(Math.Round(values[i]), short.MinValue, short.MaxValue);
                break;
            }
            case VoxelType.UInt8:
            {
                var dst = buffer.SliceSpan<byte>(k);
                for (var i = 0; i < values.Length; i++) dst[i] = (byte)Math.Clamp(Math.Round(values[i]), 0, 255);
                break;
            }
            default:
                throw new UnsupportedPixelDataException($"Unexpected voxel type {buffer.Type}.");
        }
        foreach (var v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        volume.MarkLoaded(k, min, max);
    }

    public static void DecodeSliceInto(ImageVolume volume, FrameRef frame, int k)
    {
        var ds = FrameDecoder.LoadDataset(frame.Instance.FilePath);
        var raw = FrameDecoder.Decode(ds, frame.FrameIndex);
        if (raw.Rows != volume.Ny || raw.Columns != volume.Nx)
            throw new UnsupportedPixelDataException(string.Create(CultureInfo.InvariantCulture, $"Frame size {raw.Columns}x{raw.Rows} does not match the volume."));
        var (min, max) = WriteFrame(volume.Data, k, raw, Rescale(frame));
        volume.MarkLoaded(k, min, max);
    }

    /// <summary>Converts one decoded frame into slice <paramref name="k"/> of the buffer; returns the value range written.</summary>
    public static (double Min, double Max) WriteFrame(VoxelBuffer buffer, int k, RawFrame raw, (double Slope, double Intercept) rescale)
    {
        switch (buffer.Type)
        {
            case VoxelType.UInt8 when buffer.Components == 3:
            {
                FrameDecoder.ToRgb24(raw, buffer.SliceSpan<byte>(k));
                return (0, 255);
            }
            case VoxelType.UInt8:
            {
                var dst = buffer.SliceSpan<byte>(k);
                FrameDecoder.ToUInt8(raw, dst);
                return MinMax(dst);
            }
            case VoxelType.Int16:
            {
                var dst = buffer.SliceSpan<short>(k);
                FrameDecoder.ToInt16(raw, rescale.Slope, rescale.Intercept, dst);
                return MinMax(dst);
            }
            case VoxelType.Float32:
            {
                var dst = buffer.SliceSpan<float>(k);
                FrameDecoder.ToFloat(raw, rescale.Slope, rescale.Intercept, dst);
                return MinMax(dst);
            }
            default:
                throw new UnsupportedPixelDataException($"Unexpected voxel type {buffer.Type}.");
        }
    }

    private static (double, double) MinMax(ReadOnlySpan<byte> s)
    {
        byte min = 255, max = 0;
        foreach (var v in s)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    private static (double, double) MinMax(ReadOnlySpan<short> s)
    {
        short min = short.MaxValue, max = short.MinValue;
        foreach (var v in s)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    private static (double, double) MinMax(ReadOnlySpan<float> s)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in s)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }
}
