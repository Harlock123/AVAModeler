using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Light linking: a light lighting only some objects, or all but some; kept in files, copies, snapshots and removals.</summary>
public class LightLinkTests
{
    private static (Scene Scene, ObjectItem Left, ObjectItem Right, LightItem Light) Stage()
    {
        var s = new Scene { AmbientIntensity = 0.05 };
        var cam = s.AddCamera("Camera");
        cam[Channel.PositionZ].SetConstant(-6);
        cam.Width = 64;
        cam.Height = 32;
        var left = s.AddObject("Left", Doc(Sphere(Vec3.Zero, 1, 16, 8)));
        left[Channel.PositionX].SetConstant(-1.5);
        var right = s.AddObject("Right", Doc(Sphere(Vec3.Zero, 1, 16, 8)));
        right[Channel.PositionX].SetConstant(1.5);
        var light = s.AddLight("Key");
        light[Channel.PositionZ].SetConstant(-5);
        light.Shadows = false;
        return (s, left, right, light);
    }

    private static double Brightness(RenderImage img, int x0, int x1)
    {
        double sum = 0;
        int n = 0;
        for (int y = 8; y < 24; y++)
            for (int x = x0; x < x1; x++) { var (r, g, b, _) = img.Pixel(x, y); sum += r + g + b; n++; }
        return sum / n;
    }

    private static RenderImage Render(Scene s) => RayTracer.Render(s, 1, new RenderSettings { SamplesPerPixel = 1 }, null, TestContext.Current.CancellationToken);

    [Fact]
    public void A_linked_light_lights_only_its_objects()
    {
        var (s, left, right, light) = Stage();
        var all = Render(s);
        Assert.Equal(Brightness(all, 10, 22), Brightness(all, 42, 54), 0);
        light.LinkMode = LightLinkMode.Only;
        light.LinkedObjects = [left];
        var only = Render(s);
        Assert.True(Brightness(only, 10, 22) > 3 * Brightness(only, 42, 54), "the right ball is still lit");
        Assert.Equal(Brightness(all, 10, 22), Brightness(only, 10, 22), 0);
        light.LinkMode = LightLinkMode.Except;
        var except = Render(s);
        Assert.True(Brightness(except, 42, 54) > 3 * Brightness(except, 10, 22), "the left ball is still lit");
    }

    [Fact]
    public void Links_are_kept_in_files_copies_and_snapshots_and_dropped_with_their_objects()
    {
        var (s, left, right, light) = Stage();
        light.LinkMode = LightLinkMode.Only;
        light.LinkedObjects = [right];
        var back = SceneJson.Read(SceneJson.Write(s, embedMeshes: true)).Scene;
        var bl = back.Lights.Single();
        Assert.Equal(LightLinkMode.Only, bl.LinkMode);
        Assert.Same(back.Find("Right"), bl.LinkedObjects.Single());
        var copy = s.Copy();
        Assert.Same(copy.Find("Right"), copy.Lights.Single().LinkedObjects.Single());
        var snap = s.Snapshot();
        light.LinkMode = LightLinkMode.All;
        light.LinkedObjects = [];
        snap.Restore();
        Assert.Equal([right], light.LinkedObjects);
        s.Remove(right);
        Assert.Empty(light.LinkedObjects);
        Assert.Equal(LightLinkMode.All, new LightItem("x").LinkMode);
    }
}
