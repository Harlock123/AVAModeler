using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Particle emitters and hair on objects: worked out at a frame, kept through copies, snapshots and files, and rendered.</summary>
public class ParticleHairTests
{
    /// <summary>A scene at 30 frames a second with a 1 m square floor facing up at the origin.</summary>
    private static (Scene Scene, ObjectItem Emitter) Emitter(ParticleSettings? particles = null, HairSettings? hair = null)
    {
        var scene = new Scene { FramesPerSecond = 30 };
        var o = scene.AddObject("Emitter", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        o.Particles = particles?.Checked();
        o.Hair = hair?.Checked();
        return (scene, o);
    }

    private static int ParticleCount(SceneFrame f) => f.Objects[0].Mesh.Polygons.Count(p => p.Surface == f.Objects[0].Surfaces.Count - 1) / 8;

    [Fact]
    public void Particles_are_born_at_their_rate_live_their_life_and_are_gone()
    {
        var (scene, _) = Emitter(new ParticleSettings { Rate = 10, StartFrame = 0, EndFrame = 30, Life = 2 });
        // A second in: the ten born each second so far, and the one born on the dot.
        Assert.Equal(11, ParticleCount(scene.Evaluate(30)));
        // Emission stops at frame 30; at 2.5 s those born before 0.5 s have died.
        Assert.Equal(6, ParticleCount(scene.Evaluate(75)));
        Assert.Equal(0, ParticleCount(scene.Evaluate(120)));
        // The emitter is still there (one square), then a surface of its own for the particles, named for it.
        var f = scene.Evaluate(30);
        Assert.Equal(1, f.Objects[0].Mesh.Polygons.Count(p => p.Surface == 0));
        Assert.Equal("Emitter particles", f.Objects[0].Surfaces[^1].Name);
    }

    [Fact]
    public void Each_frame_is_the_same_however_its_reached()
    {
        var (scene, _) = Emitter(new ParticleSettings { Rate = 40, EndFrame = 90, Spread = 60, Drag = 0.5 });
        var a = scene.Evaluate(45).Objects[0].Mesh.Points;
        scene.Evaluate(10);
        var b = scene.Evaluate(45).Objects[0].Mesh.Points;
        var c = scene.Copy().Evaluate(45).Objects[0].Mesh.Points;
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        // Another seed puts them elsewhere.
        var (other, o2) = Emitter(new ParticleSettings { Rate = 40, EndFrame = 90, Spread = 60, Drag = 0.5, Seed = 7 });
        Assert.NotEqual(a, other.Evaluate(45).Objects[0].Mesh.Points);
    }

    [Fact]
    public void They_fall_under_gravity_slow_with_drag_and_stop_on_the_floor()
    {
        // Dropped from the pivot with no speed: after 1 s, ½ g t² below it.
        var drop = new ParticleSettings { From = EmitFrom.Origin, Rate = 1, EndFrame = 0, Life = 20, Speed = 0, Spread = 0, Size = 0.001 };
        var (scene, o) = Emitter(drop);
        o.SetPosition(0, new Vec3(0, 10, 0));
        var p = ParticleSystem.At(drop, 30, 30, o.BaseMesh(), f => o.WorldMatrix(f));
        AssertNear(10 - 0.5 * 9.81, Assert.Single(p).Position.Y, 1e-9);
        // With drag it falls slower; it never passes its terminal speed g / drag.
        double withDrag = ParticleSystem.At(drop with { Drag = 2 }, 30, 30, o.BaseMesh(), f => o.WorldMatrix(f))[0].Position.Y;
        Assert.True(withDrag > 10 - 0.5 * 9.81);
        double late = ParticleSystem.At(drop with { Drag = 2 }, 300, 30, o.BaseMesh(), f => o.WorldMatrix(f))[0].Position.Y;
        double later = ParticleSystem.At(drop with { Drag = 2 }, 301, 30, o.BaseMesh(), f => o.WorldMatrix(f))[0].Position.Y;
        Assert.True((late - later) * 30 <= 9.81 / 2 + 1e-6);
        // A floor at 2 m: it stops there.
        AssertNear(2, ParticleSystem.At(drop with { Floor = 2 }, 90, 30, o.BaseMesh(), f => o.WorldMatrix(f))[0].Position.Y, 1e-6);
        // Leaving at 3 m/s straight up with no gravity: 3 m up after a second.
        var up = drop with { Speed = 3, SpeedJitter = 0, Gravity = 0 };
        AssertNear(13, ParticleSystem.At(up, 30, 30, o.BaseMesh(), f => o.WorldMatrix(f))[0].Position.Y, 1e-9);
    }

    [Fact]
    public void Particles_stay_where_they_were_born_as_the_emitter_moves_on()
    {
        // A still particle born at frame 0 at the origin; the emitter moves to X = 5 by frame 30.
        var still = new ParticleSettings { From = EmitFrom.Origin, Rate = 1, EndFrame = 0, Life = 10, Speed = 0, Gravity = 0, Size = 0.1 };
        var (scene, o) = Emitter(still);
        o.SetPosition(30, new Vec3(5, 0, 0));
        var f = scene.Evaluate(30);
        // In the world it's still at the origin (its diamond round it), though the object is at X = 5.
        var world = f.Objects[0].WorldMesh.Points.Skip(4).ToList();
        AssertNear(Vec3.Zero, world.Aggregate(Vec3.Zero, (a, b) => a + b) / world.Count, 1e-9);
        // Hide the emitter: only the particle's eight faces.
        o.Particles = still with { HideEmitter = true };
        Assert.Equal(8, scene.Evaluate(30).Objects[0].Mesh.Polygons.Count);
    }

    [Fact]
    public void Particles_come_from_the_surface_and_leave_along_its_normal()
    {
        // From the square facing up, straight out at 1 m/s, no gravity: a second later, all 1 m above it, within it.
        var s = new ParticleSettings { Rate = 100, EndFrame = 30, Life = 5, Spread = 0, SpeedJitter = 0, Gravity = 0, Size = 0.001 };
        var (scene, o) = Emitter(s);
        var born = ParticleSystem.At(s, 30, 30, o.BaseMesh(), f => o.WorldMatrix(f)).First();
        AssertNear(1, born.Position.Y, 1e-9);
        Assert.InRange(born.Position.X, -0.5, 0.5);
        Assert.InRange(born.Position.Z, -0.5, 0.5);
        Assert.Throws<ArgumentException>(() => (s with { Life = 0 }).Checked());
        Assert.Throws<ArgumentException>(() => (s with { EndFrame = -1 }).Checked());
    }

    [Fact]
    public void Hair_grows_on_the_surface_at_its_density_and_droops()
    {
        // 1 m² at 500 strands a square metre: 500 strands, each 4 joints of 3 quads, 3 tip triangles.
        var hair = new HairSettings { Density = 500, Length = 0.1, LengthJitter = 0, Segments = 4, Droop = 0, Frizz = 0 };
        var (scene, _) = Emitter(hair: hair);
        var f = scene.Evaluate(0);
        var mesh = f.Objects[0].Mesh;
        int hairSurface = f.Objects[0].Surfaces.Count - 1;
        Assert.Equal("Emitter hair", f.Objects[0].Surfaces[hairSurface].Name);
        Assert.Equal(500 * (3 * 3 + 3), mesh.Polygons.Count(p => p.Surface == hairSurface));
        // Straight up from a floor facing up: tips 0.1 m above it.
        Assert.Equal(0.1, mesh.Points.Max(p => p.Y), 9);
        Assert.All(mesh.Points, p => Assert.InRange(p.Y, -1e-9, 0.1 + 1e-9));
        // Drooping, the tips come lower.
        var (droopy, _) = Emitter(hair: hair with { Droop = 1 });
        Assert.True(droopy.Evaluate(0).Objects[0].Mesh.Points.Max(p => p.Y) < 0.09);
        // Only on a surface it's told to: none on a surface the object hasn't.
        var (none, _) = Emitter(hair: hair with { OnSurface = "Nowhere" });
        Assert.Single(none.Evaluate(0).Objects[0].Mesh.Polygons);
        Assert.Throws<ArgumentException>(() => (hair with { Segments = 0 }).Checked());
    }

    [Fact]
    public void Settings_are_kept_through_copies_snapshots_and_scene_files()
    {
        var (scene, o) = Emitter(new ParticleSettings { Rate = 25, Color = new Rgb(10, 20, 30), Floor = 0 }, new HairSettings { Length = 0.2, Color = new Rgb(1, 2, 3) });
        var copy = (ObjectItem)scene.Copy().Find("Emitter")!;
        Assert.Equal(o.Particles, copy.Particles);
        Assert.Equal(o.Hair, copy.Hair);
        var snap = scene.Snapshot();
        o.Particles = null;
        o.Hair = o.Hair! with { Length = 1 };
        snap.Restore();
        Assert.Equal(25, o.Particles!.Rate);
        Assert.Equal(0.2, o.Hair!.Length);
        string json = SceneJson.Write(scene, embedMeshes: true);
        var back = (ObjectItem)SceneJson.Read(json).Scene.Find("Emitter")!;
        Assert.Equal(o.Particles, back.Particles);
        Assert.Equal(o.Hair, back.Hair);
        // An object with neither writes neither.
        var (plain, p) = Emitter();
        string plainJson = SceneJson.Write(plain, embedMeshes: true);
        Assert.DoesNotContain("particles", plainJson);
        Assert.DoesNotContain("\"hair\"", plainJson);
    }

    [Fact]
    public void Particles_and_hair_show_in_a_render()
    {
        var scene = new Scene { AmbientIntensity = 0.5, BackgroundColor = Vec3.Zero };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 1, -3));
        cam.Width = 48;
        cam.Height = 48;
        scene.AddLight("Light").SetPosition(0, new Vec3(0, 3, -3));
        var o = scene.AddObject("Emitter", Doc(Square(new Vec3(0, -2, 0), 0.2, Vec3.UnitY), s => s.Color = new Rgb(0, 0, 0)));
        var settings = new RenderSettings { SamplesPerPixel = 1 };
        int Lit(RenderImage img)
        {
            int n = 0;
            for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++) if (img.Pixel(x, y).G > 40) n++;
            return n;
        }
        int bare = Lit(RayTracer.Render(scene, 20, settings, null, TestContext.Current.CancellationToken));
        // Rising 3 m/s from 2 m below the camera's line, no gravity: half a second in, they're in front of it.
        o.Particles = new ParticleSettings { Rate = 200, EndFrame = 30, Speed = 3, Spread = 30, Gravity = 0, Size = 0.2, Color = new Rgb(255, 255, 255), Glow = 1 };
        int sparks = Lit(RayTracer.Render(scene, 20, settings, null, TestContext.Current.CancellationToken));
        Assert.True(sparks > bare + 20, $"{sparks} lit pixels against {bare}");
    }
}
