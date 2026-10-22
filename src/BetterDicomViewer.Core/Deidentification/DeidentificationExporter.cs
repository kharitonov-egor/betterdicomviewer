using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Imaging;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Media;

namespace BetterDicomViewer.Core.Deidentification;

public sealed record DeidProgress(int Done, int Total, string Current);

public sealed class DeidReport
{
    public string Tool { get; set; } = "Better DICOM Viewer";
    public string Profile { get; set; } = "DICOM PS3.15 Annex E, Basic Application Level Confidentiality Profile";
    public string ProfileTable { get; set; } = ConfidentialityProfile.Edition;
    public string[] Method { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string OutputFolder { get; set; } = "";
    public int SourceObjects { get; set; }
    public int Exported { get; set; }
    public int ExcludedNonImage { get; set; }
    public int ExcludedBurnedIn { get; set; }
    public int FlaggedBurnedIn { get; set; }
    public int Redacted { get; set; }
    public int Failed { get; set; }
    public int UidsRemapped { get; set; }
    public List<string> Scope { get; set; } = [];
    public List<DeidReportItem> Items { get; set; } = [];
    public List<string> Limitations { get; set; } =
    [
        "Removing and replacing attributes does not guarantee anonymity.",
        "Identifying text burned into pixel data is only removed where redaction rectangles were applied; review every exported image.",
        "Faces and other recognizable features in 3D reconstructions of head scans are not removed.",
        "All private attributes are removed, which can drop vendor-specific information.",
        "Objects without pixel data (structured reports, presentation states, PDFs) are not exported.",
    ];
}

public sealed class DeidReportItem
{
    public string Output { get; set; } = "";
    public string Modality { get; set; } = "";
    public string SopClass { get; set; } = "";
    public string Status { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>Writes de-identified copies to a new folder (a DICOM File-set with DICOMDIR). Source files are only read.</summary>
public static class DeidentificationExporter
{
    public static async Task<DeidReport> ExportAsync(
        IReadOnlyList<InstanceInfo> instances,
        IReadOnlyCollection<string> sourceRoots,
        string outputFolder,
        DeidentificationOptions options,
        IProgress<DeidProgress>? progress,
        CancellationToken ct)
    {
        DicomRuntime.EnsureInitialized();
        var output = Path.GetFullPath(outputFolder);
        foreach (var root in sourceRoots)
        {
            if (AppPaths.IsSameOrInside(output, root) || AppPaths.IsSameOrInside(root, output))
                throw new InvalidOperationException("The export folder must be outside the source scan folders.");
        }
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("The export folder must be empty.");

        var deid = new Deidentifier(options);
        var report = new DeidReport
        {
            OutputFolder = output,
            SourceObjects = instances.Count,
            Method = deid.MethodDescriptions(),
        };
        if (options.RetainDescriptions)
            report.Limitations.Add("Study and series descriptions were kept verbatim and may contain identifying text.");
        if (options.RetainPatientCharacteristics)
            report.Limitations.Add("Patient characteristics (age, sex, weight, height) were kept, for example for SUV.");
        if (options.Dates == DateHandling.KeepFull)
            report.Limitations.Add("Full dates were kept.");

        var studies = instances.Select(i => i.StudyInstanceUid).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var seriesIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var written = new List<string>();
        report.Scope = instances
            .GroupBy(i => (i.StudyInstanceUid, i.SeriesInstanceUid))
            .Select(g => $"{g.First().Modality} series #{g.First().SeriesNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}: {g.Count()} object(s)")
            .ToList();

        var ordered = instances
            .OrderBy(i => studies.IndexOf(i.StudyInstanceUid))
            .ThenBy(i => i.SeriesNumber ?? int.MaxValue)
            .ThenBy(i => i.SeriesInstanceUid, StringComparer.Ordinal)
            .ThenBy(i => i.InstanceNumber ?? int.MaxValue)
            .ToList();

        var done = 0;
        foreach (var inst in ordered)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DeidProgress(done, ordered.Count, inst.Modality ?? ""));
            var item = new DeidReportItem { Modality = inst.Modality ?? "", SopClass = SopClassNames.Describe(inst.SopClassUid) };
            report.Items.Add(item);
            done++;

            if (!BurnedInAssessment.IsExportableObject(inst, out var why))
            {
                item.Status = "excluded";
                item.Note = why;
                report.ExcludedNonImage++;
                continue;
            }

            var (suspect, reason) = BurnedInAssessment.Assess(inst);
            List<RedactionRegion>? regions = null;
            if (suspect)
            {
                if (options.BurnedIn == BurnedInPolicy.Exclude)
                {
                    item.Status = "excluded";
                    item.Note = reason;
                    report.ExcludedBurnedIn++;
                    continue;
                }
                if (options.BurnedIn == BurnedInPolicy.Redact)
                {
                    options.Redactions.TryGetValue(DeidentificationOptions.SizeKey(inst.Columns, inst.Rows), out regions);
                    if (regions is null || regions.Count == 0)
                    {
                        item.Status = "excluded";
                        item.Note = reason + " No redaction region was defined for this image size.";
                        report.ExcludedBurnedIn++;
                        continue;
                    }
                }
            }

            try
            {
                var file = await DicomFile.OpenAsync(inst.FilePath, FileReadOption.ReadAll).ConfigureAwait(false);
                var ds = deid.Process(file.Dataset);
                if (regions is not null)
                {
                    ds = PixelRedactor.Apply(ds, regions);
                    report.Redacted++;
                    item.Note = $"{reason} Redacted {regions.Count} region(s); verify visually.";
                }
                else if (suspect)
                {
                    report.FlaggedBurnedIn++;
                    item.Note = reason + " Exported without pixel cleaning.";
                }

                var studyNo = studies.IndexOf(inst.StudyInstanceUid) + 1;
                var seriesKey = inst.StudyInstanceUid + "|" + inst.SeriesInstanceUid;
                if (!seriesIndex.TryGetValue(seriesKey, out var seNo))
                {
                    seNo = seriesIndex.Count(kv => kv.Key.StartsWith(inst.StudyInstanceUid + "|", StringComparison.Ordinal)) + 1;
                    seriesIndex[seriesKey] = seNo;
                }
                var imNo = counters[seriesKey] = counters.GetValueOrDefault(seriesKey) + 1;
                var rel = Path.Combine("DICOM", $"ST{studyNo:D2}", $"SE{seNo:D3}", $"IM{imNo:D5}");
                var path = Path.Combine(output, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var outFile = new DicomFile(ds);
                await outFile.SaveAsync(path).ConfigureAwait(false);
                written.Add(path);
                item.Output = rel;
                item.Status = regions is not null ? "exported (redacted)" : suspect ? "exported (flagged)" : "exported";
                report.Exported++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Status = "failed";
                item.Note = ex.Message;
                report.Failed++;
            }
        }

        if (written.Count > 0)
        {
            var dicomDir = new DicomDirectory();
            foreach (var f in written)
                dicomDir.AddFile(await DicomFile.OpenAsync(f).ConfigureAwait(false), Path.GetRelativePath(output, f));
            await dicomDir.SaveAsync(Path.Combine(output, "DICOMDIR")).ConfigureAwait(false);
        }

        report.UidsRemapped = deid.UidsRemapped;
        progress?.Report(new DeidProgress(ordered.Count, ordered.Count, "Writing report"));
        await File.WriteAllTextAsync(Path.Combine(output, "deidentification-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "deidentification-report.txt"), RenderText(report), ct).ConfigureAwait(false);
        return report;
    }

    public static string RenderText(DeidReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Better DICOM Viewer de-identification report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Created (UTC): {r.CreatedUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Profile: {r.Profile}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Attribute table: {r.ProfileTable}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Method: {string.Join("; ", r.Method)}");
        sb.AppendLine();
        sb.AppendLine("Scope (source objects selected for export):");
        foreach (var s in r.Scope) sb.AppendLine("  " + s);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Exported: {r.Exported} of {r.SourceObjects}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Excluded, not an image: {r.ExcludedNonImage}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Excluded, possible burned-in text: {r.ExcludedBurnedIn}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Exported with possible burned-in text (not cleaned): {r.FlaggedBurnedIn}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Exported with redaction rectangles applied: {r.Redacted}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Failed: {r.Failed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"UIDs remapped consistently: {r.UidsRemapped}");
        sb.AppendLine();
        sb.AppendLine("Limitations:");
        foreach (var l in r.Limitations) sb.AppendLine("  - " + l);
        sb.AppendLine();
        sb.AppendLine("Objects:");
        foreach (var i in r.Items)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  [{i.Status}] {i.Modality} {i.SopClass} {i.Output} {i.Note}").TrimEnd());
        return sb.ToString();
    }
}

/// <summary>Blacks out rectangles in every frame and rewrites the pixel data uncompressed (Explicit VR Little Endian).</summary>
public static class PixelRedactor
{
    public static DicomDataset Apply(DicomDataset ds, IReadOnlyList<RedactionRegion> regions)
    {
        var pixelData = DicomPixelData.Create(ds);
        var frames = pixelData.NumberOfFrames;
        var outDs = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        foreach (var item in ds)
        {
            if (item.Tag.Group == 0x7FE0) continue;
            outDs.Add(item);
        }

        var decoded = new List<RawFrame>(frames);
        for (var f = 0; f < frames; f++) decoded.Add(FrameDecoder.Decode(ds, f));
        var first = decoded[0];
        var color = first.SamplesPerPixel >= 3 || first.Photometric.StartsWith("PALETTE", StringComparison.OrdinalIgnoreCase);

        if (color)
        {
            outDs.AddOrUpdate(DicomTag.PhotometricInterpretation, "RGB");
            outDs.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)3);
            outDs.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0);
            outDs.AddOrUpdate(DicomTag.BitsAllocated, (ushort)8);
            outDs.AddOrUpdate(DicomTag.BitsStored, (ushort)8);
            outDs.AddOrUpdate(DicomTag.HighBit, (ushort)7);
            outDs.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
            outDs.Remove(DicomTag.RedPaletteColorLookupTableDescriptor, DicomTag.GreenPaletteColorLookupTableDescriptor,
                DicomTag.BluePaletteColorLookupTableDescriptor, DicomTag.RedPaletteColorLookupTableData,
                DicomTag.GreenPaletteColorLookupTableData, DicomTag.BluePaletteColorLookupTableData);
        }

        var newPixels = DicomPixelData.Create(outDs, true);
        foreach (var raw in decoded)
        {
            byte[] bytes;
            if (color)
            {
                bytes = new byte[raw.PixelCount * 3];
                FrameDecoder.ToRgb24(raw, bytes);
                Mask(raw.Columns, raw.Rows, regions, (x, y) => { var o = (y * raw.Columns + x) * 3; bytes[o] = bytes[o + 1] = bytes[o + 2] = 0; });
            }
            else
            {
                bytes = (byte[])raw.Data.Clone();
                var mono1 = raw.Photometric.Equals("MONOCHROME1", StringComparison.OrdinalIgnoreCase);
                var black = mono1
                    ? (raw.Signed ? (1 << (raw.BitsStored - 1)) - 1 : (1 << raw.BitsStored) - 1)
                    : (raw.Signed ? -(1 << (raw.BitsStored - 1)) : 0);
                var shifted = black << Math.Max(0, raw.HighBit + 1 - raw.BitsStored);
                Mask(raw.Columns, raw.Rows, regions, (x, y) =>
                {
                    var i = y * raw.Columns + x;
                    if (raw.BitsAllocated == 16)
                    {
                        bytes[i * 2] = (byte)shifted;
                        bytes[i * 2 + 1] = (byte)(shifted >> 8);
                    }
                    else if (raw.BitsAllocated == 8)
                    {
                        bytes[i] = (byte)shifted;
                    }
                });
            }
            newPixels.AddFrame(new MemoryByteBuffer(bytes));
        }
        return outDs;
    }

    private static void Mask(int columns, int rows, IReadOnlyList<RedactionRegion> regions, Action<int, int> set)
    {
        foreach (var r in regions)
        {
            var x0 = Math.Clamp(r.X, 0, columns);
            var y0 = Math.Clamp(r.Y, 0, rows);
            var x1 = Math.Clamp(r.X + r.Width, 0, columns);
            var y1 = Math.Clamp(r.Y + r.Height, 0, rows);
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                set(x, y);
        }
    }
}
