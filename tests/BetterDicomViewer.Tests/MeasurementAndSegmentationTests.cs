using BetterDicomViewer.Core.Annotations;
using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Export;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Segmentation;
using BetterDicomViewer.Synthetic;

namespace BetterDicomViewer.Tests;

public class MeasurementAndSegmentationTests
{
    private static async Task<ImageVolume> Phantom(TempDir tmp, Func<Vec3, double> value, double spacing = 1.0, int n = 48)
    {
        SyntheticDicom.WriteSeries(tmp.Path, new SeriesOptions
        {
            Columns = n,
            Rows = n,
            Slices = n,
            ColumnSpacing = spacing,
            RowSpacing = spacing,
            SliceSpacing = spacing,
            Origin = new Vec3(-n * spacing / 2, -n * spacing / 2, -n * spacing / 2),
            Value = value,
        });
        var set = Assert.Single(await SeriesAndDecodeTests.AnalyzeFolder(tmp.Path));
        var volume = VolumeLoader.Start(set, 0);
        await volume.Completion;
        return volume;
    }

    private static MeasurementContext Ctx(ImageVolume v) => new()
    {
        Data = v.Data,
        IndexToWorld = v.IndexToWorld,
        PatientSpace = true,
        ValueUnit = v.ValueUnit,
    };

    private static Annotation Ann(AnnotationKind kind, params Vec3[] points) => new()
    {
        Kind = kind,
        PatientSpace = true,
        Points = [.. points],
        PlaneRight = Vec3.UnitX,
        PlaneDown = Vec3.UnitY,
    };

    [Fact]
    public async Task LengthAndAngleUsePatientMillimetres()
    {
        using var tmp = new TempDir();
        using var v = await Phantom(tmp, _ => 0, spacing: 0.7);
        var length = MeasurementCalculator.Compute(Ann(AnnotationKind.Length, new Vec3(0, 0, 0), new Vec3(30, 40, 0)), Ctx(v));
        Assert.Equal(50, length.Length!.Value, 9);
        Assert.Equal("mm", length.LengthUnit);
        var angle = MeasurementCalculator.Compute(Ann(AnnotationKind.Angle, new Vec3(10, 0, 0), Vec3.Zero, new Vec3(0, 10, 0)), Ctx(v));
        Assert.Equal(90, angle.AngleDegrees!.Value, 9);
    }

    [Fact]
    public async Task RoiStatisticsMatchKnownRegion()
    {
        using var tmp = new TempDir();
        // Inside a 10 mm sphere: 100 HU; outside: 0 HU.
        using var v = await Phantom(tmp, p => p.Length <= 10 ? 100 : 0);
        var ellipse = Ann(AnnotationKind.Ellipse, new Vec3(-6, -6, 0), new Vec3(6, 6, 0));
        var r = MeasurementCalculator.Compute(ellipse, Ctx(v));
        Assert.Equal(Math.PI * 36, r.Area!.Value, 6);
        Assert.Equal(100, r.Stats!.Mean, 6);
        Assert.Equal(0, r.Stats.StdDev, 6);
        Assert.InRange(r.Stats.Count, 100, 130);

        var rect = Ann(AnnotationKind.Rectangle, new Vec3(-20, -20, 0), new Vec3(20, 20, 0));
        var rr = MeasurementCalculator.Compute(rect, Ctx(v));
        Assert.Equal(1600, rr.Area!.Value, 6);
        Assert.Equal(0, rr.Stats!.Min);
        Assert.Equal(100, rr.Stats.Max);
    }

    [Fact]
    public void FreehandAreaUsesShoelace()
    {
        var square = new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        Assert.Equal(100, MeasurementCalculator.PolygonArea(square), 9);
        Assert.True(MeasurementCalculator.PointInPolygon(square, 5, 5));
        Assert.False(MeasurementCalculator.PointInPolygon(square, 15, 5));
    }

    [Fact]
    public void UncalibratedFramesMeasureInPixels()
    {
        using var buffer = new VoxelBuffer(100, 100, 1, 1, VoxelType.UInt8);
        var ctx = new MeasurementContext { Data = buffer, IndexToWorld = Affine.Identity, PatientSpace = false, Calibration = new FrameCalibration() };
        var a = Ann(AnnotationKind.Length, new Vec3(0, 0, 0), new Vec3(3, 4, 0));
        a.PatientSpace = false;
        var r = MeasurementCalculator.Compute(a, ctx);
        Assert.Equal(5, r.Length!.Value, 9);
        Assert.Equal("px", r.LengthUnit);
        Assert.NotNull(r.CalibrationNote);
    }

