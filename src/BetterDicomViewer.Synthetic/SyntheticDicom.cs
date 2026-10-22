using System.Globalization;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Media;

namespace BetterDicomViewer.Synthetic;

/// <summary>Options for a synthetic single-frame image series with exactly known geometry and values.</summary>
public sealed class SeriesOptions
{
    public string Modality { get; set; } = "CT";
    public int Columns { get; set; } = 64;
    public int Rows { get; set; } = 64;
    public int Slices { get; set; } = 32;
    public double ColumnSpacing { get; set; } = 0.8;
    public double RowSpacing { get; set; } = 0.8;
    public double SliceSpacing { get; set; } = 2.5;
    public double? SliceThickness { get; set; }

    /// <summary>Optional explicit gaps between consecutive slices (length Slices-1); overrides SliceSpacing.</summary>
    public double[]? SliceGaps { get; set; }

    public Vec3 Origin { get; set; } = new(-25, -25, -40);
    public Vec3 RowDirection { get; set; } = Vec3.UnitX;
    public Vec3 ColumnDirection { get; set; } = Vec3.UnitY;

    /// <summary>In-plane shift added per slice (gantry tilt / shear).</summary>
    public Vec3 ShearPerSlice { get; set; } = Vec3.Zero;

    /// <summary>Modality value (e.g. HU or Bq/mL) at a patient-space point.</summary>
    public Func<Vec3, double> Value { get; set; } = _ => 0;

    public double RescaleSlope { get; set; } = 1;
    public double RescaleIntercept { get; set; } = -1024;
    public int BitsStored { get; set; } = 12;
    public bool Signed { get; set; }

    /// <summary>For float data such as PET: choose a per-slice slope so stored values fill int16.</summary>
    public bool PerSliceSlope { get; set; }

    public DicomTransferSyntax TransferSyntax { get; set; } = DicomTransferSyntax.ExplicitVRLittleEndian;
    public bool Extensionless { get; set; } = true;
    public bool ShuffleFileNames { get; set; } = true;
    public bool ReverseInstanceNumbers { get; set; }

    public string PatientName { get; set; } = "SYNTHETIC^PHANTOM";
    public string PatientId { get; set; } = "SYN-0001";
    public string StudyDescription { get; set; } = "Synthetic phantom study";
    public string SeriesDescription { get; set; } = "Synthetic series";
    public int SeriesNumber { get; set; } = 1;
    public string StudyInstanceUid { get; set; } = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    public string SeriesInstanceUid { get; set; } = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    public string FrameOfReferenceUid { get; set; } = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    public string StudyDate { get; set; } = "20240102";
    public string StudyTime { get; set; } = "100000";

    public Action<DicomDataset, int>? Customize { get; set; }
}

public static class SyntheticDicom
{
    public static Vec3 SlicePosition(SeriesOptions o, int k)
    {
        var normal = Vec3.Cross(o.RowDirection, o.ColumnDirection).Normalized();
        var offset = 0.0;
        for (var i = 0; i < k; i++) offset += o.SliceGaps?[i] ?? o.SliceSpacing;
        return o.Origin + normal * offset + o.ShearPerSlice * k;
    }

    public static Vec3 PatientPoint(SeriesOptions o, int i, int j, int k) =>
        SlicePosition(o, k) + o.RowDirection * (i * o.ColumnSpacing) + o.ColumnDirection * (j * o.RowSpacing);

    public static List<string> WriteSeries(string directory, SeriesOptions o)
    {
        DicomRuntime.EnsureInitialized();
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var order = Enumerable.Range(0, o.Slices).ToList();
        var names = o.ShuffleFileNames
            ? order.OrderBy(k => (k * 7919) % 104729).ToList()
            : order;

        for (var k = 0; k < o.Slices; k++)
        {
            var ds = CreateImageDataset(o, k);
            o.Customize?.Invoke(ds, k);
            var file = new DicomFile(ds);
            if (o.TransferSyntax != DicomTransferSyntax.ExplicitVRLittleEndian)
                file = file.Clone(o.TransferSyntax);
            var name = $"IM{names[k]:D5}" + (o.Extensionless ? "" : ".dcm");
            var path = Path.Combine(directory, name);
            file.Save(path);
            paths.Add(path);
        }
        return paths;
    }

