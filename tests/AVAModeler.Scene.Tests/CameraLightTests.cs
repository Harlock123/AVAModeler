using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class CameraLightTests
{
    [Fact]
    public void DefaultZoomMatchesLightWavesFieldOfView()
    {
        var cam = new CameraItem("Camera");
        AssertNear(34.708, cam.VerticalFov(0), 1e-3);
        AssertNear(45.24, cam.HorizontalFov(0), 1e-2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.2)]
    [InlineData(10)]
    public void FovConversionsRoundTrip(double zoom)
    {
        AssertNear(zoom, CameraItem.VerticalFovToZoom(CameraItem.ZoomToVerticalFov(zoom)));
        AssertNear(zoom, CameraItem.HorizontalFovToZoom(CameraItem.ZoomToHorizontalFov(zoom, 16.0 / 9), 16.0 / 9));
        AssertNear(90, CameraItem.ZoomToVerticalFov(1));
    }

    [Fact]
    public void FrameAspectIncludesPixelAspect()
    {
        var cam = new CameraItem("Camera") { Width = 720, Height = 480, PixelAspect = 0.9 };
        AssertNear(1.35, cam.FrameAspect);
    }

    [Fact]
    public void SafeAreasAreCentredFractions()
    {
        var cam = new CameraItem("Camera");
        var action = cam.SafeArea(cam.ActionSafe);
        AssertNear(32, action.X);
        AssertNear(24, action.Y);
        AssertNear(576, action.Width);
        AssertNear(432, action.Height);
        var title = cam.SafeArea(cam.TitleSafe);
        AssertNear(64, title.X);
        AssertNear(384, title.Height);
    }

    [Fact]
    public void ViewMatrixPutsWhatTheCameraSeesDownGlMinusZ()
    {
        var scene = new Scene();
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -5));
        var view = cam.ViewMatrix(0);
        AssertNear(new Vec3(0, 0, -5), view.TransformPoint(Vec3.Zero));
        // +X in the world is to the right of the image, +Y up.
        AssertNear(new Vec3(1, 2, -5), view.TransformPoint(new Vec3(1, 2, 0)));

        cam.SetRotation(0, new Vec3(90, 0, 0));
        cam.SetPosition(0, Vec3.Zero);
        AssertNear(new Vec3(0, 0, -10), cam.ViewMatrix(0).TransformPoint(new Vec3(10, 0, 0)));
    }

    [Fact]
    public void ProjectionPutsTheFrameEdgeAtNdcOne()
    {
        var scene = new Scene();
        var cam = scene.AddCamera("Camera");
        double d = 7;
        double tanV = 1 / cam.Zoom.Evaluate(0);
        var clip = cam.ProjectionMatrix(0, 0.1, 100) * cam.ViewMatrix(0);
        var right = clip.TransformPoint(new Vec3(tanV * cam.FrameAspect * d, 0, d));
        AssertNear(1, right.X);
        AssertNear(0, right.Y);
        AssertNear(1, clip.TransformPoint(new Vec3(0, tanV * d, d)).Y);
    }

    [Fact]
    public void ZoomCanBeAnimated()
    {
        var cam = new Scene().AddCamera("Camera");
        cam.Zoom.AddKey(0, 2);
        cam.Zoom.AddKey(10, 4);
        AssertNear(3, cam.Zoom.Evaluate(5));
        Assert.True(cam.VerticalFov(0) > cam.VerticalFov(10));
    }

    [Fact]
    public void LightsShineDownPlusZUnrotated()
    {
        var scene = new Scene();
        var light = scene.AddLight("Light");
        AssertNear(Vec3.UnitZ, light.Direction(0));
        light.SetRotation(0, new Vec3(90, 0, 0));
        AssertNear(Vec3.UnitX, light.Direction(0));
    }

    [Fact]
    public void FalloffReachesZeroAtTheRange()
    {
        var light = new LightItem("Point", LightType.Point) { Range = 10 };
        AssertNear(1, light.Falloff(0));
        AssertNear(0.5, light.Falloff(5));
        AssertNear(0, light.Falloff(20));
        light.Range = 0;
        AssertNear(1, light.Falloff(1000));
        Assert.Equal(1, new LightItem("Sun") { Range = 1 }.Falloff(5));
    }

    [Fact]
    public void SpotConeHasASoftEdge()
    {
        var spot = new LightItem("Spot", LightType.Spot) { ConeAngle = 30, EdgeAngle = 10 };
        Assert.Equal(1, spot.SpotFactor(0));
        Assert.Equal(1, spot.SpotFactor(20));
        AssertNear(0.5, spot.SpotFactor(25));
        Assert.Equal(0, spot.SpotFactor(30));
        Assert.Equal(0, spot.SpotFactor(60));
        Assert.Equal(1, new LightItem("Point", LightType.Point).SpotFactor(90));
    }
}
