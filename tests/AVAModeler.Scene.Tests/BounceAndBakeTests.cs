using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Bounced light (global illumination) in renders, and light maps baked through an object's UVs.</summary>
public class BounceAndBakeTests
{
    private static RenderImage Render(Scene scene, RenderSettings? settings = null) =>
        RayTracer.Render(scene, 0, settings, null, TestContext.Current.CancellationToken);

    /// <summary>A white floor beside a red wall, lit by a light overhead, looked at from above the floor.</summary>
    private static Scene RedWall()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0.3, 3, 0));
        cam.SetRotation(0, new Vec3(0, 90, 0));
        scene.AddObject("Floor", Doc(Square(Vec3.Zero, 4, Vec3.UnitY), s => s.Color = new Rgb(255, 255, 255)));
        scene.AddObject("Wall", Doc(Square(new Vec3(-0.4, 1, 0), 4, Vec3.UnitX), s => s.Color = new Rgb(255, 0, 0)));
        var light = scene.AddLight("Light", LightType.Point);
        light.SetPosition(0, new Vec3(0.6, 2.5, 0));
        return scene;
    }

    [Fact]
    public void Bounced_light_tints_a_white_floor_red_beside_a_red_wall_and_off_changes_nothing()
    {
        var scene = RedWall();
        var settings = new RenderSettings { Width = 24, Height = 24 };
        var plain = Render(scene, settings);
        var (r0, g0, _, _) = plain.Pixel(4, 12);
        Assert.Equal(r0, g0);
        // Switched on with no strength, the picture is the same.
        scene.GlobalIllumination = new GlobalIllumination { On = true, Strength = 0 };
        Assert.Equal(plain.Rgba, Render(scene, settings).Rgba);

        scene.GlobalIllumination = new GlobalIllumination { On = true, Samples = 128, Backdrop = false };
        var bounced = Render(scene, settings);
        var (r, g, _, _) = bounced.Pixel(4, 12);
        Assert.True(r > g + 3, $"near the wall the floor is pinker: red {r}, green {g}");
        Assert.True(r >= r0);
        // The same picture on one thread as on many.
        Assert.Equal(bounced.Rgba, Render(scene, new RenderSettings { Width = 24, Height = 24, MaxThreads = 1 }).Rgba);
    }

    [Fact]
    public void The_settings_are_checked_and_kept_in_files_snapshots_and_copies()
    {
        var scene = new Scene { GlobalIllumination = new GlobalIllumination { On = true, Bounces = 2, Samples = 16, Strength = 0.5 } };
        var (back, _) = SceneJson.Read(SceneJson.Write(scene));
        Assert.Equal(scene.GlobalIllumination, back.GlobalIllumination);
        Assert.DoesNotContain("GlobalIllumination", SceneJson.Write(new Scene()));
        var snap = scene.Snapshot();
        scene.GlobalIllumination = new GlobalIllumination();
        snap.Restore();
        Assert.Equal(2, scene.GlobalIllumination.Bounces);
        Assert.Equal(0.5, scene.Copy().GlobalIllumination.Strength);
        Assert.Throws<ArgumentException>(() => new GlobalIllumination { Bounces = 9 }.Checked());
        Assert.Throws<ArgumentException>(() => new GlobalIllumination { Samples = 0 }.Checked());
    }

    /// <summary>A UV-mapped floor (u along X, v along Z) under a point light, with a box hovering over one side of it.</summary>
    private static (Scene, ObjectItem) Floor(bool box)
    {
        var mesh = Square(Vec3.Zero, 2, Vec3.UnitY);
        var uv = mesh.AddUVMap("UV");
        for (int i = 0; i < mesh.Points.Count; i++) uv.Points[i] = new Vec2(mesh.Points[i].X / 2 + 0.5, mesh.Points[i].Z / 2 + 0.5);
        var scene = new Scene { AmbientIntensity = 0.1 };
        var floor = scene.AddObject("Floor", Doc(mesh, s => s.Color = new Rgb(255, 128, 64)));
        var light = scene.AddLight("Light", LightType.Point);
        light.SetPosition(0, new Vec3(0, 1, 0));
        if (box)
        {
            var b = Square(Vec3.Zero, 0.6, -Vec3.UnitY);
            scene.AddObject("Shade", Doc(b)).SetPosition(0, new Vec3(0.5, 0.5, 0));
        }
        return (scene, floor);
    }

    private static (byte R, byte G, byte B) At(LightBakeResult bake, double u, double v)
    {
        int x = Math.Min(bake.Width - 1, (int)(u * bake.Width)), y = Math.Min(bake.Height - 1, (int)((1 - v) * bake.Height));
        int k = (y * bake.Width + x) * 4;
        return (bake.Rgba[k], bake.Rgba[k + 1], bake.Rgba[k + 2]);
    }

    [Fact]
    public void A_light_map_is_brightest_under_the_light_and_dark_in_a_shadow()
    {
        var (scene, floor) = Floor(box: false);
        var bake = RayTracer.BakeLighting(scene, floor, 0, new LightBakeSettings { Size = 64, WithColor = false, Exposure = 0.5 }, TestContext.Current.CancellationToken);
        Assert.Equal(64 * 64, bake.Covered);
        Assert.Equal("UV", bake.UVMap);
        var middle = At(bake, 0.5, 0.5);
        var corner = At(bake, 0.02, 0.02);
        Assert.True(middle.R > corner.R + 20, $"under the light {middle.R}, corner {corner.R}");
        Assert.Equal(middle.R, middle.G);

        var (shaded, shadedFloor) = Floor(box: true);
        var withShadow = RayTracer.BakeLighting(shaded, shadedFloor, 0, new LightBakeSettings { Size = 64, WithColor = false, Exposure = 0.5 }, TestContext.Current.CancellationToken);
        // Under the box's shadow (x 0.7 → u 0.85) it's only the ambient; on the other side it's lit as before.
        var under = At(withShadow, 0.85, 0.5);
        var open = At(withShadow, 0.25, 0.5);
        Assert.True(under.R < open.R / 2, $"in the shadow {under.R}, in the open {open.R}");

        // With colour: the surface's orange, times the light.
        var coloured = RayTracer.BakeLighting(scene, floor, 0, new LightBakeSettings { Size = 32 }, TestContext.Current.CancellationToken);
        var c = At(coloured, 0.5, 0.5);
        Assert.True(c.R > c.G && c.G > c.B, $"{c}");
    }

    [Fact]
    public void Baking_needs_a_uv_map_and_sensible_settings()
    {
        var scene = new Scene();
        var o = scene.AddObject("Plain", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        Assert.Contains("no UV map", Assert.Throws<InvalidOperationException>(() => RayTracer.BakeLighting(scene, o, 0, null, TestContext.Current.CancellationToken)).Message);
        Assert.Throws<ArgumentException>(() => new LightBakeSettings { Size = 3 }.Checked());
    }
}
