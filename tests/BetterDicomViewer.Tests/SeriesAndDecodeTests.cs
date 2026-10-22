using BetterDicomViewer.Core.Dicom;
using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Library;
using BetterDicomViewer.Synthetic;
using FellowOakDicom;

namespace BetterDicomViewer.Tests;

public class SeriesAndDecodeTests
{
    internal static async Task<List<DisplaySet>> AnalyzeFolder(string folder)
    {
        var lib = new DicomLibrary();
        await LibraryScanner.ScanAsync(lib, folder, null, new ScanOptions { UseCache = false }, TestContext.Current.CancellationToken);
        var series = lib.Snapshot().SelectMany(s => s.Series).ToList();
        return series.SelectMany(s => SeriesAnalyzer.Analyze(s.Key, s.Instances)).ToList();
    }

    internal static double Hu(Vec3 p) => Math.Round(p.X * 3 + p.Y * 2 - p.Z * 5);

    [Fact]
    public async Task SortsSlicesByPositionNotFileNameOrInstanceNumber()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Slices = 12, ReverseInstanceNumbers = true, ShuffleFileNames = true, Value = Hu };
        SyntheticDicom.WriteSeries(tmp.Path, o);

        var sets = await AnalyzeFolder(tmp.Path);
        var vol = Assert.Single(sets);
        Assert.True(vol.IsVolume);
        var g = vol.Geometry!;
        Assert.Equal((64, 64, 12), (g.Nx, g.Ny, g.Nz));
        Assert.Equal(2.5, g.Spacing.Z, 6);
        Assert.False(g.IsResampled);
        Assert.Equal(-1, g.AcquisitionDirection);
        var z = vol.Frames.Select(f => f.Instance.ImagePositionPatient![2]).ToList();
        Assert.Equal(z.OrderBy(v => v), z);

        // Index (i, j, k) maps to the exact patient position of that voxel.
        var p = g.IndexToPatient.TransformPoint(new Vec3(5, 7, 3));
        var expected = SyntheticDicom.PatientPoint(o, 5, 7, 3);
        Assert.True(Vec3.Distance(p, expected) < 1e-6);
    }

    [Fact]
    public async Task UnevenSpacingIsResampledWithWarningAndNativeSet()
    {
        using var tmp = new TempDir();
        var gaps = new double[] { 2, 2, 2, 2, 4, 2, 2, 2, 2 };
        SyntheticDicom.WriteSeries(tmp.Path, new SeriesOptions { Slices = 10, SliceGaps = gaps, Value = Hu });
        var sets = await AnalyzeFolder(tmp.Path);
        var vol = sets.Single(s => s.IsVolume);
        Assert.True(vol.Geometry!.IsResampled);
        Assert.Equal(2, vol.Geometry.Spacing.Z, 6);
        Assert.Equal(11, vol.Geometry.Nz);
        Assert.Contains(vol.Warnings, w => w.Contains("Uneven slice spacing"));
        Assert.Contains(sets, s => s.Kind == DisplaySetKind.Frames && s.Label.StartsWith("Native slices"));
    }

    [Fact]
    public async Task GantryTiltIsKeptAsShearedGeometry()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Slices = 8, ShearPerSlice = new Vec3(0, 0.4, 0), Value = Hu };
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var vol = Assert.Single(await AnalyzeFolder(tmp.Path));
        Assert.True(vol.Geometry!.HasGantryTilt);
        var p = vol.Geometry.IndexToPatient.TransformPoint(new Vec3(3, 4, 6));
        Assert.True(Vec3.Distance(p, SyntheticDicom.PatientPoint(o, 3, 4, 6)) < 1e-6);
    }

    [Fact]
    public async Task LocalizerIsSeparatedFromVolume()
    {
        using var tmp = new TempDir();
        var series = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        SyntheticDicom.WriteSeries(tmp.Sub("axial"), new SeriesOptions { Slices = 6, SeriesInstanceUid = series, Value = Hu });
        SyntheticDicom.WriteSeries(tmp.Sub("loc"), new SeriesOptions
        {
            Slices = 1,
            SeriesInstanceUid = series,
            Rows = 80,
            RowDirection = Vec3.UnitX,
            ColumnDirection = -Vec3.UnitZ,
            Customize = (ds, _) => ds.AddOrUpdate(DicomTag.ImageType, "ORIGINAL", "PRIMARY", "LOCALIZER"),
        });
        var sets = await AnalyzeFolder(tmp.Path);
        Assert.Equal(2, sets.Count);
        Assert.True(sets[0].IsVolume);
        Assert.Equal(6, sets[0].SliceCount);
        Assert.Equal("Localizer", sets[1].Label);
    }

    [Fact]
    public async Task SparseStackIsNotTreatedAsVolume()
    {
        using var tmp = new TempDir();
        SyntheticDicom.WriteSeries(tmp.Path, new SeriesOptions { Slices = 3, SliceSpacing = 150, SliceThickness = 5 });
        var set = Assert.Single(await AnalyzeFolder(tmp.Path));
        Assert.Equal(DisplaySetKind.Frames, set.Kind);
    }

    public static TheoryData<string> LosslessSyntaxes => new()
    {
        DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID,
        DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID,
        DicomTransferSyntax.RLELossless.UID.UID,
        DicomTransferSyntax.JPEGProcess14SV1.UID.UID,
        DicomTransferSyntax.JPEGLSLossless.UID.UID,
        DicomTransferSyntax.JPEG2000Lossless.UID.UID,
    };

    [Theory]
    [MemberData(nameof(LosslessSyntaxes))]
    public async Task LosslessTransferSyntaxesDecodeExactly(string syntaxUid)
    {
        using var tmp = new TempDir();
        var syntax = DicomTransferSyntax.Lookup(DicomUID.Parse(syntaxUid));
        var o = new SeriesOptions { Slices = 4, Columns = 40, Rows = 36, TransferSyntax = syntax, Value = Hu };
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await AnalyzeFolder(tmp.Path));
        Assert.Equal(syntaxUid, set.Frames[0].Instance.TransferSyntaxUid);

        using var volume = VolumeLoader.Start(set, 0, parallelism: 2);
        await volume.Completion;
        Assert.Null(volume.LoadError);
        Assert.Equal(VoxelType.Int16, volume.Data.Type);
        for (var k = 0; k < 4; k++)
        for (var j = 0; j < 36; j += 5)
        for (var i = 0; i < 40; i += 7)
        {
            var expected = Math.Clamp(Hu(SyntheticDicom.PatientPoint(o, i, j, k)), -1024, 4095 - 1024);
            Assert.Equal(expected, volume.Data.GetValue(i, j, k));
        }
    }

    [Fact]
    public async Task SignedPixelsWithReducedBitsStoredAreSignExtended()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Slices = 3, Signed = true, BitsStored = 12, RescaleIntercept = 0, Value = p => p.X * 20 };
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await AnalyzeFolder(tmp.Path));
        using var volume = VolumeLoader.Start(set, 0, parallelism: 1);
        await volume.Completion;
        Assert.Equal(Math.Round(SyntheticDicom.PatientPoint(o, 0, 0, 0).X * 20), volume.Data.GetValue(0, 0, 0));
        Assert.True(volume.Data.GetValue(0, 0, 0) < 0);
    }

    [Fact]
    public async Task PerSliceRescaleUsesFloatStorage()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Modality = "PT", Slices = 5, PerSliceSlope = true, BitsStored = 16, Signed = true, Value = p => 1000 + p.Z * 40 + p.X };
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await AnalyzeFolder(tmp.Path));
        using var volume = VolumeLoader.Start(set, 2);
        await volume.Completion;
        Assert.Equal(VoxelType.Float32, volume.Data.Type);
        var expected = o.Value(SyntheticDicom.PatientPoint(o, 10, 10, 4));
        Assert.Equal(expected, volume.Data.GetValue(10, 10, 4), 0);
    }

    [Theory]
    [InlineData(0x0FFF, 12, 11, true, -1)]
    [InlineData(0x0800, 12, 11, true, -2048)]
    [InlineData(0x07FF, 12, 11, true, 2047)]
    [InlineData(0xF123, 12, 11, false, 0x123)]
    [InlineData(0x1230, 12, 15, false, 0x123)]
    public void BitExtraction(int raw, int bitsStored, int highBit, bool signed, int expected) =>
        Assert.Equal(expected, FrameDecoder.ExtractBits(raw, bitsStored, highBit, signed));

    [Fact]
    public void YbrFullConversionMatchesReference()
    {
        Span<byte> rgb = stackalloc byte[3];
        FrameDecoder.YbrToRgb(128, 128, 128, rgb);
        Assert.Equal(new byte[] { 128, 128, 128 }, rgb.ToArray());
        FrameDecoder.YbrToRgb(76, 85, 255, rgb);
        Assert.InRange(rgb[0], 250, 255);
        Assert.InRange(rgb[1], 0, 4);
        Assert.InRange(rgb[2], 0, 4);
    }
}