    [Fact]
    public void UltrasoundRegionCalibrationRequiresOneRegion()
    {
        var cal = FrameCalibration.For(new InstanceInfo
        {
            FilePath = "x",
            Modality = "US",
            UltrasoundRegions =
            [
                new UltrasoundRegion { MinX0 = 0, MinY0 = 0, MaxX1 = 49, MaxY1 = 99, PhysicalUnitsX = 3, PhysicalUnitsY = 3, PhysicalDeltaX = 0.01, PhysicalDeltaY = 0.01 },
                new UltrasoundRegion { MinX0 = 50, MinY0 = 0, MaxX1 = 99, MaxY1 = 99, PhysicalUnitsX = 3, PhysicalUnitsY = 3, PhysicalDeltaX = 0.02, PhysicalDeltaY = 0.02 },
            ],
        });
        using var buffer = new VoxelBuffer(100, 100, 1, 1, VoxelType.UInt8);
        var ctx = new MeasurementContext { Data = buffer, IndexToWorld = Affine.Identity, Calibration = cal };
        var inside = Ann(AnnotationKind.Length, new Vec3(10, 10, 0), new Vec3(40, 10, 0));
        inside.PatientSpace = false;
        Assert.Equal(3.0, MeasurementCalculator.Compute(inside, ctx).Length!.Value, 9);
        var across = Ann(AnnotationKind.Length, new Vec3(10, 10, 0), new Vec3(80, 10, 0));
        across.PatientSpace = false;
        Assert.Equal("px", MeasurementCalculator.Compute(across, ctx).LengthUnit);
    }

    [Fact]
    public void ProjectionCalibrationFollowsPs33Rules()
    {
        var detector = FrameCalibration.For(new InstanceInfo { FilePath = "x", Modality = "DX", ImagerPixelSpacing = [0.15, 0.15] });
        Assert.Equal("Imager Pixel Spacing", detector.Source);
        Assert.Contains("magnification", detector.Caveat);
        var corrected = FrameCalibration.For(new InstanceInfo { FilePath = "x", Modality = "DX", ImagerPixelSpacing = [0.15, 0.15], EstimatedRadiographicMagnificationFactor = 1.2 });
        Assert.Equal(0.125, corrected.ColumnSpacingMm!.Value, 9);
    }

