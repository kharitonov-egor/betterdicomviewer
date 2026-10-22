using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterDicomViewer.Core.Annotations;

namespace BetterDicomViewer.Core.Export;

/// <summary>One exported measurement. The schema is documented in docs/measurement-export.md.</summary>
public sealed class MeasurementRow
{
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";
    public string Modality { get; set; } = "";
    public int? SeriesNumber { get; set; }
    public string SeriesDescription { get; set; } = "";
    public string ImageSet { get; set; } = "";
    public int? Frame { get; set; }
    public double? Length { get; set; }
    public string? LengthUnit { get; set; }
    public double? AngleDegrees { get; set; }
    public double? Area { get; set; }
    public string? AreaUnit { get; set; }
    public int? VoxelCount { get; set; }
    public double? Mean { get; set; }
    public double? StdDev { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? ValueUnit { get; set; }
    public double? SuvMax { get; set; }
    public double? SuvMean { get; set; }
    public double? SuvPeak { get; set; }
    public double? PetSuvMax { get; set; }
    public double? PetSuvMean { get; set; }
    public double? PetSuvPeak { get; set; }
    public double? PointValue { get; set; }
    public double? PointSuv { get; set; }
    public string? Text { get; set; }
    public string? CalibrationNote { get; set; }
    public string CoordinateSystem { get; set; } = "";
    public string Points { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? PatientName { get; set; }
    public string? PatientId { get; set; }

    public static MeasurementRow From(Annotation a, MeasurementResult? r, string modality, int? seriesNumber, string seriesDescription, string imageSet)
    {
        var ci = CultureInfo.InvariantCulture;
        return new MeasurementRow
        {
            Label = a.Label,
            Type = Annotation.KindName(a.Kind),
            Modality = modality,
            SeriesNumber = seriesNumber,
            SeriesDescription = seriesDescription,
            ImageSet = imageSet,
            Frame = a.FrameIndex,
            Length = r?.Length,
            LengthUnit = r?.Length is null ? null : r.LengthUnit,
            AngleDegrees = r?.AngleDegrees,
            Area = r?.Area,
            AreaUnit = r?.Area is null ? null : r.AreaUnit,
            VoxelCount = r?.Stats?.Count,
            Mean = r?.Stats is { Count: > 0 } s ? s.Mean : null,
            StdDev = r?.Stats is { Count: > 0 } s2 ? s2.StdDev : null,
            Min = r?.Stats is { Count: > 0 } s3 ? s3.Min : null,
            Max = r?.Stats is { Count: > 0 } s4 ? s4.Max : null,
            ValueUnit = r?.Stats?.Unit ?? (r?.ProbeValue is null ? null : r.ValueUnit),
            SuvMax = r?.Stats?.SuvMax,
            SuvMean = r?.Stats?.SuvMean,
            SuvPeak = r?.Stats?.SuvPeak,
            PetSuvMax = r?.SecondaryStats?.SuvMax,
            PetSuvMean = r?.SecondaryStats?.SuvMean,
            PetSuvPeak = r?.SecondaryStats?.SuvPeak,
            PointValue = r?.ProbeValue,
            PointSuv = r?.ProbeSuv,
            Text = a.Text,
            CalibrationNote = r?.CalibrationNote,
            CoordinateSystem = a.PatientSpace ? "DICOM patient (LPS), mm" : "image pixels (column, row), scaled for pixel aspect",
            Points = string.Join(" | ", a.Points.Select(p => string.Create(ci, $"{p.X:0.###};{p.Y:0.###};{p.Z:0.###}"))),
            CreatedUtc = a.CreatedUtc,
        };
    }
}

public static class MeasurementExport
{
    private static readonly string[] Columns =
    [
        "Label", "Type", "Modality", "SeriesNumber", "SeriesDescription", "ImageSet", "Frame", "Length", "LengthUnit", "AngleDegrees",
        "Area", "AreaUnit", "VoxelCount", "Mean", "StdDev", "Min", "Max", "ValueUnit", "SuvMax", "SuvMean", "SuvPeak",
        "PetSuvMax", "PetSuvMean", "PetSuvPeak", "PointValue", "PointSuv", "Text", "CalibrationNote", "CoordinateSystem", "Points", "CreatedUtc",
        "PatientName", "PatientId",
    ];

    public static void WriteCsv(string path, IEnumerable<MeasurementRow> rows)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var r in rows)
        {
            string N(double? v) => v?.ToString("0.####", ci) ?? "";
            var values = new[]
            {
                r.Label, r.Type, r.Modality, r.SeriesNumber?.ToString(ci) ?? "", r.SeriesDescription, r.ImageSet, r.Frame?.ToString(ci) ?? "",
                N(r.Length), r.LengthUnit ?? "", N(r.AngleDegrees), N(r.Area), r.AreaUnit ?? "", r.VoxelCount?.ToString(ci) ?? "",
                N(r.Mean), N(r.StdDev), N(r.Min), N(r.Max), r.ValueUnit ?? "", N(r.SuvMax), N(r.SuvMean), N(r.SuvPeak),
                N(r.PetSuvMax), N(r.PetSuvMean), N(r.PetSuvPeak), N(r.PointValue), N(r.PointSuv), r.Text ?? "", r.CalibrationNote ?? "",
                r.CoordinateSystem, r.Points, r.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", ci), r.PatientName ?? "", r.PatientId ?? "",
            };
            sb.AppendLine(string.Join(",", values.Select(Escape)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void WriteJson(string path, IEnumerable<MeasurementRow> rows)
    {
        var doc = new
        {
            format = "better-dicom-viewer.measurements",
            version = 1,
            exportedUtc = DateTime.UtcNow,
            measurements = rows.ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
