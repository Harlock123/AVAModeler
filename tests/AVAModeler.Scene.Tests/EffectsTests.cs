using System.Diagnostics;
using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>
/// The render effects, each switched on: ambient occlusion, soft (area and sized) lights, the environment picture,
/// depth of field and motion blur. Each test renders small pictures and measures what the effect should do.
/// </summary>
public class EffectsTests
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

    // ───────────────────────────── ambient occlusion ─────────────────────────────

    /// <summary>A floor meeting a wall, lit by ambient light alone, seen from in front and above.</summary>
    private static Scene Corner()
    {
        var scene = new Scene { AmbientIntensity = 1 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 2, -5));
        cam.SetRotation(0, new Vec3(0, 25, 0));
        scene.AddObject("Floor", Doc(Square(new Vec3(0, 0, 0), 12, Vec3.UnitY)));
        scene.AddObject("Wall", Doc(Square(new Vec3(0, 0, 1), 12, -Vec3.UnitZ)));
        return scene;
    }

    [Fact]
    public void Ambient_occlusion_darkens_the_corner_but_not_the_open_floor()
    {
        var scene = Corner();
        var settings = new RenderSettings { Width = 64, Height = 48 };
        var corner = new Vec3(0, 0, 0.92);
        var open = new Vec3(0, 0, -2.5);
        var plain = Render(scene, 0, settings);
        Assert.Equal(At(plain, scene, open), At(plain, scene, corner));

        scene.Occlusion = new AmbientOcclusion { On = true, Samples = 64, Distance = 1 };
        var shaded = Render(scene, 0, settings);
        int inCorner = At(shaded, scene, corner), onFloor = At(shaded, scene, open);
        Assert.True(inCorner < onFloor * 0.7, $"Corner {inCorner}, open floor {onFloor}.");
        // Nothing is within a metre of the open floor, so it's as bright as without occlusion.
        Assert.Equal(At(plain, scene, open), onFloor);

        // Half the strength takes away half as much.
        scene.Occlusion = scene.Occlusion with { Strength = 0.5 };
        int half = At(Render(scene, 0, settings), scene, corner);
        Assert.InRange(half, inCorner + 1, onFloor - 1);
    }

    // ───────────────────────────── soft shadows ─────────────────────────────

    /// <summary>A square blocker a metre over the ground, a light 4 m up shining down, and a camera looking straight down.</summary>
    private static (Scene Scene, LightItem Light) Shadow(LightType type)
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Top");
        cam.SetPosition(0, new Vec3(0, 6, 0));
        cam.SetRotation(0, new Vec3(0, 90, 0));
        scene.AddObject("Ground", Doc(Square(Vec3.Zero, 20, Vec3.UnitY)));
        // Facing down: the shadow rays from the ground hit it, and from above it's invisible, so the shadow shows whole.
        scene.AddObject("Blocker", Doc(Square(new Vec3(0, 1, 0), 1, -Vec3.UnitY)));
        var light = scene.AddLight("Light", type);
        light.SetPosition(0, new Vec3(0, 4, 0));
        light.SetRotation(0, new Vec3(0, 90, 0));
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

    [Fact]
    public void A_point_lights_shadow_is_hard_and_an_area_lights_has_a_penumbra()
    {
        var settings = new RenderSettings { Width = 64, Height = 64 };
        var (pointScene, _) = Shadow(LightType.Point);
        var hard = Row(Render(pointScene, 0, settings));
        Assert.True(hard[0] < 10 && hard.Max() > 200, string.Join(",", hard));
        Assert.True(Penumbra(hard) <= 1, string.Join(",", hard));

        var (areaScene, area) = Shadow(LightType.Area);
        area.AreaWidth = area.AreaHeight = 2;
        area.ShadowSamples = 64;
        var soft = Row(Render(areaScene, 0, settings));
        Assert.True(Penumbra(soft) >= 5, string.Join(",", soft));
        // Across the penumbra the light rises steadily from the shadow outward.
        int start = Array.FindIndex(soft, v => v > soft[0] + 20);
        Assert.True(start > 0 && soft[start + 3] > soft[start], string.Join(",", soft));
    }

    [Fact]
    public void A_point_light_with_a_radius_casts_a_soft_shadow_and_a_sharper_one_when_smaller()
    {
        var settings = new RenderSettings { Width = 64, Height = 64 };
        var (scene, light) = Shadow(LightType.Point);
        light.ShadowSamples = 64;
        light.Radius = 1;
        int wide = Penumbra(Row(Render(scene, 0, settings)));
        light.Radius = 0.2;
        int narrow = Penumbra(Row(Render(scene, 0, settings)));
        Assert.True(wide >= 4 && narrow < wide, $"Radius 1: {wide} px, radius 0.2: {narrow} px.");
        // A spot light with a radius is soft too.
        light.Type = LightType.Spot;
        light.ConeAngle = 80;
        light.Radius = 1;
        Assert.True(Penumbra(Row(Render(scene, 0, settings))) >= 4);
    }

    [Fact]
    public void An_area_light_shines_from_its_front_only()
    {
        var (scene, light) = Shadow(LightType.Area);
        var settings = new RenderSettings { Width = 32, Height = 32 };
        Assert.True(Brightness(Render(scene, 0, settings).Pixel(2, 2)) > 100);
        light.SetRotation(0, new Vec3(0, -90, 0)); // turned to face up
        Assert.Equal(0, Brightness(Render(scene, 0, settings).Pixel(2, 2)));
    }

    // ───────────────────────────── the environment ─────────────────────────────

    /// <summary>
    /// A 16 × 8 environment: the middle four columns (ahead, +Z) red, the top two rows (overhead) white, the rest dark
    /// green; written as a Radiance file in a folder.
    /// </summary>
    private static string Environment(string folder)
    {
        int w = 16, h = 8;
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = y < 2 ? new Vec3(1, 1, 1) : x is >= 6 and < 10 ? new Vec3(0.75, 0.25, 0.125) : new Vec3(0, 0.25, 0);
                int i = (y * w + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = ((float)c.X, (float)c.Y, (float)c.Z);
            }
        string path = Path.Combine(folder, "env.hdr");
        File.WriteAllBytes(path, Hdr.Write(new HdrImage(w, h, rgb)));
        return path;
    }

    private static Scene Looking(Vec3 rotation)
    {
        var scene = new Scene();
        var cam = scene.AddCamera("Camera");
        cam.SetRotation(0, rotation);
        cam.Width = cam.Height = 9;
        return scene;
    }

    [Fact]
    public void The_environment_is_the_backdrop_in_every_direction()
    {
        using var dir = new TempDir();
        string env = Environment(dir.Path);
        var ahead = Looking(Vec3.Zero);
        ahead.Environment = new EnvironmentSettings { Image = env };
        var image = Render(ahead, 0);
        // The pixel straight ahead is the red, as 0.75, 0.25, 0.125 maps to bytes; alpha stays 0 (nothing was hit).
        Assert.Equal(((byte)191, (byte)64, (byte)32, (byte)0), image.Pixel(4, 4));
        // Looking straight up: white. Looking left (-X): green.
        Assert.Equal((255, 255, 255), Rgb(Render(Up(env), 0).Pixel(4, 4)));
        var left = Looking(new Vec3(-90, 0, 0));
        left.Environment = new EnvironmentSettings { Image = env };
        Assert.Equal((0, 64, 0), Rgb(Render(left, 0).Pixel(4, 4)));
        // Turned by 90° heading, what was ahead is now off to the right (+X), and intensity scales it.
        var right = Looking(new Vec3(90, 0, 0));
        right.Environment = new EnvironmentSettings { Image = env, Heading = 90, Intensity = 0.5 };
        Assert.Equal((96, 32, 16), Rgb(Render(right, 0).Pixel(4, 4)));
    }

    private static Scene Up(string env)
    {
        var s = Looking(new Vec3(0, -90, 0));
        s.Environment = new EnvironmentSettings { Image = env };
        return s;
    }

    private static (int, int, int) Rgb((byte R, byte G, byte B, byte A) p) => (p.R, p.G, p.B);

    [Fact]
    public void The_environment_shows_in_reflections()
    {
        using var dir = new TempDir();
        var scene = Looking(Vec3.Zero);
        scene.Environment = new EnvironmentSettings { Image = Environment(dir.Path) };
        // A mirror ahead sends the view back behind the camera, where the environment is green.
        scene.AddObject("Mirror", Doc(Square(new Vec3(0, 0, 2), 3, -Vec3.UnitZ), s => { s.Diffuse = 0; s.Reflection = 1; }));
        Assert.Equal(((byte)0, (byte)64, (byte)0, (byte)255), Render(scene, 0).Pixel(4, 4));
    }

    [Fact]
    public void Environment_lighting_lights_the_side_facing_the_bright_part()
    {
        using var dir = new TempDir();
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = cam.Height = 32;
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1, 32, 16), s => s.Smoothing = true));
        // White overhead, dark below.
        int w = 32, h = 16;
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w; x++)
                rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = 1;
        string env = Path.Combine(dir.Path, "half.hdr");
        File.WriteAllBytes(env, Hdr.Write(new HdrImage(w, h, rgb)));
        scene.Environment = new EnvironmentSettings { Image = env, Samples = 32 };
        // Seen but not lighting: with no lights and no ambient the ball is black.
        var unlit = Render(scene, 0);
        Assert.Equal(0, Brightness(unlit.Pixel(16, 16)));

        scene.Environment = scene.Environment with { Lighting = true };
        var lit = Render(scene, 0);
        int top = At(lit, scene, new Vec3(0, 0.7, -0.7)), bottom = At(lit, scene, new Vec3(0, -0.7, -0.7)), middle = Brightness(lit.Pixel(16, 16));
        Assert.True(top > bottom + 150, $"Top {top}, bottom {bottom}.");
        // Facing the horizon it sees half the bright sky: about half as bright as the top.
        Assert.InRange(middle, top / 4, top);
        // Brighter environment, brighter ball.
        scene.Environment = scene.Environment with { Intensity = 0.5 };
        Assert.True(Brightness(Render(scene, 0).Pixel(16, 16)) < middle);
    }

    [Fact]
    public void Built_in_environments_need_no_file_and_a_missing_one_is_a_warning()
    {
        var scene = Looking(new Vec3(0, -30, 0));
        scene.Environment = new EnvironmentSettings { Image = "builtin:sky" };
        var sky = Render(scene, 0).Pixel(4, 4);
        Assert.True(sky.B > sky.R, $"Sky {sky}.");
        scene.Environment = new EnvironmentSettings { Image = "builtin:studio" };
        Assert.NotEqual(sky, Render(scene, 0).Pixel(4, 4));
        Assert.Empty(scene.Evaluate(0).Warnings);

        scene.Environment = new EnvironmentSettings { Image = "/no/such/place/env.hdr" };
        scene.BackgroundColor = new Vec3(0, 0, 1);
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)0), Render(scene, 0).Pixel(4, 4));
        Assert.Contains(scene.Evaluate(0).Warnings, w => w.Contains("Can't find the environment image"));
        scene.Environment = new EnvironmentSettings { Image = "builtin:moon" };
        Assert.Contains(scene.Evaluate(0).Warnings, w => w.Contains("builtin:sky"));
    }

    // ───────────────────────────── depth of field ─────────────────────────────

    /// <summary>Two glowing cards, one 3 m away and one 9 m away, on black; a camera with depth of field focused on the near one.</summary>
    private static Scene Cards()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.Width = 96;
        cam.Height = 48;
        void Card(string name, Vec3 at, double size) =>
            scene.AddObject(name, Doc(Square(at, size, -Vec3.UnitZ), s => { s.Luminosity = 1; s.Diffuse = 0; s.Color = new Rgb(255, 255, 255); }));
        Card("Near", new Vec3(-0.45, 0, 3), 0.6);
        Card("Far", new Vec3(1.35, 0, 9), 1.8);
        cam.DepthOfField = new DepthOfField { On = true, FocalDistance = 3, FStop = 0.05, Samples = 32 };
        return scene;
    }

    /// <summary>The sharpest step between neighbouring pixels along the middle row, within a span of columns.</summary>
    private static int EdgeContrast(RenderImage image, int from, int to)
    {
        int best = 0;
        for (int x = from; x < to; x++) best = Math.Max(best, Math.Abs(Brightness(image.Pixel(x, 24)) - Brightness(image.Pixel(x + 1, 24))));
        return best;
    }

    [Fact]
    public void Depth_of_field_keeps_the_focused_card_sharp_and_blurs_the_other()
    {
        var scene = Cards();
        var image = Render(scene, 0);
        int near = EdgeContrast(image, 2, 47), far = EdgeContrast(image, 48, 94);
        Assert.True(near > 600, $"The card in focus has an edge of {near}.");
        Assert.True(far < near / 2, $"In focus {near}, out of focus {far}.");
        // Focused on the far card instead, the near one blurs.
        scene.CurrentCamera!.DepthOfField = scene.CurrentCamera.DepthOfField with { FocalDistance = 9 };
        var refocused = Render(scene, 0);
        Assert.True(EdgeContrast(refocused, 48, 94) > EdgeContrast(refocused, 2, 47) * 2);
        // Without depth of field both are sharp.
        scene.CurrentCamera.DepthOfField = scene.CurrentCamera.DepthOfField with { On = false };
        var plain = Render(scene, 0);
        Assert.True(EdgeContrast(plain, 48, 94) > 600 && EdgeContrast(plain, 2, 47) > 600);
    }

    [Fact]
    public void The_aperture_follows_lightwaves_lens()
    {
        var dof = new DepthOfField { FStop = 4 };
        // Zoom 3.2 is a 24 mm lens: at f/4 the opening is 6 mm across.
        Assert.Equal(0.003, dof.ApertureRadius(3.2), 12);
        Assert.Equal(4, DepthOfField.FStopFor(0.003, 3.2), 9);
        Assert.Equal(4.0, new CameraItem("c").DepthOf(new Vec3(0, 1, 4), 0), 12);
    }

    // ───────────────────────────── motion blur ─────────────────────────────

    /// <summary>A glowing red ball sweeping from left to right over frames 0 to 10, a glowing green one standing still, on black.</summary>
    private static Scene Moving()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -8));
        cam.Width = 96;
        cam.Height = 48;
        var moving = scene.AddObject("Moving", Doc(Sphere(Vec3.Zero, 0.4), s => { s.Luminosity = 1; s.Diffuse = 0; s.Color = new Rgb(255, 0, 0); }));
        moving.SetPosition(0, new Vec3(-4, 0.8, 0));
        moving.SetPosition(10, new Vec3(4, 0.8, 0));
        var still = scene.AddObject("Still", Doc(Sphere(Vec3.Zero, 0.4), s => { s.Luminosity = 1; s.Diffuse = 0; s.Color = new Rgb(0, 255, 0); }));
        still.SetPosition(0, new Vec3(0, -0.8, 0));
        return scene;
    }

    /// <summary>How many pixels along a row are partly covered (a channel between 10% and 90%).</summary>
    private static int Smeared(RenderImage image, int y, Func<(byte R, byte G, byte B, byte A), byte> channel) =>
        Enumerable.Range(0, image.Width).Count(x => channel(image.Pixel(x, y)) is > 25 and < 230);

    private static int Covered(RenderImage image, int y, Func<(byte R, byte G, byte B, byte A), byte> channel) =>
        Enumerable.Range(0, image.Width).Count(x => channel(image.Pixel(x, y)) > 25);

    [Fact]
    public void Motion_blur_smears_what_moves_along_its_path_and_leaves_still_things_sharp()
    {
        var scene = Moving();
        var cam = scene.CurrentCamera!;
        var (_, movingRow) = PixelOf(scene, new Vec3(0, 0.8, 0), 96, 48);
        var (_, stillRow) = PixelOf(scene, new Vec3(0, -0.8, 0), 96, 48);
        var sharp = Render(scene, 5, new RenderSettings { SamplesPerPixel = 16 });
        cam.MotionBlur = new MotionBlur { On = true, Shutter = 1, Samples = 16 };
        var blurred = Render(scene, 5);

        // The moving ball covers far more of its row, as a streak with soft ends, and the streak lies along its path:
        // a frame's travel is 0.8 m either way of the middle.
        Assert.True(Covered(blurred, movingRow, p => p.R) > Covered(sharp, movingRow, p => p.R) + 5);
        Assert.True(Smeared(blurred, movingRow, p => p.R) > Smeared(sharp, movingRow, p => p.R) + 5);
        Assert.Equal(0, blurred.Pixel(48, PixelOf(scene, new Vec3(0, 2, 0), 96, 48).Y).R);
        // The still ball is as sharp as it was.
        Assert.InRange(Covered(blurred, stillRow, p => p.G), Covered(sharp, stillRow, p => p.G) - 1, Covered(sharp, stillRow, p => p.G) + 1);
        Assert.True(Smeared(blurred, stillRow, p => p.G) <= Smeared(sharp, stillRow, p => p.G) + 1);
    }

    [Fact]
    public void Motion_blur_follows_deformation_and_the_camera_too()
    {
        // A camera panning across a still ball blurs it.
        var scene = Moving();
        scene.Remove(scene.Items.Single(i => i.Name == "Moving"));
        var cam = scene.CurrentCamera!;
        cam.Channels[0].AddKey(0, -2);
        cam.Channels[0].AddKey(10, 2);
        var (_, row) = PixelOf(scene, new Vec3(0, -0.8, 0), 96, 48, 5);
        var sharp = Render(scene, 5, new RenderSettings { SamplesPerPixel = 16 });
        cam.MotionBlur = new MotionBlur { On = true, Shutter = 1, Samples = 16 };
        Assert.True(Smeared(Render(scene, 5), row, p => p.G) > Smeared(sharp, row, p => p.G) + 3);
    }

    // ───────────────────────────── together ─────────────────────────────

    /// <summary>Every effect on at once, small.</summary>
    internal static Scene Everything()
    {
        var scene = EffectsBaselineTests.Busy();
        scene.Occlusion = new AmbientOcclusion { On = true, Samples = 8, Distance = 0.8 };
        scene.Environment = new EnvironmentSettings { Image = "builtin:studio", Lighting = true, Samples = 8, Heading = 30 };
        var spot = (LightItem)scene.Find("Spot")!;
        spot.Radius = 0.3;
        spot.ShadowSamples = 8;
        var area = scene.AddLight("Area", LightType.Area);
        area.SetPosition(0, new Vec3(0, 3, -1));
        area.SetRotation(0, new Vec3(0, 70, 0));
        area.AreaWidth = 2;
        area.ShadowSamples = 8;
        area.Intensity.SetConstant(0.6);
        var cam = scene.CurrentCamera!;
        cam.DepthOfField = new DepthOfField { On = true, FocalDistance = 5, FStop = 0.3, Samples = 8 };
        cam.MotionBlur = new MotionBlur { On = true, Shutter = 0.5, Samples = 8 };
        return scene;
    }

    [Fact]
    public void Renders_with_every_effect_are_the_same_every_time_on_any_threads()
    {
        var scene = Everything();
        var settings = new RenderSettings { Width = 40, Height = 30, SamplesPerPixel = 4, RefractionIndices = { ["Glass"] = 1.4 } };
        var a = Render(scene, 5, settings);
        var b = Render(scene, 5, new RenderSettings { Width = 40, Height = 30, SamplesPerPixel = 4, MaxThreads = 1, RefractionIndices = { ["Glass"] = 1.4 } });
        Assert.Equal(a.Rgba, b.Rgba);
        // A copy of the scene (as Render Animation and agents render) renders the same too.
        Assert.Equal(a.Rgba, Render(scene.Copy(), 5, settings).Rgba);
        Assert.NotEqual(EffectsBaselineTests.Hash(a), EffectsBaselineTests.Hash(Render(EffectsBaselineTests.Busy(), 5, settings)));
    }

    [Fact]
    public void Effect_samples_are_per_pixel_and_shared_among_its_rays()
    {
        var cam = new CameraItem("c");
        Assert.Equal(4, RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = 4 }, cam));
        cam.DepthOfField = new DepthOfField { On = true, Samples = 9 };
        Assert.Equal(9, RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = 4 }, cam));
        cam.MotionBlur = new MotionBlur { On = true, Samples = 25 };
        Assert.Equal(25, RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = 4 }, cam));
        Assert.Equal(36, RayTracer.RaysPerPixel(new RenderSettings { SamplesPerPixel = 36 }, cam));
    }

    [Fact]
    public void Settings_out_of_range_are_refused_with_a_reason()
    {
        Assert.Contains("1 to 256", Assert.Throws<ArgumentException>(() => new AmbientOcclusion { Samples = 0 }.Checked()).Message);
        Assert.Contains("metres", Assert.Throws<ArgumentException>(() => new AmbientOcclusion { Distance = 0 }.Checked()).Message);
        Assert.Throws<ArgumentException>(() => new AmbientOcclusion { Strength = 2 }.Checked());
        Assert.Throws<ArgumentException>(() => new EnvironmentSettings { Intensity = -1 }.Checked());
        Assert.Throws<ArgumentException>(() => new DepthOfField { FStop = 0 }.Checked());
        Assert.Throws<ArgumentException>(() => new DepthOfField { FocalDistance = -1 }.Checked());
        Assert.Throws<ArgumentException>(() => new MotionBlur { Shutter = 1.5 }.Checked());
        var ok = new MotionBlur { Shutter = 1 };
        Assert.Same(ok, ok.Checked());
    }

    [Fact]
    public void The_showcase_scene_renders_in_reasonable_time()
    {
        // Not a benchmark: a guard that the effects stay usable. Every effect on, 40 × 30, 8 rays a pixel.
        var scene = Everything();
        var clock = Stopwatch.StartNew();
        Render(scene, 5, new RenderSettings { Width = 40, Height = 30, SamplesPerPixel = 1 });
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed.TotalSeconds:0.0} s.");
    }
}