    [Fact]
    public async Task SuvRoiStatisticsUseScaleFactor()
    {
        using var tmp = new TempDir();
        var injection = new DateTime(2024, 1, 1, 8, 0, 0);
        var o = new SeriesOptions
        {
            Modality = "PT",
            Columns = 32,
            Rows = 32,
            Slices = 20,
            ColumnSpacing = 2,
            RowSpacing = 2,
            SliceSpacing = 2,
            Origin = new Vec3(-32, -32, -20),
            PerSliceSlope = true,
            Signed = true,
            BitsStored = 16,
            Value = p => p.Length < 8 ? 20000 : 5000,
            Customize = SyntheticDicom.PetAttributes(injection, TimeSpan.FromMinutes(60), 370e6, 75),
        };
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await SeriesAndDecodeTests.AnalyzeFolder(tmp.Path));
        using var v = VolumeLoader.Start(set, 0);
        await v.Completion;
        Assert.True(v.Suv!.IsAvailable, string.Join(" ", v.Suv.Problems));
        var factor = v.Suv.BodyWeightFactor!.Value;
        var ctx = new MeasurementContext { Data = v.Data, IndexToWorld = v.IndexToWorld, PatientSpace = true, ValueUnit = v.ValueUnit, Suv = v.Suv };
        var r = MeasurementCalculator.Compute(Ann(AnnotationKind.Ellipse, new Vec3(-20, -20, 0), new Vec3(20, 20, 0)), ctx);
        Assert.Equal(20000 * factor, r.Stats!.SuvMax!.Value, 2);
        Assert.InRange(r.Stats.SuvPeak!.Value, 5000 * factor, 20000 * factor);
        var probe = MeasurementCalculator.Compute(Ann(AnnotationKind.Probe, Vec3.Zero), ctx);
        Assert.Equal(20000 * factor, probe.ProbeSuv!.Value, 2);
    }

    [Fact]
    public async Task BrushRegionGrowInterpolationAndUndo()
    {
        using var tmp = new TempDir();
        using var v = await Phantom(tmp, p => p.Length <= 12 ? 200 : 0);
        using var map = new LabelMap(v.Nx, v.Ny, v.Nz, v.IndexToWorld, "set", "series");
        var seg = map.AddSegment("Lesion");

        var disk = map.Paint(Vec3.Zero, 5, Vec3.UnitZ, 0.49, seg.Label);
        Assert.NotNull(disk);
        Assert.InRange(disk!.Count, 70, 90);
        map.Apply(disk, undo: true);
        Assert.Equal(0, map.Count(seg.Label));
        map.Apply(disk, undo: false);
        Assert.Equal(disk.Count, map.Count(seg.Label));

        var grow = map.RegionGrow(Vec3.Zero, 100, 300, v.Data, seg.Label, 1_000_000, null, out var truncated);
        Assert.False(truncated);
        var sphereVoxels = map.Count(seg.Label);
        Assert.InRange(sphereVoxels, (long)(4.0 / 3 * Math.PI * 1728 * 0.9), (long)(4.0 / 3 * Math.PI * 1728 * 1.1));
        Assert.NotNull(grow);

        var stats = SegmentStatistics.Compute(map, v, null).Single();
        Assert.Equal(200, stats.Mean!.Value, 6);
        Assert.Equal(sphereVoxels / 1000.0, stats.VolumeMl, 6);

        var other = map.AddSegment("Interpolated");
        map.ClearSegment(seg.Label);
        map.Paint(new Vec3(0, 0, -10), 6, Vec3.UnitZ, 0.49, other.Label);
        map.Paint(new Vec3(0, 0, 10), 6, Vec3.UnitZ, 0.49, other.Label);
        var interp = map.InterpolateSlices(other.Label);
        Assert.NotNull(interp);
        Assert.InRange(interp!.Count, 19 * 90, 19 * 130);
    }

    [Fact]
    public async Task LabelMapRoundTripsThroughNrrdAndWritesNifti()
    {
        using var tmp = new TempDir();
        using var v = await Phantom(tmp, _ => 0, n: 24);
        using var map = new LabelMap(v.Nx, v.Ny, v.Nz, v.IndexToWorld, "set", "series");
        var seg = map.AddSegment("Liver");
        map.Paint(Vec3.Zero, 4, Vec3.Zero, 0, seg.Label);
        var count = map.Count(seg.Label);
        var path = Path.Combine(tmp.Path, "labels.seg.nrrd");
        LabelMapIo.SaveNrrd(map, path);

        using var loaded = new LabelMap(v.Nx, v.Ny, v.Nz, v.IndexToWorld, "set", "series");
        Assert.True(LabelMapIo.TryLoadNrrd(path, loaded));
        Assert.Equal(count, loaded.Count(seg.Label));
        Assert.Equal("Liver", loaded.Segments.Single().Name);
        Assert.Equal(seg.Color, loaded.Segments.Single().Color);

        using var wrongGrid = new LabelMap(v.Nx, v.Ny, v.Nz + 1, v.IndexToWorld, "set", "series");
        Assert.False(LabelMapIo.TryLoadNrrd(path, wrongGrid));

        var nifti = Path.Combine(tmp.Path, "labels.nii.gz");
        LabelMapIo.SaveNifti(map, nifti);
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(nifti), System.IO.Compression.CompressionMode.Decompress);
        var header = new byte[352];
        gz.ReadExactly(header);
        Assert.Equal(348, BitConverter.ToInt32(header, 0));
        Assert.Equal(v.Nx, BitConverter.ToInt16(header, 42));
        // sform row 0 must flip LPS x to RAS: srow_x[3] = -origin.x.
        Assert.Equal(-v.IndexToWorld.Tx, BitConverter.ToSingle(header, 280 + 12), 3);
    }

    [Fact]
    public void MeasurementCsvEscapesAndHasDocumentedColumns()
    {
        using var tmp = new TempDir();
        var a = Ann(AnnotationKind.Length, Vec3.Zero, new Vec3(3, 4, 0));
        a.Label = "L1";
        a.Text = "note, with \"quotes\"";
        var row = MeasurementRow.From(a, new MeasurementResult { Length = 5 }, "CT", 2, "Chest", "Axial");
        var path = Path.Combine(tmp.Path, "m.csv");
        MeasurementExport.WriteCsv(path, [row]);
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("Label,Type,Modality,SeriesNumber", lines[0].TrimStart('﻿'));
        Assert.Contains("\"note, with \"\"quotes\"\"\"", lines[1]);
        Assert.Contains(",5,mm,", lines[1]);
        var json = Path.Combine(tmp.Path, "m.json");
        MeasurementExport.WriteJson(json, [row]);
        Assert.Contains("better-dicom-viewer.measurements", File.ReadAllText(json));
    }

    [Fact]
    public void AnnotationsPersistOutsideSourceFolders()
    {
        using var tmp = new TempDir();
        var store = new AnnotationStore();
        var a = Ann(AnnotationKind.Probe, new Vec3(1, 2, 3));
        a.StudyInstanceUid = "1.2.3." + Guid.NewGuid().ToString("N")[..8].Select(c => (int)c).Sum();
        a.Label = store.NextLabel(a.StudyInstanceUid, a.Kind);
        store.Add(a);
        Assert.StartsWith(BetterDicomViewer.Core.AppPaths.Root, AnnotationStore.PathFor(a.StudyInstanceUid));
        var reloaded = new AnnotationStore().ForStudy(a.StudyInstanceUid);
        Assert.Equal("P1", Assert.Single(reloaded).Label);
        store.Remove(a);
        Assert.Empty(new AnnotationStore().ForStudy(a.StudyInstanceUid));
    }
}
