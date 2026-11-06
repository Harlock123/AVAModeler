using System.Diagnostics;
using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>
/// The lights' own effects in the ray tracer: shadow maps (lit where lit, dark under an occluder, a wider soft edge
/// with fuzziness, no shadow rays), lens flares (where the light is, faded behind objects and off screen, alpha left
/// alone) and volumetric beams (inside the cone only, shadowed, dimmed by fog), each deterministic on any number of threads.
/// </summary>
public class LightEffectsTests
{
    private static RenderImage Render(Scene scene, double frame, RenderSettings? settings = null) =>
        RayTracer.Render(scene, frame, settings, null, TestContext.Current.CancellationToken);

    private static int Brightness((byte R, byte G, byte B, byte A) p) => p.R + p.G + p.B;

    /// <summary>Where a world point lands in the image of the scene's camera at a frame.</summary>
    private static (int X, int Y) PixelOf(Scene scene, Vec3 p, int width, int height, double frame = 0)
    {
        var cam = scene.CurrentCamera!;
        var local = cam.CameraToWorld(frame).Inverted().TransformPoint(p);
        double tanV = 1 / cam.Zoom.Evaluate(frame), tanH = tanV * width * cam.PixelAspect / height;
        double x = (local.X / local.Z / tanH + 1) / 2 * width;
        double y = (1 - local.Y / local.Z / tanV) / 2 * height;
        return ((int)x, (int)y);
    }

    private static int At(RenderImage image, Scene scene, Vec3 p)
    {
        var (x, y) = PixelOf(scene, p, image.Width, image.Height);
        return Brightness(image.Pixel(x, y));
    }

    // ───────────────────────────── shadow maps ─────────────────────────────

