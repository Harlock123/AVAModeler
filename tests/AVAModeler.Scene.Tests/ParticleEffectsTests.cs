using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>
/// Particles' looks (blobs, puffs, sprites, copies of an object) and collisions, hair's comb and swing, and cloth and
/// soft bodies.
/// </summary>
public class ParticleEffectsTests
{
    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    /// <summary>One particle dropped from 2 m with no speed, once, living 20 s.</summary>
    private static readonly ParticleSettings Drop = new() { From = EmitFrom.Origin, Rate = 1, EndFrame = 0, Life = 20, Speed = 0, Spread = 0, Size = 0.01 };

    /// <summary>A scene at 30 frames a second: an emitter 2 m up and a 2 m platform facing up at the origin.</summary>
    private static (Scene Scene, ObjectItem Emitter, ObjectItem Platform) Dropping(ParticleSettings s)
    {
        var scene = new Scene { FramesPerSecond = 30 };
        var o = scene.AddObject("Emitter", Doc(Square(Vec3.Zero, 0.1, Vec3.UnitY)));
        o.SetPosition(0, new Vec3(0, 2, 0));
        o.Particles = s.Checked();
        var platform = scene.AddObject("Platform", Doc(Square(Vec3.Zero, 2, Vec3.UnitY)));
        return (scene, o, platform);
    }

    private static List<ParticleSystem.Particle> Alive(Scene scene, ObjectItem o, double frame) =>
        ParticleSystem.At(o.Particles!, frame, scene.FramesPerSecond, o.BaseMesh(), f => o.WorldMatrix(f),
            o.Particles!.Collide ? Colliders.Of(scene, [o], frame, ParticleSystem.StepOf(o.Particles!, scene.FramesPerSecond)) : null);

    [Fact]
    public void Particles_stick_to_bounce_off_or_die_on_an_object()
    {
        var (scene, o, _) = Dropping(Drop with { Collide = true });
        // Stuck on the platform, just above it.
        AssertNear(new Vec3(0, 0, 0), Assert.Single(Alive(scene, o, 60)).Position, 1e-3);
        // Bouncing back with all its speed: up near 2 m again at twice the time it took to fall.
        o.Particles = Drop with { Collide = true, OnHit = ParticleHit.Bounce, Bounce = 1, Friction = 0 };
        double fall = Math.Sqrt(2 * 2 / 9.81);
        AssertNear(2, Alive(scene, o, 2 * fall * 30).Single().Position.Y, 0.05);
        // Losing half its speed: it rises a quarter of the drop.
        o.Particles = Drop with { Collide = true, OnHit = ParticleHit.Bounce, Bounce = 0.5, Friction = 0 };
        AssertNear(0.5, Alive(scene, o, 1.5 * fall * 30).Single().Position.Y, 0.03);
        // Gone on a hit.
        o.Particles = Drop with { Collide = true, OnHit = ParticleHit.Die };
        Assert.Empty(Alive(scene, o, 60));
        // Without collisions it falls straight through.
        o.Particles = Drop;
        Assert.True(Alive(scene, o, 60).Single().Position.Y < -10);
    }

    [Fact]
    public void A_stuck_particle_rides_along_with_the_object_it_hit()
    {
        var (scene, o, platform) = Dropping(Drop with { Collide = true });
        platform.SetPosition(30, Vec3.Zero);
        platform.SetPosition(90, new Vec3(1, 0, 0));
        // It lands about frame 19 (the platform easing back before it sets off); by frame 90 the platform has carried it.
        double landed = Math.Sqrt(2 * 2 / 9.81) * 30;
        var carried = new Vec3(1 - platform.WorldMatrix(landed).TransformPoint(Vec3.Zero).X, 0, 0);
        AssertNear(carried, Alive(scene, o, 90).Single().Position, 2e-3);
        // Evaluated through the scene, the same (as a diamond on the particles' surface).
        o.Particles = o.Particles! with { Size = 0.001 };
        var f = scene.Evaluate(90).Objects.First(x => x.Item == o);
        var world = f.World;
        var points = f.Mesh.Polygons.Where(p => p.Surface == f.Surfaces.Count - 1).SelectMany(p => p.Points).Select(i => world.TransformPoint(f.Mesh.Points[i])).ToList();
        AssertNear(carried, points.Aggregate(Vec3.Zero, (a, b) => a + b) / points.Count, 2e-3);
    }

