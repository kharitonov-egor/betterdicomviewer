using BetterDicomViewer.Core.Geometry;
using BetterDicomViewer.Core.Viewing;

namespace BetterDicomViewer.Tests;

public class GeometryTests
{
    [Fact]
    public void AffineInverseRoundTrips()
    {
        var a = Affine.FromColumns(new Vec3(0.7, 0.1, 0), new Vec3(-0.1, 0.7, 0.05), new Vec3(0.2, 0, 2.5), new Vec3(-120, 80, 33));
        var p = new Vec3(12.5, -3, 7);
        var back = a.Inverse().TransformPoint(a.TransformPoint(p));
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
        Assert.Equal(p.Z, back.Z, 9);
        var composed = (a * a.Inverse()).TransformPoint(p);
        Assert.Equal(p.Z, composed.Z, 9);
    }

    [Fact]
    public void SliceViewScreenWorldRoundTrip()
    {
        var view = new SliceView { Width = 400, Height = 300, Scale = 0.5, Center = new Vec3(10, 20, 30) };
        view.SetPlane(new Vec3(1, 0, 0), new Vec3(0, 0, -1));
        var world = view.ScreenToWorld(123.25, 77.5);
        var (x, y, depth) = view.WorldToScreen(world);
        Assert.Equal(123.25, x, 9);
        Assert.Equal(77.5, y, 9);
        Assert.Equal(0, depth, 9);
        Assert.Equal(new Vec3(10, 20, 30), view.ScreenToWorld(200, 150));
    }

    [Fact]
    public void ZoomKeepsAnchorFixed()
    {
        var view = new SliceView { Width = 200, Height = 200, Scale = 1, Center = Vec3.Zero };
        var before = view.ScreenToWorld(50, 60);
        view.ZoomAt(50, 60, 2.0);
        var after = view.ScreenToWorld(50, 60);
        Assert.True(Vec3.Distance(before, after) < 1e-9);
        Assert.Equal(0.5, view.Scale, 12);
    }

    [Fact]
    public void ResliceAxesMapPixelCentresToVoxelIndices()
    {
        // 1 mm voxels, origin at (-10,-10,-10): index = world + 10.
        var indexToWorld = Affine.FromColumns(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(-10, -10, -10));
        var view = new SliceView { Width = 20, Height = 20, Scale = 1, Center = Vec3.Zero };
        var axes = view.ResliceAxes(indexToWorld.Inverse());
        // Pixel (0,0) centre is world (-9.5, -9.5, 0) -> index (0.5, 0.5, 10).
        Assert.Equal(0.5, axes[3], 9);
        Assert.Equal(0.5, axes[7], 9);
        Assert.Equal(10, axes[11], 9);
        Assert.Equal(1, axes[0], 9);
        Assert.Equal(1, axes[5], 9);
    }

    [Theory]
    [InlineData(1, 0, 0, "L")]
    [InlineData(-1, 0, 0, "R")]
    [InlineData(0, -1, 0, "A")]
    [InlineData(0, 0, 1, "S")]
    [InlineData(0.8, 0.6, 0, "LP")]
    public void OrientationLetters(double x, double y, double z, string expected) =>
        Assert.Equal(expected, OrientationLabels.ForDirection(new Vec3(x, y, z)));

    [Fact]
    public void AxialRadiologicalViewLabels()
    {
        var (left, top, right, bottom) = OrientationLabels.ForView(Vec3.UnitX, Vec3.UnitY);
        Assert.Equal(("R", "A", "L", "P"), (left, top, right, bottom));
    }

    [Fact]
    public void PatientOrientationParsesProjectionLabels()
    {
        var parsed = OrientationLabels.FromPatientOrientation("P\\F");
        Assert.NotNull(parsed);
        Assert.Equal("P", OrientationLabels.ForDirection(parsed!.Value.Row));
        Assert.Equal("I", OrientationLabels.ForDirection(parsed.Value.Column));
    }

    [Fact]
    public void VoiLinearMatchesDicomDefinition()
    {
        // PS3.3 C.11.2.1.2.1 with c=40, w=400: x <= -160.5 -> 0, x > 239.5 -> 1.
        Assert.Equal(0, Voi.Map(-161, 40, 400, VoiFunction.Linear));
        Assert.Equal(1, Voi.Map(240, 40, 400, VoiFunction.Linear));
        Assert.Equal(0.5, Voi.Map(39.5, 40, 400, VoiFunction.Linear), 9);
        Assert.Equal(0.5, Voi.Map(40, 40, 400, VoiFunction.LinearExact), 9);
    }
}
