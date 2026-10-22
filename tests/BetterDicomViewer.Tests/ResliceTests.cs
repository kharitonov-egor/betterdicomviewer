using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Imaging;
using BetterDicomViewer.Core.Viewing;
using BetterDicomViewer.Imaging;
using BetterDicomViewer.Synthetic;

namespace BetterDicomViewer.Tests;

/// <summary>
/// End-to-end geometry checks through the native VTK reslicer: a volume whose values are a linear function of patient
/// position is resampled on arbitrary planes; trilinear interpolation reproduces linear functions exactly, so every
/// interior output pixel must equal the function evaluated at that pixel's patient coordinate.
/// </summary>
public class ResliceTests
{
    private static double F(Vec3 p) => 2 * p.X - 3 * p.Y + 0.5 * p.Z + 100;

    private static async Task<ImageVolume> LinearVolume(TempDir tmp, SeriesOptions o)
    {
        o.Modality = "PT";
        o.PerSliceSlope = true;
        o.Signed = true;
        o.BitsStored = 16;
        o.Value = F;
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await SeriesAndDecodeTests.AnalyzeFolder(tmp.Path));
        var volume = VolumeLoader.Start(set, 0);
        await volume.Completion;
        return volume;
    }

    public static TheoryData<double, double, double, double, double, double> Planes => new()
    {
        { 1, 0, 0, 0, 1, 0 },
        { 1, 0, 0, 0, 0, -1 },
        { 0, 1, 0, 0, 0, -1 },
        { 0.8, 0.6, 0, 0, 0.2, -0.98 },
        { 0.6, -0.48, 0.64, 0.8, 0.36, -0.48 },
    };

    [Theory]
    [MemberData(nameof(Planes))]
    public async Task ReslicedValuesMatchPatientCoordinates(double rx, double ry, double rz, double dx, double dy, double dz)
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions
        {
            Columns = 48,
            Rows = 40,
            Slices = 30,
            ColumnSpacing = 0.7,
            RowSpacing = 0.9,
            SliceSpacing = 1.5,
            Origin = new Vec3(-15, -20, -25),
            RowDirection = new Vec3(0.98, 0.2, 0).Normalized(),
            ColumnDirection = new Vec3(-0.2, 0.98, 0).Normalized(),
        };
        using var volume = await LinearVolume(tmp, o);
        var g = volume.Source.Geometry!;

        var view = new SliceView { Width = 64, Height = 48, Scale = 0.37, Center = g.Center };
        view.SetPlane(new Vec3(rx, ry, rz), new Vec3(dx, dy, dz));
        var axes = view.ResliceAxes(volume.WorldToIndex);
        var output = new float[view.Width * view.Height];
        using var reslicer = new Reslicer();
        unsafe
        {
            fixed (float* p = output)
                reslicer.Reslice(volume.Data, axes, view.Width, view.Height, Interpolation.Linear, 0, 1, -9999, p, VoxelType.Float32);
        }

        var checkedPixels = 0;
        for (var y = 0; y < view.Height; y++)
        for (var x = 0; x < view.Width; x++)
        {
            var world = view.ScreenToWorld(x + 0.5, y + 0.5);
            var idx = volume.WorldToIndexPoint(world);
            if (idx.X < 0.01 || idx.Y < 0.01 || idx.Z < 0.01 || idx.X > g.Nx - 1.01 || idx.Y > g.Ny - 1.01 || idx.Z > g.Nz - 1.01) continue;
            var expected = F(world);
            // Stored int16 quantization with per-slice slope ~ max/32000 bounds the error.
            Assert.True(Math.Abs(output[y * view.Width + x] - expected) < 0.05, $"pixel ({x},{y}): {output[y * view.Width + x]} vs {expected}");
            checkedPixels++;
        }
        Assert.True(checkedPixels > 500, $"only {checkedPixels} interior pixels checked");
    }

    [Fact]
    public async Task SlabMaximumProjectionTakesMaxAlongNormal()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Columns = 32, Rows = 32, Slices = 20, SliceSpacing = 1, ColumnSpacing = 1, RowSpacing = 1, Origin = new Vec3(0, 0, 0) };
        o.Modality = "PT";
        o.PerSliceSlope = true;
        o.Signed = true;
        o.BitsStored = 16;
        o.Value = p => 10 + p.Z;
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await SeriesAndDecodeTests.AnalyzeFolder(tmp.Path));
        using var volume = VolumeLoader.Start(set, 0);
        await volume.Completion;

        var view = new SliceView { Width = 8, Height = 8, Scale = 1, Center = new Vec3(16, 16, 9), SlabMode = SlabMode.Max, SlabThicknessMm = 5 };
        view.SetPlane(Vec3.UnitX, Vec3.UnitY);
        var renderer = new Reslicer();
        var output = new float[64];
        var normalSign = Math.Sign(view.Normal.Z);
        unsafe
        {
            fixed (float* p = output)
                renderer.Reslice(volume.Data, view.ResliceAxes(volume.WorldToIndex, 1.0), 8, 8, Interpolation.Linear, (int)SlabMode.Max, 5, 0, p, VoxelType.Float32);
        }
        renderer.Dispose();
        Assert.Equal(10 + 9 + 2, output[27], 2);
        Assert.Equal(1, normalSign);
    }
    [Fact]
    public async Task ObliqueSlabIsCentredOnThePlane()
    {
        using var tmp = new TempDir();
        var o = new SeriesOptions { Columns = 40, Rows = 40, Slices = 40, SliceSpacing = 1, ColumnSpacing = 1, RowSpacing = 1, Origin = new Vec3(-20, -20, -20) };
        o.Modality = "PT";
        o.PerSliceSlope = true;
        o.Signed = true;
        o.BitsStored = 16;
        o.Value = p => 100 + p.Z;
        SyntheticDicom.WriteSeries(tmp.Path, o);
        var set = Assert.Single(await SeriesAndDecodeTests.AnalyzeFolder(tmp.Path));
        using var volume = VolumeLoader.Start(set, 0);
        await volume.Completion;

        var view = new SliceView { Width = 9, Height = 9, Scale = 0.5, Center = Vec3.Zero };
        view.SetPlane(new Vec3(1, 0, 0), new Vec3(0, 0.8, -0.6));
        var nz = Math.Abs(view.Normal.Z);
        using var reslicer = new Reslicer();
        var output = new float[81];
        unsafe
        {
            fixed (float* p = output)
                reslicer.Reslice(volume.Data, view.ResliceAxes(volume.WorldToIndex, 1.0), 9, 9, Interpolation.Linear, (int)SlabMode.Max, 5, 0, p, VoxelType.Float32);
        }
        // Centre pixel: plane passes through z = f(centre) with samples at -2..+2 mm along the normal.
        var centre = view.ScreenToWorld(4.5, 4.5);
        Assert.Equal(100 + centre.Z + 2 * nz, output[40], 2);
    }
}