    [Fact]
    public void Particles_bounce_on_the_floor_too_and_stepped_frames_agree_however_reached()
    {
        var s = Drop with { Floor = 0, OnHit = ParticleHit.Bounce, Bounce = 1, Friction = 0 };
        var (scene, o, platform) = Dropping(s);
        scene.Remove(platform);
        double fall = Math.Sqrt(2 * 2 / 9.81);
        AssertNear(2, ParticleSystem.At(s, 2 * fall * 30, 30, o.BaseMesh(), f => o.WorldMatrix(f)).Single().Position.Y, 0.05);
        // Many particles, spread wide, bouncing: the same at frame 50 straight away, after another frame, and in a copy.
        o.Particles = s with { Rate = 200, EndFrame = 60, Speed = 2, Spread = 80 };
        var a = scene.Evaluate(50).Objects[0].Mesh.Points;
        scene.Evaluate(20);
        Assert.Equal(a, scene.Evaluate(50).Objects[0].Mesh.Points);
        Assert.Equal(a, scene.Copy().Evaluate(50).Objects[0].Mesh.Points);
    }

    /// <summary>Which faces join which: the number of separate pieces.</summary>
    private static int Pieces(Mesh mesh, IEnumerable<int> faces)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            while (parent.TryGetValue(x, out int p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p);
            return x;
        }
        foreach (int f in faces)
        {
            var pts = mesh.Polygons[f].Points;
            foreach (int p in pts) parent.TryAdd(p, p);
            for (int i = 1; i < pts.Count; i++) parent[Find(pts[i])] = Find(pts[0]);
        }
        return parent.Keys.Select(Find).Distinct().Count();
    }

    [Fact]
    public void Blobs_are_balls_alone_and_melt_together_when_close()
    {
        static ParticleSystem.Particle At(Vec3 p) => new(p, 0.2, 0);
        var mesh = new Mesh();
        int made = ParticleLooks.AddBlobs(mesh, [At(Vec3.Zero)], Mat4.Identity, 0);
        Assert.True(made > 50);
        // A lone blob is a ball of the particle's size, faces looking out.
        foreach (var p in mesh.Points) AssertNear(0.1, p.Length, 0.012);
        foreach (var poly in mesh.Polygons) Assert.True(Vec3.Dot(mesh.Normal(poly), mesh.Centroid(poly)) > 0);
        // Closed: every edge between two faces, once each way.
        var edges = new Dictionary<(int, int), int>();
        foreach (var poly in mesh.Polygons)
            for (int i = 0; i < poly.Count; i++)
            {
                var e = (poly.Points[i], poly.Points[(i + 1) % poly.Count]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        Assert.All(edges, kv => Assert.Equal(1, kv.Value));
        Assert.All(edges.Keys, e => Assert.True(edges.ContainsKey((e.Item2, e.Item1))));
        // Two close together are one piece; two far apart, two.
        var close = new Mesh();
        ParticleLooks.AddBlobs(close, [At(Vec3.Zero), At(new Vec3(0.15, 0, 0))], Mat4.Identity, 0);
        Assert.Equal(1, Pieces(close, Enumerable.Range(0, close.Polygons.Count)));
        var apart = new Mesh();
        ParticleLooks.AddBlobs(apart, [At(Vec3.Zero), At(new Vec3(1, 0, 0))], Mat4.Identity, 0);
        Assert.Equal(2, Pieces(apart, Enumerable.Range(0, apart.Polygons.Count)));
    }

    [Fact]
    public void Particles_can_carry_copies_of_an_object_sized_turned_and_aligned()
    {
        var leaf = Square(Vec3.Zero, 1, Vec3.UnitZ);
        var mesh = new Mesh();
        var s = new ParticleSettings { Look = ParticleLook.Object, InstanceOf = "Leaf", AlignToMotion = true };
        ParticleLooks.AddInstances(mesh, [new ParticleSystem.Particle(new Vec3(1, 2, 3), 0.5, 0, new Vec3(5, 0, 0))], s, leaf, 3, Mat4.Identity);
        // Centred on the particle, its side the particle's size, its +Z turned along the flight (+X), on the surfaces after the first three.
        AssertNear(new Vec3(1, 2, 3), mesh.Centroid(mesh.Polygons[0]), 1e-9);
        AssertNear(0.5, mesh.Bounds().Size.Length / Math.Sqrt(2), 1e-9);
        AssertNear(1, Math.Abs(mesh.Normal(0).X), 1e-9);
        Assert.Equal(3, mesh.Polygons[0].Surface);
        // Through a scene: a copy on each particle, with the leaf's surfaces after the particles'.
        var scene = new Scene { FramesPerSecond = 30 };
        var o = scene.AddObject("Emitter", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        scene.AddObject("Leaf", Doc(leaf, x => x.Name = "Green"));
        o.Particles = new ParticleSettings { Rate = 30, EndFrame = 60, Look = ParticleLook.Object, InstanceOf = "Leaf", Spin = 90 }.Checked();
        var f = scene.Evaluate(15).Objects[0];
        Assert.Equal(["Default", "Emitter particles", "Green"], f.Surfaces.Select(x => x.Name));
        Assert.Equal(16, f.Mesh.Polygons.Count(p => p.Surface == 2));
        // With the leaf missing, diamonds stand in.
        o.Particles = o.Particles with { InstanceOf = "Nothing" };
        Assert.Equal(16 * 8, scene.Evaluate(15).Objects[0].Mesh.Polygons.Count(p => p.Surface == 1));
        Assert.Contains("instance_of", Assert.Throws<ArgumentException>(() => new ParticleSettings { Look = ParticleLook.Object }.Checked()).Message);
    }

    /// <summary>A scene looking at a cloud of still particles at the origin from 3 m back.</summary>
    private static (Scene Scene, ObjectItem Emitter) Cloud(ParticleLook look, Vec3 background, Rgb color)
    {
        var scene = new Scene { FramesPerSecond = 30, AmbientIntensity = 1, BackgroundColor = background };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -3));
        cam.Width = cam.Height = 32;
        var o = scene.AddObject("Emitter", Doc(Square(Vec3.Zero, 0.01, Vec3.UnitY)));
        o.Particles = new ParticleSettings
        {
            From = EmitFrom.Origin, Rate = 300, EndFrame = 30, Life = 10, Speed = 0.6, Spread = 180, Gravity = 0, Size = 0.4, Look = look,
            Color = color, Glow = 0, HideEmitter = true, Opacity = 0.8,
        }.Checked();
        return (scene, o);
    }

    private static double Brightness(RenderImage img)
    {
        double sum = 0;
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++) sum += img.Pixel(x, y).G;
        return sum / (img.Width * img.Height);
    }

    [Fact]
    public void Puffs_hide_what_is_behind_them_and_sprites_add_light_and_the_views_see_diamonds()
    {
        var settings = new RenderSettings { SamplesPerPixel = 1 };
        // Dark smoke in front of a white backdrop darkens the picture, and covers it.
        var (smoke, puffs) = Cloud(ParticleLook.Puff, Vec3.One, new Rgb(30, 30, 30));
        var f = smoke.Evaluate(30).Objects[0];
        Assert.Equal(1, f.ViewOnlySurface);
        Assert.Equal(300 + 1, f.Soft.Count);
        Assert.True(f.Mesh.Polygons.Count(p => p.Surface == 1) > 0);
        double through = Brightness(RayTracer.Render(smoke, 30, settings, null, Cancel));
        Assert.True(through < 200, $"{through}");
        // Sprites on black add light.
        var (sparks, _) = Cloud(ParticleLook.Sprite, Vec3.Zero, new Rgb(255, 255, 255));
        double glow = Brightness(RayTracer.Render(sparks, 30, settings, null, Cancel));
        Assert.True(glow > 10, $"{glow}");
        // Sprites with no brightness show nothing at all: the views' diamonds aren't rendered.
        sparks.Objects.First().Particles = sparks.Objects.First().Particles! with { Opacity = 0 };
        Assert.Equal(0, Brightness(RayTracer.Render(sparks, 30, settings, null, Cancel)));
        // Puffs that hide nothing leave the backdrop as it was.
        puffs.Particles = puffs.Particles! with { Opacity = 0 };
        Assert.Equal(255, Brightness(RayTracer.Render(smoke, 30, settings, null, Cancel)));
    }

    /// <summary>The average green of a box of pixels, from fractions of the width and height.</summary>
    private static double Region(RenderImage img, double x0, double x1, double y0, double y1)
    {
        double sum = 0;
        int count = 0;
        for (int y = (int)(y0 * img.Height); y < (int)(y1 * img.Height); y++)
            for (int x = (int)(x0 * img.Width); x < (int)(x1 * img.Width); x++) { sum += img.Pixel(x, y).G; count++; }
        return sum / count;
    }

    [Fact]
    public void Puffs_cast_shadows_and_are_seen_in_mirrors()
    {
        var settings = new RenderSettings { SamplesPerPixel = 1 };
        // A white wall a metre behind the cloud, lit from the side so the cloud's shadow falls 2 m to its right.
        var (scene, puffs) = Cloud(ParticleLook.Puff, Vec3.Zero, new Rgb(200, 200, 200));
        scene.AmbientIntensity = 0;
        var cam = scene.Cameras.First();
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Zoom.SetConstant(1);
        cam.Width = cam.Height = 48;
        scene.AddObject("Wall", Doc(Square(new Vec3(0, 0, 1), 8, -Vec3.UnitZ)));
        var sun = scene.AddLight("Sun");
        sun.SetRotation(0, new Vec3(Math.Atan2(2, 1) * 180 / Math.PI, 0, 0));
        // Where the shadow falls the camera sees only the wall (the cloud is at most a metre across in front of it).
        var shaded = RayTracer.Render(scene, 30, settings, null, Cancel);
        puffs.Particles = puffs.Particles! with { Opacity = 0 };
        var clear = RayTracer.Render(scene, 30, settings, null, Cancel);
        double inShadow = Region(shaded, 0.66, 0.74, 0.45, 0.55), lit = Region(clear, 0.66, 0.74, 0.45, 0.55);
        Assert.True(inShadow < lit * 0.8, $"{inShadow} against {lit}");
        // Far from the shadow, the same.
        AssertNear(Region(clear, 0.05, 0.15, 0.45, 0.55), Region(shaded, 0.05, 0.15, 0.45, 0.55), 1);

        // A mirror in front of the camera shows a glowing cloud behind it, which the camera can't see itself.
        var (room, cloud) = Cloud(ParticleLook.Puff, Vec3.Zero, new Rgb(255, 255, 255));
        cloud.Particles = cloud.Particles! with { Glow = 1 };
        cloud.SetPosition(0, new Vec3(0, 0, -6));
        room.AddObject("Mirror", Doc(Square(new Vec3(0, 0, 1), 8, -Vec3.UnitZ), sf => { sf.Diffuse = 0; sf.Reflection = 1; }));
        Assert.True(Brightness(RayTracer.Render(room, 30, settings, null, Cancel)) > 5);
        cloud.Particles = cloud.Particles with { Opacity = 0 };
        Assert.Equal(0, Brightness(RayTracer.Render(room, 30, settings, null, Cancel)));
    }

    /// <summary>The hair's points (in the object's coordinates) at a frame.</summary>
    private static List<Vec3> HairPoints(Scene scene, double frame)
    {
        var f = scene.Evaluate(frame).Objects[0];
        int surface = f.Surfaces.ToList().FindIndex(x => x.Name.EndsWith(" hair"));
        return [.. f.Mesh.Polygons.Where(p => p.Surface == surface).SelectMany(p => p.Points).Distinct().Select(i => f.Mesh.Points[i])];
    }

    [Fact]
    public void Combed_hair_lies_along_the_comb()
    {
        var scene = new Scene();
        var o = scene.AddObject("Rug", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        o.Hair = new HairSettings { Density = 200, Length = 0.1, LengthJitter = 0, Droop = 0, Frizz = 0 }.Checked();
        Assert.True(HairPoints(scene, 1).Max(p => p.Y) > 0.09);
        o.Hair = o.Hair with { Comb = 1, CombDirection = new Vec3(1, 0, 0) };
        var combed = HairPoints(scene, 1);
        Assert.True(combed.Max(p => p.Y) < 0.01, $"{combed.Max(p => p.Y)}");
        Assert.True(combed.Max(p => p.X) > 0.55);
        Assert.Contains("comb direction", Assert.Throws<ArgumentException>(() => (o.Hair with { CombDirection = Vec3.Zero }).Checked()).Message);
    }

    [Fact]
    public void Hair_combed_by_hand_lies_where_it_was_combed()
    {
        var scene = new Scene();
        var o = scene.AddObject("Rug", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        o.Hair = new HairSettings { Density = 400, Length = 0.1, LengthJitter = 0, Droop = 0, Frizz = 0 }.Checked();
        var points = o.BaseMesh().Points;
        // Every point combed along +X: as the comb for the whole object does.
        o.Hair = HairComb.Comb(o.Hair, points, null, 0, new Vec3(1, 0, 0), 1).Checked();
        Assert.Equal(4, o.Hair.Combing!.Combed);
        var all = HairPoints(scene, 1);
        Assert.Equal(HairPoints(WithComb(new Vec3(1, 0, 0)), 1), all);
        // Uncombed again: standing up, the combing gone.
        o.Hair = HairComb.Comb(o.Hair, points, null, 0, Vec3.Zero, 1, uncomb: true);
        Assert.Null(o.Hair.Combing);
        Assert.True(HairPoints(scene, 1).Max(p => p.Y) > 0.09);
        // Combed only at one corner: the hair there lies down, the far corner's stands up; half strength, half way.
        o.Hair = HairComb.Comb(o.Hair, points, new Vec3(-0.5, 0, -0.5), 0.5, new Vec3(0, 0, -1), 1);
        Assert.Equal(1, o.Hair.Combing!.Combed);
        var corner = HairPoints(scene, 1);
        Assert.True(corner.Where(p => p.X < -0.4 && p.Z < -0.4).Min(p => p.Y) < 0.03);
        Assert.True(corner.Where(p => p.X > 0.4 && p.Z > 0.4).Max(p => p.Y) > 0.09);
        var half = HairComb.Comb(new HairSettings(), points, null, 0, new Vec3(0, 0, 2), 0.5);
        AssertNear(new Vec3(0, 0, 0.5), half.Combing!.Directions[0]);
        // Kept in scene files, and checked.
        var back = (ObjectItem)SceneJson.Read(SceneJson.Write(scene, embedMeshes: true)).Scene.Find("Rug")!;
        Assert.Equal(o.Hair, back.Hair);
        Assert.Contains("combing", Assert.Throws<ArgumentException>(() => (o.Hair with { Combing = new CombMap([new Vec3(3, 0, 0)]) }).Checked()).Message);
        Assert.Contains("direction", Assert.Throws<ArgumentException>(() => HairComb.Comb(o.Hair, points, null, 0, Vec3.Zero, 1)).Message);

        Scene WithComb(Vec3 way)
        {
            var other = new Scene();
            other.AddObject("Rug", Doc(Square(Vec3.Zero, 1, Vec3.UnitY))).Hair = o.Hair! with { Combing = null, Comb = 1, CombDirection = way };
            return other;
        }
    }

    [Fact]
    public void Hair_swings_with_the_bones_that_move_it()
    {
        // A rug standing still, its one bone sliding it +X for half a second and stopping.
        var scene = new Scene { FramesPerSecond = 30 };
        var o = scene.AddObject("Rug", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        var bone = scene.AddBone(o, "Bone", Vec3.Zero, Vec3.Zero, 1);
        bone.SetPosition(1, Vec3.Zero);
        bone.SetPosition(15, new Vec3(1, 0, 0));
        o.Hair = new HairSettings { Density = 100, Length = 0.2, Droop = 0, Frizz = 0 }.Checked();
        var still = HairPoints(scene, 17);
        o.Hair = o.Hair with { Dynamics = true };
        var swung = HairPoints(scene, 17);
        // The strands carry on forward past where they'd stand; long after, they stand again.
        Assert.True(swung.Average(p => p.X) > still.Average(p => p.X) + 0.005, $"{swung.Average(p => p.X)} against {still.Average(p => p.X)}");
        o.Hair = o.Hair with { Dynamics = false };
        var later = HairPoints(scene, 300);
        o.Hair = o.Hair with { Dynamics = true };
        var settled = HairPoints(scene, 300);
        for (int i = 0; i < later.Count; i++) AssertNear(later[i], settled[i], 1e-4);
    }

    [Fact]
    public void Hair_swings_on_when_the_object_stops_and_settles()
    {
        var s = new HairSettings { Dynamics = true, Stiffness = 2, Damping = 0.3 };
        // Moving +X at 1 m/s for a second, then stopped dead.
        Mat4 At(double frame) => Mat4.Translation(new Vec3(Math.Min(frame / 30, 1), 0, 0));
        var root = new Vec3(0, 0.5, 0);
        // While it moves steadily the hair keeps up; just after the stop it carries on forward; much later it's still.
        Assert.True(HairSwing.Lag(s, 25, 30, 0, At)(root).Length < 1e-3);
        Assert.True(HairSwing.Lag(s, 33, 30, 0, At)(root).X > 0.02);
        Assert.True(HairSwing.Lag(s, 300, 30, 0, At)(root).Length < 1e-4);
        // Turning swings the tips by their distance from the turn.
        Mat4 Spin(double frame) => Mat4.RotationY(Math.Min(frame / 30, 1));
        var near = HairSwing.Lag(s, 33, 30, 0, Spin)(new Vec3(0.1, 0, 0)).Length;
        var far = HairSwing.Lag(s, 33, 30, 0, Spin)(new Vec3(1, 0, 0)).Length;
        AssertNear(10, far / near, 1e-6);
        // Through a scene: the strands move with the swing.
        var scene = new Scene { FramesPerSecond = 30 };
        var o = scene.AddObject("Ball", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        o.SetPosition(1, Vec3.Zero);
        o.SetPosition(15, new Vec3(1, 0, 0));
        o.Hair = new HairSettings { Density = 100, Length = 0.2 }.Checked();
        var still = HairPoints(scene, 17);
        o.Hair = o.Hair with { Dynamics = true };
        Assert.NotEqual(still, HairPoints(scene, 17));
    }

    /// <summary>An n × n grid of quads, size metres, flat at a height, facing up; its first row of faces on surface 1.</summary>
    private static Document Cloth(int n, double size, double height)
    {
        var mesh = new Mesh();
        Vec3 P(int i, int j) => new(-size / 2 + size * i / n, height, -size / 2 + size * j / n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) Face(mesh, j == n - 1 ? 1 : 0, Vec3.UnitY, P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1));
        mesh.MergePoints(1e-9);
        var doc = Doc(mesh);
        doc.Surfaces.Add(new Surface("Edge"));
        return doc;
    }

    private static Vec3[] World(SceneFrame f, ObjectItem o)
    {
        var x = f.Objects.First(q => q.Item == o);
        return [.. x.Mesh.Points.Select(x.World.TransformPoint)];
    }

    [Fact]
    public void Cloth_falls_under_gravity_and_stops_on_the_floor()
    {
        var scene = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 90 };
        var o = scene.AddObject("Cloth", Cloth(6, 1, 1));
        o.SoftBody = new SoftBodySettings { Damping = 0, Floor = 0 }.Checked();
        // Before it starts, as modelled; a third of a second in, ½ g t² lower (all of it together).
        Assert.All(World(scene.Evaluate(0), o), p => AssertNear(1, p.Y));
        Assert.All(World(scene.Evaluate(10), o), p => AssertNear(1 - 0.5 * 9.81 / 9, p.Y, 0.02));
        // Two seconds in, lying on the floor, its thickness above it, still its size.
        var down = World(scene.Evaluate(60), o);
        Assert.All(down, p => AssertNear(0.005, p.Y, 1e-6));
        AssertNear(1, down.Max(p => p.X) - down.Min(p => p.X), 0.02);
    }

    [Fact]
    public void Pinned_cloth_hangs_and_a_frame_is_the_same_however_its_reached()
    {
        var scene = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 90 };
        var o = scene.AddObject("Flag", Cloth(8, 1, 1));
        o.SoftBody = new SoftBodySettings { Pin = "Edge", Stiffness = 1 }.Checked();
        var start = World(scene.Evaluate(0), o);
        var hung = World(scene.Evaluate(60), o);
        // The pinned edge (z = +0.5 and the row before it) stays put; the far edge hangs about the cloth's length below.
        for (int i = 0; i < start.Length; i++)
            if (start[i].Z > 0.5 - 1.0 / 8 - 1e-9) AssertNear(start[i], hung[i]);
        Assert.True(hung.Min(p => p.Y) < 1 - 0.6, $"{hung.Min(p => p.Y)}");
        // Its edges keep about their length.
        var mesh = o.BaseMesh();
        foreach (var poly in mesh.Polygons)
            for (int k = 0; k < 4; k++)
            {
                int a = poly.Points[k], b = poly.Points[(k + 1) % 4];
                AssertNear(1.0 / 8, (hung[a] - hung[b]).Length, 0.02);
            }
        // Straight to frame 45, after going on to 60, and in a copy: the same.
        var at45 = World(scene.Evaluate(45), o);
        var fresh = scene.Copy();
        Assert.Equal(at45, World(fresh.Evaluate(45), (ObjectItem)fresh.Find("Flag")!));
        scene.Evaluate(80);
        Assert.Equal(at45, World(scene.Evaluate(45), o));
        // A change to the scene starts it again, the same as before.
        scene.Notify();
        Assert.Equal(at45, World(scene.Evaluate(45), o));
    }

    [Fact]
    public void A_full_goal_follows_the_animation_and_collisions_drape_cloth_over_an_object()
    {
        var scene = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 90 };
        var o = scene.AddObject("Jelly", Cloth(4, 1, 1));
        o.SetPosition(0, Vec3.Zero);
        o.SetPosition(30, new Vec3(0, 0, 2));
        o.SoftBody = new SoftBodySettings { Goal = 1 }.Checked();
        var f = scene.Evaluate(20);
        var animated = o.BaseMesh().Points.Select(f.WorldOf(o).TransformPoint).ToArray();
        var soft = World(f, o);
        for (int i = 0; i < soft.Length; i++) AssertNear(animated[i], soft[i], 1e-9);
        // A sheet dropped on a ball: over the ball it stays on top; its edges hang below the ball's middle.
        var drape = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 90 };
        var cloth = drape.AddObject("Sheet", Cloth(10, 1.2, 0.6));
        drape.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.3, 32, 16)));
        cloth.SoftBody = new SoftBodySettings { Collide = true, Bend = 0.05 }.Checked();
        var lying = World(drape.Evaluate(60), cloth);
        var top = lying.OrderBy(p => p.X * p.X + p.Z * p.Z).First();
        Assert.True(top.Y > 0.29, $"{top.Y}");
        Assert.True(lying.Min(p => p.Y) < 0, $"{lying.Min(p => p.Y)}");
        Assert.All(lying, p => Assert.True(p.Length > 0.29, $"{p} is inside the ball"));
    }

    [Fact]
    public void Self_collision_keeps_cloth_falling_on_itself_from_passing_through()
    {
        // Two sheets in one object, the upper half a square across, so its points fall between the lower one's.
        static Document Two(bool self)
        {
            var doc = Cloth(6, 1, 0.3);
            var mesh = doc.Layers[0].Mesh;
            int count = mesh.Points.Count;
            foreach (var p in mesh.Points.Take(count).ToList()) mesh.Points.Add(p + new Vec3(1.0 / 12, 0.3, 1.0 / 12));
            foreach (var poly in mesh.Polygons.ToList()) mesh.Polygons.Add(new Polygon([.. poly.Points.Select(i => i + count)], poly.Surface));
            return doc;
        }
        (double Lower, double Upper) Rest(bool self)
        {
            var scene = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 90 };
            var o = scene.AddObject("Sheets", Two(self));
            o.SoftBody = new SoftBodySettings { Floor = 0, Damping = 2, SelfCollide = self }.Checked();
            var at = World(scene.Evaluate(75), o);
            int half = at.Length / 2;
            return (at.Take(half).Average(p => p.Y), at.Skip(half).Min(p => p.Y));
        }
        // Without it both lie flat on the floor, one through the other; with it the upper lies on the lower.
        var (lower, upper) = Rest(false);
        AssertNear(0.005, lower, 1e-3);
        AssertNear(0.005, upper, 1e-3);
        (lower, upper) = Rest(true);
        AssertNear(0.005, lower, 0.01);
        Assert.True(upper > lower + 0.05, $"{upper} over {lower}");
        Assert.Contains("self-collision", Assert.Throws<ArgumentException>(() => new SoftBodySettings { SelfDistance = -1 }.Checked()).Message);
    }

    /// <summary>A pour: 201 particles 5 cm across from a spout 20 cm across half a metre up, over a second, living long.</summary>
    private static readonly ParticleSettings Pour = new()
    {
        From = EmitFrom.Surface, Rate = 200, EndFrame = 30, Life = 30, Speed = 0.3, Spread = 0, Size = 0.05, Fluid = true, Viscosity = 0.05,
    };

    private static (Scene Scene, ObjectItem Spout) Pouring(ParticleSettings s)
    {
        var scene = new Scene { FramesPerSecond = 30, FirstFrame = 0, LastFrame = 120 };
        var o = scene.AddObject("Spout", Doc(Square(Vec3.Zero, 0.2, -Vec3.UnitY)));
        o.SetPosition(0, new Vec3(0, 0.5, 0));
        o.Particles = s.Checked();
        return (scene, o);
    }

    private static List<ParticleSystem.Particle> Fluid(Scene scene, ObjectItem o, double frame) =>
        Fluids.At(scene, o, o.Particles!, frame, o.BaseMesh(), f => o.WorldMatrix(f),
            o.Particles!.Collide ? Colliders.Of(scene, [o], frame, 1 / (scene.FramesPerSecond * Fluids.StepsPerFrame(scene.FramesPerSecond))) : null);

    [Fact]
    public void A_fluid_pours_and_spreads_into_a_puddle_its_particles_kept_apart()
    {
        // Poured straight down: without the fluid they'd stop where they land, under the spout; as a fluid they spread out.
        var (scene, spout) = Pouring(Pour with { Floor = 0 });
        var puddle = Fluid(scene, spout, 90);
        Assert.Equal(201, puddle.Count);
        Assert.All(puddle, q => Assert.True(q.Position.Y >= -1e-9, $"{q.Position} is under the floor"));
        double across = puddle.Max(q => q.Position.X) - puddle.Min(q => q.Position.X);
        Assert.True(across > 0.4, $"{across}");
        // Low: a puddle a few particles deep, not a column.
        Assert.True(puddle.Max(q => q.Position.Y) < 0.2, $"{puddle.Max(q => q.Position.Y)}");
        // No two closer than half a size: they push each other apart.
        double closest = double.PositiveInfinity;
        for (int i = 0; i < puddle.Count; i++)
            for (int j = i + 1; j < puddle.Count; j++) closest = Math.Min(closest, (puddle[i].Position - puddle[j].Position).Length);
        Assert.True(closest > 0.4 * 0.05, $"{closest}");
        // The same however the frame is reached: straight there, after going on, and in a copy.
        var at45 = Fluid(scene, spout, 45).Select(q => q.Position).ToList();
        Fluid(scene, spout, 100);
        Assert.Equal(at45, Fluid(scene, spout, 45).Select(q => q.Position));
        var copy = scene.Copy();
        var other = (ObjectItem)copy.Find("Spout")!;
        Assert.Equal(at45, Fluid(copy, other, 45).Select(q => q.Position));
        // Through the scene, as blobs: one surface.
        spout.Particles = spout.Particles! with { Look = ParticleLook.Blob };
        Assert.True(scene.Evaluate(90).Objects[0].Mesh.Polygons.Count > 100);
        Assert.Contains("viscosity", Assert.Throws<ArgumentException>(() => (Pour with { Viscosity = 2 }).Checked()).Message);
    }

    [Fact]
    public void A_fluid_fills_what_holds_it()
    {
        // An open box 40 cm across and a metre deep, its walls facing in, round the spout (high enough that splashes stay in).
        var (scene, spout) = Pouring(Pour with { Collide = true });
        var box = new Mesh();
        double h = 0.2;
        Face(box, 0, Vec3.UnitY, new(-h, 0, -h), new(h, 0, -h), new(h, 0, h), new(-h, 0, h));
        Face(box, 0, Vec3.UnitX, new(-h, 0, -h), new(-h, 0, h), new(-h, 1.0, h), new(-h, 1.0, -h));
        Face(box, 0, -Vec3.UnitX, new(h, 0, -h), new(h, 1.0, -h), new(h, 1.0, h), new(h, 0, h));
        Face(box, 0, Vec3.UnitZ, new(-h, 0, -h), new(-h, 1.0, -h), new(h, 1.0, -h), new(h, 0, -h));
        Face(box, 0, -Vec3.UnitZ, new(-h, 0, h), new(h, 0, h), new(h, 1.0, h), new(-h, 1.0, h));
        scene.AddObject("Box", Doc(box));
        var held = Fluid(scene, spout, 90);
        Assert.Equal(201, held.Count);
        Assert.All(held, q => Assert.True(Math.Abs(q.Position.X) < h + 0.01 && Math.Abs(q.Position.Z) < h + 0.01 && q.Position.Y > -0.01,
            $"{q.Position} got out of the box"));
        // Settled in the bottom of it, spread across it.
        Assert.True(held.Average(q => q.Position.Y) < 0.15, $"{held.Average(q => q.Position.Y)}");
        Assert.True(held.Max(q => q.Position.X) - held.Min(q => q.Position.X) > 0.25);
    }

    [Fact]
    public void Particles_hair_and_soft_bodies_are_kept_in_lightwave_scenes_as_plugin_sections()
    {
        var scene = new Scene();
        var o = scene.AddObject("Thing", Cloth(2, 1, 0));
        var plain = scene.AddObject("Plain", Doc(Square(Vec3.Zero, 1, Vec3.UnitY)));
        o.Particles = new ParticleSettings { Look = ParticleLook.Blob, Collide = true, OnHit = ParticleHit.Bounce, Floor = -2, Fluid = true, Viscosity = 0.2 }.Checked();
        o.Hair = HairComb.Comb(new HairSettings { Dynamics = true, Comb = 0.3 }, o.BaseMesh().Points, null, 0, new Vec3(1, 0, 0), 0.5).Checked();
        o.SoftBody = new SoftBodySettings { Pin = "Edge", SelfCollide = true }.Checked();
        var warnings = new List<string>();
        string lws = LwsWriter.Write(scene, warnings);
        Assert.Contains("Plugin CustomObjHandler 1 AVA_Particles", lws);
        Assert.Contains("Plugin CustomObjHandler 1 AVA_Hair", lws);
        Assert.Contains("Plugin CustomObjHandler 1 AVA_SoftBody", lws);
        Assert.Equal(3, lws.Split('\n').Count(l => l == "EndPlugin"));
        var read = LwsReader.Read(lws, _ => new Document(), "/no/such");
        var back = read.Scene.Objects.First(x => x.Name == "Thing");
        Assert.Equal((o.Particles, o.Hair, o.SoftBody), (back.Particles, back.Hair, back.SoftBody));
        var other = read.Scene.Objects.First(x => x.Name == "Plain");
        Assert.True(other.Particles == null && other.Hair == null && other.SoftBody == null);
        // An object without them writes none, as before.
        o.Particles = null;
        o.Hair = null;
        o.SoftBody = null;
        Assert.DoesNotContain("Plugin", LwsWriter.Write(scene));
        _ = plain;
        // A section that can't be read is a warning, and the rest of the scene still is.
        string bad = lws.Replace("\"look\":\"Blob\"", "\"look\":\"Teapot\"");
        var broken = LwsReader.Read(bad, _ => new Document(), "/no/such");
        Assert.Contains(broken.Warnings, w => w.Contains("AVA_Particles"));
        Assert.NotNull(broken.Scene.Objects.First(x => x.Name == "Thing").Hair);
    }

    [Fact]
    public void New_settings_are_kept_through_copies_snapshots_and_scene_files()
    {
        var scene = new Scene();
        var o = scene.AddObject("Thing", Cloth(2, 1, 0));
        o.Particles = new ParticleSettings { Look = ParticleLook.Puff, Opacity = 0.3, Collide = true, OnHit = ParticleHit.Bounce, Bounce = 0.7 }.Checked();
        o.Hair = new HairSettings { Dynamics = true, Stiffness = 5, Comb = 0.5, CombDirection = new Vec3(1, 0, 0) }.Checked();
        o.SoftBody = new SoftBodySettings { Goal = 0.4, Pin = "Edge", Floor = -1, StartFrame = 10, SelfCollide = true, SelfDistance = 0.02 }.Checked();
        var copy = (ObjectItem)scene.Copy().Find("Thing")!;
        Assert.Equal((o.Particles, o.Hair, o.SoftBody), (copy.Particles, copy.Hair, copy.SoftBody));
        var snap = scene.Snapshot();
        var kept = o.SoftBody;
        o.SoftBody = null;
        snap.Restore();
        Assert.Equal(kept, o.SoftBody);
        var back = (ObjectItem)SceneJson.Read(SceneJson.Write(scene, embedMeshes: true)).Scene.Find("Thing")!;
        Assert.Equal((o.Particles, o.Hair, o.SoftBody), (back.Particles, back.Hair, back.SoftBody));
        Assert.DoesNotContain("softBody", SceneJson.Write(new Scene(), embedMeshes: true));
        Assert.Contains("goal", Assert.Throws<ArgumentException>(() => new SoftBodySettings { Goal = 2 }.Checked()).Message);
    }
}