    /// <summary>A square blocker a metre over the ground, a light 4 m up shining down, and a camera looking straight down.</summary>
    private static (Scene Scene, LightItem Light) Shadow(LightType type)
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Top");
        cam.SetPosition(0, new Vec3(0, 6, 0));
        cam.SetRotation(0, new Vec3(0, 90, 0));
        scene.AddObject("Ground", Doc(Square(Vec3.Zero, 20, Vec3.UnitY)));
        scene.AddObject("Blocker", Doc(Square(new Vec3(0, 1, 0), 1, -Vec3.UnitY)));
        var light = scene.AddLight("Light", type);
        light.SetPosition(0, new Vec3(0, 4, 0));
        light.SetRotation(0, new Vec3(0, 90, 0));
        light.ConeAngle = 60;
        return (scene, light);
    }

    /// <summary>The ground's brightness along the row through the middle, from the shadow out past its edge.</summary>
    private static int[] Row(RenderImage image) => Enumerable.Range(image.Width / 2, image.Width / 2).Select(x => Brightness(image.Pixel(x, image.Height / 2))).ToArray();

    /// <summary>How many pixels along a row lie between the shadow's darkness and the full light (the penumbra).</summary>
    private static int Penumbra(int[] row)
    {
        int dark = row[0], bright = row.Max();
        double lo = dark + (bright - dark) * 0.1, hi = bright - (bright - dark) * 0.1;
        return row.Count(v => v > lo && v < hi);
    }

    [Theory]
    [InlineData(LightType.Spot)]
    [InlineData(LightType.Point)]
    [InlineData(LightType.Distant)]
    public void A_shadow_map_shadows_what_ray_tracing_shadows_without_a_shadow_ray(LightType type)
    {
        var (scene, light) = Shadow(type);
        var traced = new RenderSettings { Width = 64, Height = 64, Counters = new RenderCounters() };
        var rays = Render(scene, 0, traced);
        Assert.True(traced.Counters!.ShadowRays > 0);

        light.ShadowMap = new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 0 };
        var mapped = new RenderSettings { Width = 64, Height = 64, Counters = new RenderCounters() };
        var map = Render(scene, 0, mapped);
        Assert.Equal(0, mapped.Counters!.ShadowRays);
        Assert.True(mapped.Counters!.ShadowMapLookups > 0);

        var row = Row(map);
        Assert.True(row[0] < 10 && row.Max() > 200, string.Join(",", row));
        // Lit where lit, dark where dark: only pixels along the shadow's edge may differ.
        int differ = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (Math.Abs(Brightness(map.Pixel(x, y)) - Brightness(rays.Pixel(x, y))) > 60) differ++;
        Assert.True(differ <= 64 * 64 / 40, $"{differ} pixels differ from the ray-traced shadow.");
    }

    [Fact]
    public void Fuzziness_widens_a_shadow_maps_soft_edge()
    {
        var (scene, light) = Shadow(LightType.Spot);
        var settings = new RenderSettings { Width = 64, Height = 64 };
        light.ShadowMap = new ShadowMapSettings { On = true, Size = 512, Fuzziness = 0 };
        int hard = Penumbra(Row(Render(scene, 0, settings)));
        light.ShadowMap = light.ShadowMap with { Fuzziness = 12 };
        var soft = Row(Render(scene, 0, settings));
        Assert.True(hard <= 2 && Penumbra(soft) >= 4, $"Fuzziness 0: {hard} px, 12: {Penumbra(soft)} px ({string.Join(",", soft)}).");
        // Still dark in the middle and lit far out.
        Assert.True(soft[0] < 10 && soft[^1] > 200, string.Join(",", soft));
    }

    [Fact]
    public void A_soft_light_with_a_shadow_map_is_lit_as_a_point_and_an_area_light_keeps_shadow_rays()
    {
        var (scene, light) = Shadow(LightType.Point);
        light.Radius = 0.3;
        light.ShadowSamples = 16;
        light.ShadowMap = new ShadowMapSettings { On = true };
        var settings = new RenderSettings { Width = 32, Height = 32, Counters = new RenderCounters() };
        Render(scene, 0, settings);
        Assert.Equal(0, settings.Counters!.ShadowRays);

        light.Type = LightType.Area;
        Assert.False(light.UsesShadowMap);
        settings.Counters = new RenderCounters();
        Render(scene, 0, settings);
        Assert.True(settings.Counters!.ShadowRays > 0);
        Assert.Equal(0, settings.Counters!.ShadowMapLookups);
    }

    /// <summary>A few thousand triangles over a floor: a ball on a pedestal and a ring of posts round it, under a spot.</summary>
    internal static (Scene Scene, LightItem Spot) Heavy()
    {
        var scene = new Scene { AmbientIntensity = 0.1 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 3, -6));
        cam.SetRotation(0, new Vec3(0, 25, 0));
        scene.AddObject("Floor", Doc(Square(Vec3.Zero, 20, Vec3.UnitY)));
        scene.AddObject("Ball", Doc(Sphere(new Vec3(0, 1, 0), 0.8, 64, 32), s => s.Smoothing = true));
        for (int i = 0; i < 6; i++)
        {
            double a = i * Math.PI / 3;
            scene.AddObject($"Post {i}", Doc(Sphere(new Vec3(2 * Math.Cos(a), 0.4, 2 * Math.Sin(a)), 0.3, 24, 12)));
        }
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.SetPosition(0, new Vec3(1, 5, -1));
        spot.SetRotation(0, spot.RotationToward(new Vec3(0, 0, 0), 0));
        spot.ConeAngle = 45;
        return (scene, spot);
    }

    [Fact]
    public void A_shadow_map_takes_no_shadow_rays_where_a_soft_ray_traced_shadow_takes_many()
    {
        var (scene, spot) = Heavy();
        bool timings = Environment.GetEnvironmentVariable("AVAMODELER_LIGHT_TIMINGS") == "1";
        int w = timings ? 320 : 48, h = timings ? 240 : 36;
        spot.Radius = 0.3;
        spot.ShadowSamples = 16;
        var traced = new RenderSettings { Width = w, Height = h, Counters = new RenderCounters() };
        var clock = Stopwatch.StartNew();
        var soft = Render(scene, 0, traced);
        var tracedTime = clock.Elapsed;

        spot.ShadowMap = new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 3 };
        var mapped = new RenderSettings { Width = w, Height = h, Counters = new RenderCounters() };
        clock.Restart();
        var map = Render(scene, 0, mapped);
        var mapTime = clock.Elapsed;

        // Every lit point took 16 shadow rays from the soft light; with the map, none (one lookup a point instead).
        Assert.True(traced.Counters!.ShadowRays >= 8 * mapped.Counters!.ShadowMapLookups,
            $"{traced.Counters!.ShadowRays} shadow rays against {mapped.Counters!.ShadowMapLookups} lookups.");
        Assert.Equal(0, mapped.Counters!.ShadowRays);
        // Both pictures agree on the whole (the soft edges differ a little).
        double mean(RenderImage i) => Enumerable.Range(0, i.Width * i.Height).Average(p => Brightness(i.Pixel(p % i.Width, p / i.Width)));
        Assert.InRange(mean(map), mean(soft) * 0.85, mean(soft) * 1.15);
        TestContext.Current.TestOutputHelper?.WriteLine($"{w}×{h}: ray-traced soft shadow {tracedTime.TotalMilliseconds:0} ms, shadow map {mapTime.TotalMilliseconds:0} ms.");
        if (timings) WriteTimings(scene, spot, w, h);
    }

    /// <summary>The timings the report quotes: run with AVAMODELER_LIGHT_TIMINGS=1 and a console logger.</summary>
    private static void WriteTimings(Scene scene, LightItem spot, int w, int h)
    {
        double Time(Action<LightItem> set)
        {
            spot.Radius = 0;
            spot.ShadowMap = new ShadowMapSettings();
            spot.Flare = new LensFlare();
            spot.Volumetric = new VolumetricLight();
            set(spot);
            Render(scene, 0, new RenderSettings { Width = w, Height = h });
            var clock = Stopwatch.StartNew();
            Render(scene, 0, new RenderSettings { Width = w, Height = h });
            return clock.Elapsed.TotalMilliseconds;
        }
        var lines = new (string, Action<LightItem>)[]
        {
            ("plain (hard ray-traced shadow)", _ => { }),
            ("lens flare (glow, ring, 8 streaks)", l => l.Flare = new LensFlare { On = true, Ring = true, Streaks = 8 }),
            ("volumetric, 16 samples, shadowed", l => l.Volumetric = new VolumetricLight { On = true, Samples = 16 }),
            ("volumetric, 64 samples, shadowed", l => l.Volumetric = new VolumetricLight { On = true, Samples = 64 }),
            ("volumetric, 64 samples, unshadowed", l => l.Volumetric = new VolumetricLight { On = true, Samples = 64, Shadows = false }),
            ("volumetric, 64 samples, shadow map", l => { l.Volumetric = new VolumetricLight { On = true, Samples = 64 }; l.ShadowMap = new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 1 }; }),
            ("soft ray-traced shadow (radius 0.3, 16 samples)", l => { l.Radius = 0.3; l.ShadowSamples = 16; }),
            ("shadow map 1024, fuzziness 3", l => l.ShadowMap = new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 3 }),
            ("shadow map 2048, fuzziness 3", l => l.ShadowMap = new ShadowMapSettings { On = true, Size = 2048, Fuzziness = 3 }),
        };
        foreach (var (name, set) in lines) TestContext.Current.TestOutputHelper?.WriteLine($"TIMING {w}×{h} {name}: {Time(set):0} ms");
    }

    // ───────────────────────────── lens flares ─────────────────────────────

    /// <summary>A camera at the origin looking along +Z at a point light 10 m away, nothing else, all dark.</summary>
    private static (Scene Scene, LightItem Light) Flare()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        scene.AddCamera("Camera");
        var light = scene.AddLight("Bulb", LightType.Point);
        light.SetPosition(0, new Vec3(0, 0, 10));
        light.Flare = new LensFlare { On = true, Ring = true, Streaks = 6 };
        return (scene, light);
    }

    private static readonly RenderSettings Small = new() { Width = 64, Height = 48 };

    [Fact]
    public void A_flare_glows_where_the_light_is_and_leaves_alpha_alone()
    {
        var (scene, light) = Flare();
        var image = Render(scene, 0, Small);
        Assert.True(Brightness(image.Pixel(32, 24)) > 600, $"{image.Pixel(32, 24)}");
        Assert.True(Brightness(image.Pixel(32, 24)) > Brightness(image.Pixel(40, 30)));
        Assert.Equal(0, Brightness(image.Pixel(0, 0)));
        Assert.All(Enumerable.Range(0, 64 * 48), p => Assert.Equal(0, image.Rgba[p * 4 + 3]));

        // A wider glow reaches further.
        int edge = Brightness(image.Pixel(32 + 6, 24));
        light.Flare = light.Flare with { Size = 0.3 };
        Assert.True(Brightness(Render(scene, 0, Small).Pixel(32 + 6, 24)) > edge);

        // Off, or at intensity 0, the picture is the plain one.
        var plain = Render(Without(scene), 0, Small);
        Assert.All(plain.Rgba.Where((_, i) => i % 4 < 3), b => Assert.Equal(0, b));
        light.FlareIntensity.SetConstant(0);
        Assert.Equal(plain.Rgba, Render(scene, 0, Small).Rgba);
    }

    private static Scene Without(Scene scene)
    {
        var copy = scene.Copy();
        foreach (var l in copy.Lights) l.Flare = l.Flare with { On = false };
        return copy;
    }

    [Fact]
    public void A_flare_fades_behind_objects_and_is_dimmed_through_transparent_ones()
    {
        var (scene, _) = Flare();
        int clear = Brightness(Render(scene, 0, Small).Pixel(32, 24));
        var wall = scene.AddObject("Wall", Doc(Square(new Vec3(0, 0, 5), 4, -Vec3.UnitZ)));
        var hidden = Render(scene, 0, Small);
        Assert.Equal(Render(Without(scene), 0, Small).Rgba, hidden.Rgba);

        wall.Document!.Surfaces[0].Transparency = 0.5;
        int dimmed = Brightness(Render(scene, 0, Small).Pixel(32, 24));
        Assert.InRange(dimmed, clear / 4, clear - 50);

        // Not fading behind objects, it shows through the opaque wall.
        wall.Document.Surfaces[0].Transparency = 0;
        scene.Lights.Single().Flare = scene.Lights.Single().Flare with { FadeBehind = false };
        Assert.True(Brightness(Render(scene, 0, Small).Pixel(32, 24)) > 600);
    }

    [Fact]
    public void A_flare_fades_off_screen_and_a_light_behind_the_camera_has_none()
    {
        var (scene, light) = Flare();
        var plain = Render(Without(scene), 0, Small).Rgba;
        // Far out to the side, beyond the frame by more than the glow's radius.
        light.SetPosition(0, new Vec3(30, 0, 10));
        Assert.Equal(plain, Render(scene, 0, Small).Rgba);
        light.SetPosition(0, new Vec3(0, 0, -10));
        Assert.Equal(plain, Render(scene, 0, Small).Rgba);
        // Without the off-screen fade, a light just outside the frame still throws its glow and streaks in.
        light.SetPosition(0, new Vec3(4.5, 0, 10));
        light.Flare = light.Flare with { Size = 0.5, FadeOffScreen = false };
        Assert.NotEqual(plain, Render(scene, 0, Small).Rgba);
        light.Flare = light.Flare with { FadeOffScreen = true, Size = 0.01 };
        Assert.Equal(plain, Render(scene, 0, Small).Rgba);
    }

    [Fact]
    public void A_distant_light_flares_where_its_direction_meets_the_picture()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        scene.AddCamera("Camera");
        var sun = scene.AddLight("Sun");
        // Shining back toward the camera along -Z: the sun is straight ahead.
        sun.SetRotation(0, new Vec3(180, 0, 0));
        sun.Flare = new LensFlare { On = true, Size = 0.3 };
        var image = Render(scene, 0, Small);
        Assert.True(Brightness(image.Pixel(32, 24)) > 600, $"{image.Pixel(32, 24)}");
        // Shining the way the camera looks, the sun is behind it.
        sun.SetRotation(0, Vec3.Zero);
        Assert.All(Render(scene, 0, Small).Rgba.Where((_, i) => i % 4 < 3), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Streaks_are_the_same_every_render_and_differ_between_lights()
    {
        var (scene, light) = Flare();
        light.Flare = new LensFlare { On = true, Glow = false, Streaks = 5, StreakLength = 0.45 };
        var a = Render(scene, 0, Small);
        Assert.Equal(a.Rgba, Render(scene.Copy(), 0, Small).Rgba);
        // Away from the centre only the streaks' directions are lit.
        int lit = Enumerable.Range(0, 64 * 48).Count(p => Brightness(a.Pixel(p % 64, p / 64)) > 30);
        Assert.InRange(lit, 10, 64 * 48 / 4);
        light.Name = "Another";
        Assert.NotEqual(a.Rgba, Render(scene, 0, Small).Rgba);
    }

    [Fact]
    public void The_flare_intensity_envelope_animates_it()
    {
        var (scene, light) = Flare();
        light.FlareIntensity.SetConstant(0);
        light.FlareIntensity.SetKey(0, new EnvelopeKey(0, 0));
        light.FlareIntensity.AddKey(10, 1);
        Assert.Equal(0, Brightness(Render(scene, 0, Small).Pixel(32, 24)));
        int half = Brightness(Render(scene, 5, Small).Pixel(32, 24));
        int full = Brightness(Render(scene, 10, Small).Pixel(32, 24));
        Assert.True(half > 100 && half < full, $"{half} at frame 5, {full} at 10.");
        // Dissolve takes it away too.
        light.Flare = light.Flare with { Dissolve = 1 };
        Assert.Equal(0, Brightness(Render(scene, 10, Small).Pixel(32, 24)));
    }

    [Fact]
    public void Fading_with_distance_dims_a_far_light()
    {
        var (scene, light) = Flare();
        light.Flare = light.Flare with { FadeWithDistance = true, NominalDistance = 5 };
        light.FlareIntensity.SetConstant(0.4);
        int far = Brightness(Render(scene, 0, Small).Pixel(32, 24));
        light.Flare = light.Flare with { NominalDistance = 20 };
        int near = Brightness(Render(scene, 0, Small).Pixel(32, 24));
        Assert.True(far < near, $"{far} faded, {near} not.");
    }

    // ───────────────────────────── volumetric lights ─────────────────────────────

    /// <summary>A spot 4 m up shining straight down onto a floor in the dark, seen from the side at 2 m high, 8 m away.</summary>
    private static (Scene Scene, LightItem Spot) Beam()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 2, -8));
        scene.AddObject("Floor", Doc(Square(Vec3.Zero, 20, Vec3.UnitY)));
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.SetPosition(0, new Vec3(0, 4, 0));
        spot.SetRotation(0, new Vec3(0, 90, 0));
        spot.ConeAngle = 20;
        spot.EdgeAngle = 2;
        spot.Volumetric = new VolumetricLight { On = true, Intensity = 2, Samples = 32 };
        return (scene, spot);
    }

    [Fact]
    public void A_beam_shows_in_the_air_inside_the_cone_only()
    {
        var (scene, spot) = Beam();
        var settings = new RenderSettings { Width = 64, Height = 48 };
        var lit = Render(scene, 0, settings);
        spot.Volumetric = spot.Volumetric with { On = false };
        var plain = Render(scene, 0, settings);
        var inBeam = new Vec3(0, 2.5, 0);
        var beside = new Vec3(2.5, 2.5, 0);
        Assert.Equal(0, At(plain, scene, inBeam));
        Assert.True(At(lit, scene, inBeam) > 40, $"{At(lit, scene, inBeam)}");
        Assert.Equal(At(plain, scene, beside), At(lit, scene, beside));
        // Alpha is the geometry's: the beam over the backdrop leaves it transparent.
        var (x, y) = PixelOf(scene, inBeam, 64, 48);
        Assert.Equal(0, lit.Pixel(x, y).A);
    }

    [Fact]
    public void A_shadowed_beam_has_a_shaft_cut_out_under_an_object()
    {
        var (scene, spot) = Beam();
        var settings = new RenderSettings { Width = 64, Height = 48 };
        var below = new Vec3(0, 1.6, 0);
        int open = At(Render(scene, 0, settings), scene, below);
        scene.AddObject("Blocker", Doc(Square(new Vec3(0, 3, 0), 0.8, -Vec3.UnitY)));
        int shadowed = At(Render(scene, 0, settings), scene, below);
        Assert.True(shadowed < open * 0.6, $"Open {open}, under the blocker {shadowed}.");
        spot.Volumetric = spot.Volumetric with { Shadows = false };
        Assert.Equal(open, At(Render(scene, 0, settings), scene, below));
        // A shadow map shadows the beam too, without shadow rays.
        spot.Volumetric = spot.Volumetric with { Shadows = true };
        spot.ShadowMap = new ShadowMapSettings { On = true };
        var mapped = new RenderSettings { Width = 64, Height = 48, Counters = new RenderCounters() };
        Assert.True(At(Render(scene, 0, mapped), scene, below) < open * 0.6);
        Assert.Equal(0, mapped.Counters!.ShadowRays);
    }

    [Fact]
    public void Few_samples_give_about_the_same_beam_as_many_and_fog_dims_it()
    {
        var (scene, spot) = Beam();
        var settings = new RenderSettings { Width = 64, Height = 48 };
        double Mean(RenderImage i) => Enumerable.Range(0, 64 * 48).Average(p => Brightness(i.Pixel(p % 64, p / 64)));
        spot.Volumetric = spot.Volumetric with { Samples = 128 };
        double many = Mean(Render(scene, 0, settings));
        spot.Volumetric = spot.Volumetric with { Samples = 8 };
        double few = Mean(Render(scene, 0, settings));
        Assert.InRange(few, many * 0.9, many * 1.1);

        int clear = At(Render(scene, 0, settings), scene, new Vec3(0, 2.5, 0));
        scene.Fog = FogType.Linear;
        scene.FogMinDistance = 0;
        scene.FogMaxDistance = 16;
        scene.FogColor = Vec3.Zero;
        int foggy = At(Render(scene, 0, settings), scene, new Vec3(0, 2.5, 0));
        Assert.InRange(foggy, 1, clear - 1);
    }

    [Fact]
    public void A_point_lights_beam_is_a_glow_round_it_that_its_range_bounds()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -6));
        var bulb = scene.AddLight("Bulb", LightType.Point);
        bulb.Range = 2;
        bulb.Volumetric = new VolumetricLight { On = true, Intensity = 5 };
        var image = Render(scene, 0, Small);
        Assert.True(At(image, scene, Vec3.Zero) > 50);
        Assert.Equal(0, At(image, scene, new Vec3(2.2, 0, 0)));
        Assert.True(At(image, scene, Vec3.Zero) > At(image, scene, new Vec3(1.2, 0, 0)));
    }

    // ───────────────────────────── all of them ─────────────────────────────

    [Fact]
    public void The_light_effects_render_the_same_on_one_thread_or_many()
    {
        var (scene, spot) = Beam();
        scene.AddObject("Blocker", Doc(Square(new Vec3(0, 3, 0), 0.8, -Vec3.UnitY)));
        spot.ShadowMap = new ShadowMapSettings { On = true, Fuzziness = 2 };
        var sun = scene.AddLight("Sun");
        sun.SetRotation(0, new Vec3(180, -10, 0));
        sun.Flare = new LensFlare { On = true, Ring = true, Streaks = 7 };
        sun.Volumetric = new VolumetricLight { On = true, Samples = 8, Distance = 12 };
        RenderImage With(int threads) => Render(scene, 0, new RenderSettings { Width = 48, Height = 36, SamplesPerPixel = 2, MaxThreads = threads });
        Assert.Equal(With(1).Rgba, With(8).Rgba);
    }

    [Fact]
    public void Busy_scene_with_a_flare_at_no_intensity_renders_as_it_did()
    {
        var scene = EffectsBaselineTests.Busy();
        var plain = EffectsBaselineTests.Hash(Render(scene, 0, new RenderSettings { Width = 64, Height = 48 }));
        // The flare keeps the picture in full precision until it's drawn: with nothing drawn, the bytes are the same.
        var point = scene.Lights.First(l => l.Type == LightType.Point);
        point.Flare = new LensFlare { On = true };
        point.FlareIntensity.SetConstant(0);
        Assert.Equal(plain, EffectsBaselineTests.Hash(Render(scene, 0, new RenderSettings { Width = 64, Height = 48 })));
    }

    [Fact]
    public void The_records_check_their_settings()
    {
        Assert.Contains("streaks", Assert.Throws<ArgumentException>(() => new LensFlare { Streaks = 65 }.Checked()).Message);
        Assert.Contains("size", Assert.Throws<ArgumentException>(() => new LensFlare { Size = 0 }.Checked()).Message);
        Assert.Contains("dissolve", Assert.Throws<ArgumentException>(() => new LensFlare { Dissolve = 2 }.Checked()).Message);
        Assert.Contains("nominal", Assert.Throws<ArgumentException>(() => new LensFlare { NominalDistance = 0 }.Checked()).Message);
        Assert.Contains("1 to 256", Assert.Throws<ArgumentException>(() => new VolumetricLight { Samples = 0 }.Checked()).Message);
        Assert.Contains("distance", Assert.Throws<ArgumentException>(() => new VolumetricLight { Distance = -1 }.Checked()).Message);
        Assert.Contains("64 to 4096", Assert.Throws<ArgumentException>(() => new ShadowMapSettings { Size = 32 }.Checked()).Message);
        Assert.Contains("fuzziness", Assert.Throws<ArgumentException>(() => new ShadowMapSettings { Fuzziness = -1 }.Checked()).Message);
        new LensFlare().Checked();
        new VolumetricLight().Checked();
        new ShadowMapSettings().Checked();
    }
}
