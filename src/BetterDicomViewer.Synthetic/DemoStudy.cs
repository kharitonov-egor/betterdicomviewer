using System.Globalization;
using BetterDicomViewer.Core.Geometry;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace BetterDicomViewer.Synthetic;

/// <summary>
/// Generates a fictitious patient with a CT phantom, a prior CT, a PET phantom with valid SUV metadata and an
/// ultrasound cine whose header band carries fake burned-in text. Used for demos, screenshots and manual testing.
/// </summary>
public static class DemoStudy
{
    public const string PatientName = "DEMO^SYNTHETIC";
    public const string PatientId = "SYN-2024-001";
    private const int Version = 4;

    public static void EnsureGenerated(string folder)
    {
        var marker = Path.Combine(folder, "GENERATED.txt");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == Version.ToString(CultureInfo.InvariantCulture)) return;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        Generate(folder);
        File.WriteAllText(marker, Version.ToString(CultureInfo.InvariantCulture));
    }

    public static void Generate(string folder)
    {
        var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var forUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var priorStudyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var priorForUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        SyntheticDicom.WriteSeries(Path.Combine(folder, "CT"), new SeriesOptions
        {
            Modality = "CT",
            Columns = 384,
            Rows = 384,
            Slices = 110,
            ColumnSpacing = 0.9,
            RowSpacing = 0.9,
            SliceSpacing = 3,
            Origin = new Vec3(-172.35, -172.35, -330),
            Value = p => CtValue(p, lesionRadius: 16),
            BitsStored = 12,
            RescaleIntercept = -1024,
            TransferSyntax = DicomTransferSyntax.JPEGProcess14SV1,
            PatientName = PatientName,
            PatientId = PatientId,
            StudyInstanceUid = studyUid,
            FrameOfReferenceUid = forUid,
            StudyDescription = "Synthetic PET/CT chest abdomen",
            SeriesDescription = "CT 3 mm (synthetic phantom)",
            SeriesNumber = 2,
            StudyDate = "20240305",
            StudyTime = "100000",
            ReverseInstanceNumbers = true,
        });

        var injection = new DateTime(2024, 3, 5, 9, 0, 0);
        const double weight = 75;
        const double dose = 370e6;
        var decayedDose = dose * Math.Pow(2, -3600 / 6586.2);
        var bqPerSuv = decayedDose / (weight * 1000);
        SyntheticDicom.WriteSeries(Path.Combine(folder, "PET"), new SeriesOptions
        {
            Modality = "PT",
            Columns = 128,
            Rows = 128,
            Slices = 110,
            ColumnSpacing = 2.7,
            RowSpacing = 2.7,
            SliceSpacing = 3,
            Origin = new Vec3(-171.45, -171.45, -330),
            Value = p => PetSuv(p) * bqPerSuv,
            PerSliceSlope = true,
            Signed = true,
            BitsStored = 16,
            PatientName = PatientName,
            PatientId = PatientId,
            StudyInstanceUid = studyUid,
            FrameOfReferenceUid = forUid,
            StudyDescription = "Synthetic PET/CT chest abdomen",
            SeriesDescription = "PET AC (synthetic, SUV metadata)",
            SeriesNumber = 3,
            StudyDate = "20240305",
            StudyTime = "100000",
            Customize = (ds, k) =>
            {
                SyntheticDicom.PetAttributes(injection, TimeSpan.FromMinutes(60), dose, weight)(ds, k);
                ds.AddOrUpdate(DicomTag.PatientSize, "1.78");
                ds.AddOrUpdate(DicomTag.PatientSex, "M");
            },
        });

        SyntheticDicom.WriteSeries(Path.Combine(folder, "PRIOR"), new SeriesOptions
        {
            Modality = "CT",
            Columns = 384,
            Rows = 384,
            Slices = 110,
            ColumnSpacing = 0.9,
            RowSpacing = 0.9,
            SliceSpacing = 3,
            Origin = new Vec3(-172.35, -172.35, -318),
            Value = p => CtValue(p + new Vec3(0, 0, 0), lesionRadius: 9),
            BitsStored = 12,
            RescaleIntercept = -1024,
            PatientName = PatientName,
            PatientId = PatientId,
            StudyInstanceUid = priorStudyUid,
            FrameOfReferenceUid = priorForUid,
            StudyDescription = "Synthetic CT chest abdomen (prior)",
            SeriesDescription = "CT 3 mm prior (synthetic phantom)",
            SeriesNumber = 2,
            StudyDate = "20230301",
            StudyTime = "093000",
        });

        WriteUltrasoundCine(Path.Combine(folder, "US"), DicomUIDGenerator.GenerateDerivedFromUUID().UID);
    }

    private static double Ellipsoid(Vec3 p, Vec3 c, Vec3 r)
    {
        var d = p - c;
        return d.X * d.X / (r.X * r.X) + d.Y * d.Y / (r.Y * r.Y) + d.Z * d.Z / (r.Z * r.Z);
    }

    private static double Noise(Vec3 p, double amplitude)
    {
        var h = (int)(p.X * 7.1) * 73856093 ^ (int)(p.Y * 7.3) * 19349663 ^ (int)(p.Z * 3.7) * 83492791;
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h & 0xFFFF) / 65535.0 - 0.5) * 2 * amplitude;
    }

    /// <summary>Hounsfield units of the torso phantom. Patient LPS coordinates in mm, z from -330 (pelvis) to 0 (lung apex).</summary>
    public static double CtValue(Vec3 p, double lesionRadius)
    {
        var bodyR = (p.X / 165) * (p.X / 165) + (p.Y / 115) * (p.Y / 115);
        if (p.Y > 128 && p.Y < 140 && Math.Abs(p.X) < 175) return 150 + Noise(p, 15);
        if (bodyR > 1) return -1000 + Noise(p, 5);
        var hu = bodyR > 0.85 ? -95.0 : 45.0;

        var ribZone = p.Z > -180 && p.Z < -5;
        if (ribZone && bodyR > 0.66 && bodyR < 0.74 && ((p.Z % 24 + 24) % 24) < 9 && p.Y < 70) hu = 750;

        if (p.Z > -170)
        {
            foreach (var side in new[] { -1, 1 })
            {
                if (Ellipsoid(p, new Vec3(side * 72, -5, -70), new Vec3(52, 70, 100)) < 1)
                {
                    hu = -840;
                    var vx = p.X - side * (50 + 10 * Math.Sin(p.Z / 30));
                    var vy = p.Y + 10;
                    if (vx * vx + vy * vy < 16) hu = 40;
                    if (Ellipsoid(p, new Vec3(side * 80, -30, -95), new Vec3(6, 6, 6)) < 1 && side > 0) hu = 30;
                }
            }
            if (Ellipsoid(p, new Vec3(-12, -25, -125), new Vec3(48, 42, 50)) < 1)
            {
                hu = 50;
                if (Ellipsoid(p, new Vec3(-12, -25, -125), new Vec3(26, 24, 30)) < 1) hu = 260;
            }
        }

        if (Ellipsoid(p, new Vec3(-65, 10, -205), new Vec3(78, 70, 62)) < 1)
        {
            hu = 62;
            if (Ellipsoid(p, new Vec3(-75, -10, -200), new Vec3(lesionRadius, lesionRadius, lesionRadius)) < 1) hu = 18;
        }
        if (Ellipsoid(p, new Vec3(70, 5, -215), new Vec3(40, 50, 45)) < 1) hu = 48;
        foreach (var side in new[] { -1, 1 })
            if (Ellipsoid(p, new Vec3(side * 62, 52, -255), new Vec3(26, 22, 48)) < 1) hu = 160;
        if (p.Z < -270 && Ellipsoid(p, new Vec3(0, -20, -310), new Vec3(45, 35, 30)) < 1) hu = 8;

        var ax = p.X - 18;
        var ay = p.Y - 42;
        if (ax * ax + ay * ay < 13 * 13 && p.Z > -290 && p.Z < -60) hu = 280;

        var sx = p.X;
        var sy = p.Y - 75;
        var r2 = sx * sx + sy * sy;
        var level = ((p.Z % 30) + 30) % 30;
        if (r2 < 19 * 19 && level > 3)
        {
            hu = r2 > 15 * 15 ? 1100 : 380;
        }
        if (sx * sx + (p.Y - 100) * (p.Y - 100) < 8 * 8) hu = 25;
        if (sx * sx + (p.Y - 100) * (p.Y - 100) < 12 * 12 && hu > 300 && p.Y > 92) hu = 900;
        return hu + Noise(p, 12);
    }

    /// <summary>Activity of the PET phantom in SUV units.</summary>
    public static double PetSuv(Vec3 p)
    {
        var bodyR = (p.X / 165) * (p.X / 165) + (p.Y / 115) * (p.Y / 115);
        if (bodyR > 1) return 0;
        static double Smooth(double e, double inside, double outside) => e < 0.8 ? inside : e > 1.2 ? outside : outside + (inside - outside) * (1.2 - e) / 0.4;
        var suv = 0.9;
        if (p.Z > -170)
        {
            foreach (var side in new[] { -1, 1 })
                suv = Math.Min(suv, Smooth(Ellipsoid(p, new Vec3(side * 72, -5, -70), new Vec3(52, 70, 100)), 0.35, suv));
            suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(-12, -25, -125), new Vec3(42, 38, 45)), 3.5, suv));
            suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(80, -30, -95), new Vec3(7, 7, 7)), 7.5, suv));
        }
        suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(-65, 10, -205), new Vec3(78, 70, 62)), 2.1, suv));
        suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(-75, -10, -200), new Vec3(16, 16, 16)), 9.0, suv));
        foreach (var side in new[] { -1, 1 })
            suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(side * 62, 52, -255), new Vec3(26, 22, 48)), 4.0, suv));
        if (p.Z < -270) suv = Math.Max(suv, Smooth(Ellipsoid(p, new Vec3(0, -20, -310), new Vec3(45, 35, 30)), 18, suv));
        return Math.Max(0, suv + Noise(p, 0.12));
    }

    private static void WriteUltrasoundCine(string folder, string studyUid)
    {
        Directory.CreateDirectory(folder);
        const int w = 480;
        const int h = 360;
        const int frames = 40;
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.UltrasoundMultiFrameImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, PatientName },
            { DicomTag.PatientID, PatientId },
            { DicomTag.PatientBirthDate, "19700101" },
            { DicomTag.PatientSex, "M" },
            { DicomTag.StudyDate, "20240210" },
            { DicomTag.StudyTime, "140000" },
            { DicomTag.StudyDescription, "Synthetic ultrasound" },
            { DicomTag.SeriesDescription, "US cine with burned-in header (synthetic)" },
            { DicomTag.Modality, "US" },
            { DicomTag.SeriesNumber, "1" },
            { DicomTag.InstanceNumber, "1" },
            { DicomTag.Rows, (ushort)h },
            { DicomTag.Columns, (ushort)w },
            { DicomTag.SamplesPerPixel, (ushort)3 },
            { DicomTag.PhotometricInterpretation, "RGB" },
            { DicomTag.PlanarConfiguration, (ushort)0 },
            { DicomTag.BitsAllocated, (ushort)8 },
            { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.NumberOfFrames, frames.ToString(CultureInfo.InvariantCulture) },
            { DicomTag.FrameTime, "50" },
            { DicomTag.FrameIncrementPointer, DicomTag.FrameTime },
        };
        ds.Add(new DicomSequence(DicomTag.SequenceOfUltrasoundRegions, new DicomDataset
        {
            { DicomTag.RegionSpatialFormat, (ushort)1 },
            { DicomTag.RegionDataType, (ushort)1 },
            { DicomTag.RegionFlags, 0u },
            { DicomTag.RegionLocationMinX0, 40u },
            { DicomTag.RegionLocationMinY0, 40u },
            { DicomTag.RegionLocationMaxX1, 440u },
            { DicomTag.RegionLocationMaxY1, 340u },
            { DicomTag.PhysicalUnitsXDirection, (ushort)3 },
            { DicomTag.PhysicalUnitsYDirection, (ushort)3 },
            { DicomTag.PhysicalDeltaX, 0.012 },
            { DicomTag.PhysicalDeltaY, 0.012 },
        }));
        var pd = DicomPixelData.Create(ds, true);
        var rng = new Random(7);
        for (var f = 0; f < frames; f++)
        {
            var px = new byte[w * h * 3];
            var phase = 2 * Math.PI * f / frames;
            for (var y = 40; y < 340; y++)
            for (var x = 40; x < 440; x++)
            {
                var cx = (x - 240) / 120.0;
                var cy = (y - 190 - 10 * Math.Sin(phase)) / (70.0 + 8 * Math.Cos(phase));
                var inside = cx * cx + cy * cy;
                var depth = (y - 40) / 300.0;
                double v = 70 + rng.Next(0, 60) - depth * 40;
                if (inside < 1) v = 25 + rng.Next(0, 30);
                if (Math.Abs(inside - 1) < 0.08) v = 200 + rng.Next(0, 40);
                var b = (byte)Math.Clamp(v, 0, 255);
                var o = (y * w + x) * 3;
                px[o] = px[o + 1] = px[o + 2] = b;
            }
            PixelFont.Draw(px, w, h, 8, 8, "DEMO^SYNTHETIC  ID SYN-2024-001  1970-01-01", 2, 230);
            PixelFont.Draw(px, w, h, 8, 346, "LT TRV SYNTHETIC", 1, 200);
            pd.AddFrame(new MemoryByteBuffer(px));
        }
        new DicomFile(ds).Save(Path.Combine(folder, "US000001"));
    }
}

/// <summary>Minimal 5x7 bitmap font for synthetic burned-in text.</summary>
internal static class PixelFont
{
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['B'] = ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
        ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
        ['D'] = ["11100", "10010", "10001", "10001", "10001", "10010", "11100"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
        ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01111"],
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['I'] = ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
        ['N'] = ["10001", "10001", "11001", "10101", "10011", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        ['^'] = ["00100", "01010", "10001", "00000", "00000", "00000", "00000"],
        ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
        [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
    };

    public static void Draw(byte[] rgb, int w, int h, int x0, int y0, string text, int scale, byte value)
    {
        var x = x0;
        foreach (var ch in text.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(ch, out var g))
            {
                for (var row = 0; row < 7; row++)
                for (var col = 0; col < 5; col++)
                {
                    if (g[row][col] != '1') continue;
                    for (var sy = 0; sy < scale; sy++)
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var px = x + col * scale + sx;
                        var py = y0 + row * scale + sy;
                        if (px < 0 || py < 0 || px >= w || py >= h) continue;
                        var o = (py * w + px) * 3;
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = value;
                    }
                }
            }
            x += 6 * scale;
        }
    }
}