    public static DicomDataset CreateImageDataset(SeriesOptions o, int k)
    {
        var position = SlicePosition(o, k);
        var instanceNumber = o.ReverseInstanceNumbers ? o.Slices - k : k + 1;
        var sopClass = o.Modality switch
        {
            "PT" => DicomUID.PositronEmissionTomographyImageStorage,
            "MR" => DicomUID.MRImageStorage,
            _ => DicomUID.CTImageStorage,
        };

        var values = new double[o.Rows * o.Columns];
        for (var j = 0; j < o.Rows; j++)
        for (var i = 0; i < o.Columns; i++)
            values[j * o.Columns + i] = o.Value(PatientPoint(o, i, j, k));

        var slope = o.RescaleSlope;
        var intercept = o.RescaleIntercept;
        if (o.PerSliceSlope)
        {
            var max = values.Max(Math.Abs);
            slope = max > 0 ? max / 32000.0 : 1.0;
            intercept = 0;
        }

        var pixels = new byte[values.Length * 2];
        var maxStored = o.Signed ? (1 << (o.BitsStored - 1)) - 1 : (1 << o.BitsStored) - 1;
        var minStored = o.Signed ? -(1 << (o.BitsStored - 1)) : 0;
        for (var p = 0; p < values.Length; p++)
        {
            var stored = (int)Math.Round((values[p] - intercept) / slope);
            stored = Math.Clamp(stored, minStored, maxStored);
            var raw = (ushort)(stored & 0xFFFF);
            pixels[p * 2] = (byte)raw;
            pixels[p * 2 + 1] = (byte)(raw >> 8);
        }

        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, sopClass },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, o.StudyInstanceUid },
            { DicomTag.SeriesInstanceUID, o.SeriesInstanceUid },
            { DicomTag.FrameOfReferenceUID, o.FrameOfReferenceUid },
            { DicomTag.PatientName, o.PatientName },
            { DicomTag.PatientID, o.PatientId },
            { DicomTag.PatientBirthDate, "19700101" },
            { DicomTag.PatientSex, "O" },
            { DicomTag.StudyDate, o.StudyDate },
            { DicomTag.StudyTime, o.StudyTime },
            { DicomTag.SeriesDate, o.StudyDate },
            { DicomTag.SeriesTime, o.StudyTime },
            { DicomTag.AccessionNumber, "SYNACC1" },
            { DicomTag.StudyDescription, o.StudyDescription },
            { DicomTag.SeriesDescription, o.SeriesDescription },
            { DicomTag.Modality, o.Modality },
            { DicomTag.Manufacturer, "Better DICOM Viewer synthetic" },
            { DicomTag.SeriesNumber, o.SeriesNumber.ToString(CultureInfo.InvariantCulture) },
            { DicomTag.InstanceNumber, instanceNumber.ToString(CultureInfo.InvariantCulture) },
            { DicomTag.ImageType, "ORIGINAL", "PRIMARY", "AXIAL" },
            { DicomTag.ImagePositionPatient, Ds(position.X), Ds(position.Y), Ds(position.Z) },
            {
                DicomTag.ImageOrientationPatient,
                Ds(o.RowDirection.X), Ds(o.RowDirection.Y), Ds(o.RowDirection.Z),
                Ds(o.ColumnDirection.X), Ds(o.ColumnDirection.Y), Ds(o.ColumnDirection.Z)
            },
            { DicomTag.PixelSpacing, Ds(o.RowSpacing), Ds(o.ColumnSpacing) },
            { DicomTag.SliceThickness, Ds(o.SliceThickness ?? o.SliceSpacing) },
            { DicomTag.RescaleSlope, Ds(slope) },
            { DicomTag.RescaleIntercept, Ds(intercept) },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, PhotometricInterpretation.Monochrome2.Value },
            { DicomTag.Rows, (ushort)o.Rows },
            { DicomTag.Columns, (ushort)o.Columns },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)o.BitsStored },
            { DicomTag.HighBit, (ushort)(o.BitsStored - 1) },
            { DicomTag.PixelRepresentation, (ushort)(o.Signed ? 1 : 0) },
        };
        if (o.Modality == "CT") ds.Add(DicomTag.RescaleType, "HU");
        if (o.Modality == "CT")
        {
            ds.Add(DicomTag.WindowCenter, "40");
            ds.Add(DicomTag.WindowWidth, "400");
        }

        var pixelData = DicomPixelData.Create(ds, true);
        pixelData.AddFrame(new MemoryByteBuffer(pixels));
        return ds;
    }

    /// <summary>Decimal String values are limited to 16 characters; keep as many significant digits as fit.</summary>
    private static string Ds(double v)
    {
        for (var digits = 15; digits >= 6; digits--)
        {
            var s = v.ToString("G" + digits, CultureInfo.InvariantCulture);
            if (s.Length <= 16) return s;
        }
        return v.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes a DICOMDIR that references the given files (paths must be inside <paramref name="root"/>).</summary>
    public static string WriteDicomDir(string root, IEnumerable<string> files)
    {
        var dir = new DicomDirectory();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f).Replace('/', '\\');
            dir.AddFile(DicomFile.Open(f), rel);
        }
        var path = Path.Combine(root, "DICOMDIR");
        dir.Save(path);
        return path;
    }

    /// <summary>Adds PET module attributes for SUV tests. Times are relative to <paramref name="injection"/>.</summary>
    public static Action<DicomDataset, int> PetAttributes(
        DateTime injection, TimeSpan seriesOffset, double doseBq, double? weightKg, string units = "BQML",
        string decayCorrection = "START", double halfLife = 6586.2, string corrected = "ATTN\\DECY",
        Func<int, TimeSpan>? acquisitionOffset = null, bool includeStartDateTime = true)
    {
        return (ds, k) =>
        {
            var series = injection + seriesOffset;
            var acq = injection + (acquisitionOffset?.Invoke(k) ?? seriesOffset);
            ds.AddOrUpdate(DicomTag.SeriesDate, series.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            ds.AddOrUpdate(DicomTag.SeriesTime, series.ToString("HHmmss", CultureInfo.InvariantCulture));
            ds.AddOrUpdate(DicomTag.AcquisitionDate, acq.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            ds.AddOrUpdate(DicomTag.AcquisitionTime, acq.ToString("HHmmss.ffffff", CultureInfo.InvariantCulture));
            ds.AddOrUpdate(DicomTag.Units, units);
            ds.AddOrUpdate(DicomTag.DecayCorrection, decayCorrection);
            ds.AddOrUpdate(DicomTag.CorrectedImage, corrected.Split('\\'));
            if (weightKg is { } w) ds.AddOrUpdate(DicomTag.PatientWeight, Ds(w));
            var rp = new DicomDataset
            {
                { DicomTag.Radiopharmaceutical, "Fluorodeoxyglucose" },
                { DicomTag.RadiopharmaceuticalStartTime, injection.ToString("HHmmss", CultureInfo.InvariantCulture) },
                { DicomTag.RadionuclideTotalDose, Ds(doseBq) },
                { new DicomTag(0x0018, 0x1075), Ds(halfLife) },
            };
            if (includeStartDateTime)
                rp.Add(DicomTag.RadiopharmaceuticalStartDateTime, injection.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            rp.Add(new DicomSequence(DicomTag.RadionuclideCodeSequence, new DicomDataset
            {
                { DicomTag.CodeValue, "C-111A1" },
                { DicomTag.CodingSchemeDesignator, "SRT" },
                { DicomTag.CodeMeaning, "^18^Fluorine" },
            }));
            ds.AddOrUpdate(new DicomSequence(DicomTag.RadiopharmaceuticalInformationSequence, rp));
        };
    }
}
