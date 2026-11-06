using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>
/// Object dissolve in the ray tracer, the scene's image list (stills and numbered sequences), sequence textures,
/// compositing (background and foreground images, colour and alpha keys), and how they're kept: copies, snapshots,
/// .avascene and .lws.
/// </summary>
public class DissolveCompositingTests
{
    private static RenderImage Render(Scene scene, double frame = 1, int samples = 1) =>
        RayTracer.Render(scene, frame, new RenderSettings { SamplesPerPixel = samples }, null, TestContext.Current.CancellationToken);

    private static (int, int, int) Rgb((byte R, byte G, byte B, byte A) p) => (p.R, p.G, p.B);

    private static void Near((int R, int G, int B) want, (byte R, byte G, byte B, byte A) got, int tolerance = 2) =>
        Assert.True(Math.Abs(want.R - got.R) <= tolerance && Math.Abs(want.G - got.G) <= tolerance && Math.Abs(want.B - got.B) <= tolerance,
            $"wanted {want}, got {Rgb(got)}");

    /// <summary>A surface that shows its colour as it is: fully luminous, not lit.</summary>
    private static Action<Surface> Flat(byte r, byte g, byte b) => s =>
    {
        s.Color = new Rgb(r, g, b);
        s.Diffuse = 0;
        s.Specular = 0;
        s.Luminosity = 1;
    };

    /// <summary>A camera at z = -4 looking down +Z at 80 × 80 (32 pixels a metre at z = 0), a blue backdrop, no ambient light.</summary>
    private static Scene Stage()
    {
        var scene = new Scene { AmbientIntensity = 0, BackgroundColor = new Vec3(0, 0, 1) };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 80;
        cam.Height = 80;
        return scene;
    }

    /// <summary>A flat-coloured 2 × 2 square facing the camera at a depth.</summary>
    private static ObjectItem Wall(Scene scene, string name, double z, byte r, byte g, byte b, double size = 2) =>
        scene.AddObject(name, Doc(Square(new Vec3(0, 0, z), size, -Vec3.UnitZ), Flat(r, g, b)));

    /// <summary>Writes a PNG of w × h pixels from a function of the pixel (red, green, blue, alpha).</summary>
    private static string Picture(string folder, string name, int w, int h, Func<int, int, (byte, byte, byte, byte)> f)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
            }
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Png.Encode(w, h, rgba));
        return path;
    }

    private static string Solid(string folder, string name, byte r, byte g, byte b) => Picture(folder, name, 4, 4, (_, _) => (r, g, b, 255));

    // ───────────────────────────── dissolve ─────────────────────────────

    [Fact]
    public void A_half_dissolved_object_shows_half_of_what_is_behind_it_and_half_covers_the_alpha()
    {
        var scene = Stage();
        var wall = Wall(scene, "Wall", 0, 255, 0, 0);
        var solid = Render(scene);
        Assert.Equal((255, 0, 0), Rgb(solid.Pixel(40, 40)));
        Assert.Equal(255, solid.Pixel(40, 40).A);

        wall.Dissolve.SetConstant(0.5);
        var half = Render(scene);
        // Half red, half the blue backdrop; the alpha is half covered. Off the wall nothing changes.
        Near((128, 0, 128), half.Pixel(40, 40));
        Assert.InRange(half.Pixel(40, 40).A, 127, 128);
        Assert.Equal(solid.Pixel(5, 5), half.Pixel(5, 5));

        // In front of a solid green wall the pixel is covered fully, half red and half green.
        Wall(scene, "Behind", 1, 0, 255, 0, 4);
        var front = Render(scene);
        Near((128, 128, 0), front.Pixel(40, 40));
        Assert.Equal(255, front.Pixel(40, 40).A);
    }

    [Fact]
    public void A_dissolve_of_zero_keyed_or_constant_renders_exactly_as_before()
    {
        var scene = Stage();
        var wall = Wall(scene, "Wall", 0, 200, 100, 50);
        var before = Render(scene, 1, 4).Rgba;
        wall.Dissolve.AddKey(1, 0);
        wall.Dissolve.AddKey(10, 0);
        Assert.Equal(before, Render(scene, 1, 4).Rgba);
    }

    [Fact]
    public void A_wholly_dissolved_object_is_left_out_and_casts_no_shadow()
    {
        var (scene, floorPoint, _) = ShadowStage(out var blocker);
        blocker.Dissolve.SetConstant(1);
        var gone = Render(scene);
        scene.Remove(blocker);
        Assert.Equal(Render(scene).Rgba, gone.Rgba);
        Assert.True(gone.Pixel(floorPoint.X, floorPoint.Y).R > 100);
    }

    /// <summary>A white floor lit from above at 45° by a distant light, a blocker a metre over it, seen from straight above.</summary>
    private static (Scene Scene, (int X, int Y) Shadowed, (int X, int Y) Lit) ShadowStage(out ObjectItem blocker)
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Top");
        cam.SetPosition(0, new Vec3(0, 6, 0));
        cam.SetRotation(0, new Vec3(0, 90, 0));
        cam.Zoom.SetConstant(1);
        cam.Width = 80;
        cam.Height = 80;
        var light = scene.AddLight("Sun");
        light.SetRotation(0, new Vec3(0, 45, 0));
        scene.AddObject("Floor", Doc(Square(Vec3.Zero, 12, Vec3.UnitY), s => { s.Color = new Rgb(255, 255, 255); s.Diffuse = 1; s.Specular = 0; }));
        // It faces down, toward the floor (a face is hit from its front only), so the camera above sees through it.
        blocker = scene.AddObject("Blocker", Doc(Square(new Vec3(0, 1, 0), 1, -Vec3.UnitY), Flat(0, 0, 0)));
        // The shadow falls a metre toward +Z (z from 0.5 to 1.5); the camera sees the floor through the blocker's back.
        var shadowed = PixelOf(scene, new Vec3(0, 0, 1.1));
        var lit = PixelOf(scene, new Vec3(0, 0, -2));
        return (scene, shadowed, lit);
    }

    /// <summary>Where a world point lands in the 80 × 80 picture of the scene's camera.</summary>
    private static (int X, int Y) PixelOf(Scene scene, Vec3 p)
    {
        var cam = scene.CurrentCamera!;
        var local = cam.CameraToWorld(1).Inverted().TransformPoint(p);
        double tanV = 1 / cam.Zoom.Evaluate(1), tanH = tanV * cam.Width * cam.PixelAspect / cam.Height;
        return ((int)((local.X / local.Z / tanH + 1) / 2 * cam.Width), (int)((1 - local.Y / local.Z / tanV) / 2 * cam.Height));
    }

    [Fact]
    public void A_half_dissolved_blocker_lets_half_the_light_through_its_shadow()
    {
        var (scene, shadowed, lit) = ShadowStage(out var blocker);
        var solid = Render(scene);
        int full = solid.Pixel(lit.X, lit.Y).R;
        Assert.True(full > 100, $"the lit floor is {full}");
        Assert.Equal(0, solid.Pixel(shadowed.X, shadowed.Y).R);

        blocker.Dissolve.SetConstant(0.5);
        var half = Render(scene);
        Assert.InRange(half.Pixel(shadowed.X, shadowed.Y).R, full / 2 - 2, full / 2 + 2);
        Assert.Equal(full, half.Pixel(lit.X, lit.Y).R);
    }

    [Fact]
    public void Dissolve_is_a_channel_copied_snapshotted_and_kept_in_files()
    {
        using var dir = new TempDir();
        var scene = Stage();
        var wall = Wall(scene, "Wall", 0, 255, 0, 0);
        wall.Dissolve.SetKey(0, new EnvelopeKey(1, 0));
        wall.Dissolve.AddKey(new EnvelopeKey(30, 1, Linear: true));
        Assert.Equal(0.5, wall.DissolveAt(15.5), 6);
        Assert.Equal(1, wall.DissolveAt(100));

        var copy = (ObjectItem)scene.Copy().Find("Wall")!;
        Assert.Equal(wall.Dissolve.Keys, copy.Dissolve.Keys);

        var snap = scene.Snapshot();
        wall.Dissolve.SetConstant(0.25);
        snap.Restore();
        Assert.Equal(2, wall.Dissolve.Keys.Count);

        var json = (ObjectItem)SceneJson.Read(SceneJson.Write(scene, embedMeshes: true)).Scene.Find("Wall")!;
        Assert.Equal(wall.Dissolve.Keys, json.Dissolve.Keys);
        // A solid object writes no dissolve, so other files are as they were.
        var plain = Stage();
        Wall(plain, "Wall", 0, 1, 2, 3);
        Assert.DoesNotContain("dissolve", SceneJson.Write(plain, embedMeshes: true));

        var warnings = new List<string>();
        string lws = LwsWriter.Write(scene, warnings);
        Assert.Contains("ObjectDissolve (envelope)", lws);
        var read = LwsReader.Read(lws, _ => new Document(), dir.Path).Scene;
        var back = read.Objects.Single();
        Assert.Equal(2, back.Dissolve.Keys.Count);
        Assert.Equal(1, back.Dissolve.Keys[1].Value, 9);
        Assert.Equal(30, back.Dissolve.Keys[1].Frame, 6);
        Assert.DoesNotContain("Dissolve", LwsWriter.Write(plain));
    }

    [Fact]
    public void LightWave_5s_ObjDissolve_is_read_as_a_value_or_an_envelope()
    {
        const string text = """
            LWSC
            1

            FirstFrame 1
            LastFrame 30
            LoadObject Objects/A.lwo
            ObjDissolve 0.4
            LoadObject Objects/B.lwo
            ObjDissolve (envelope)
              1
              2
              0
              0 0 0 0 0
              1
              20 0 0 0 0
            """;
        var scene = LwsReader.Read(text, _ => new Document(), "/no/such").Scene;
        var objects = scene.Objects.ToList();
        Assert.Equal(0.4, objects[0].DissolveAt(5), 9);
        Assert.Equal(0, objects[1].DissolveAt(0), 9);
        Assert.Equal(1, objects[1].DissolveAt(20), 9);
    }

    // ───────────────────────────── image sequences ─────────────────────────────

    [Fact]
    public void A_sequence_resolves_by_frame_and_offset_holding_or_looping_outside_its_files()
    {
        using var dir = new TempDir();
        for (int i = 1; i <= 3; i++) Solid(dir.Path, $"sky{i:0000}.png", (byte)(i * 60), 0, 0);
        string first = Path.Combine(dir.Path, "sky0001.png");
        var seq = new SceneImage { Path = first, Sequence = true }.Checked();
        string F(int n) => Path.Combine(dir.Path, $"sky{n:0000}.png");

        Assert.Equal((1, 3), ImageSequence.Range(seq));
        Assert.Equal(F(1), ImageSequence.Resolve(seq, 1));
        Assert.Equal(F(2), ImageSequence.Resolve(seq, 2));
        Assert.Equal(F(3), ImageSequence.Resolve(seq, 2.6));
        // Held at the first and last outside them.
        Assert.Equal(F(3), ImageSequence.Resolve(seq, 9));
        Assert.Equal(F(1), ImageSequence.Resolve(seq, -4));
        // An offset moves the numbers; looping wraps them round 1 to 3.
        Assert.Equal(F(3), ImageSequence.Resolve(seq with { Offset = 1 }, 2));
        var loop = seq with { Loop = true };
        Assert.Equal(F(1), ImageSequence.Resolve(loop, 4));
        Assert.Equal(F(2), ImageSequence.Resolve(loop, 5));
        Assert.Equal(F(3), ImageSequence.Resolve(loop, 0));
        // A still is its own file at every frame.
        Assert.Equal(first, ImageSequence.Resolve(seq with { Sequence = false }, 7));

        // A new file in the folder is found (the scan is kept until the folder changes).
        Solid(dir.Path, "sky0004.png", 0, 0, 0);
        Directory.SetLastWriteTimeUtc(dir.Path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal((1, 4), ImageSequence.Range(seq));
        Assert.Equal(F(4), ImageSequence.Resolve(seq, 4));

        // The digits' count is kept for a number with no file.
        Assert.Equal(("sky", 4, 12, ".png"), ImageSequence.Pattern("sky0012.png"));
        Assert.Null(ImageSequence.Pattern("sky.png"));
        Assert.Throws<ArgumentException>(() => new SceneImage { Path = "sky.png", Sequence = true }.Checked());
        var empty = new SceneImage { Path = Path.Combine(dir.Path, "none0001.png"), Sequence = true };
        Assert.Equal(Path.Combine(dir.Path, "none0007.png"), ImageSequence.Resolve(empty, 7));
        Assert.Contains("no frames", ImageSequence.Problem(empty));
    }

    [Fact]
    public void A_texture_whose_image_is_a_sequence_in_the_list_changes_with_the_frame()
    {
        using var dir = new TempDir();
        string red = Solid(dir.Path, "tex0001.png", 255, 0, 0);
        Solid(dir.Path, "tex0002.png", 0, 255, 0);
        var scene = Stage();
        var layer = new TextureLayer { Image = red, Size = new Vec3(2, 2, 2) };
        scene.AddObject("Wall", Doc(Square(Vec3.Zero, 2, -Vec3.UnitZ), s => { Flat(128, 128, 128)(s); s.Textures = [layer]; }));
        // Not in the list: the texture's own file every frame.
        Assert.Equal((255, 0, 0), Rgb(Render(scene, 2).Pixel(40, 40)));

        scene.Images = [new SceneImage { Path = red, Sequence = true }];
        Assert.Equal((255, 0, 0), Rgb(Render(scene, 1).Pixel(40, 40)));
        Assert.Equal((0, 255, 0), Rgb(Render(scene, 2).Pixel(40, 40)));
        Assert.Equal((0, 255, 0), Rgb(Render(scene, 8).Pixel(40, 40)));
        Assert.Equal(Path.Combine(dir.Path, "tex0002.png"), SceneImages.TextureFile(scene, red, null, 2));
    }

    // ───────────────────────────── compositing ─────────────────────────────

    [Fact]
    public void The_background_image_shows_where_camera_rays_miss_stretched_to_the_frame()
    {
        using var dir = new TempDir();
        string bg = Picture(dir.Path, "bg.png", 8, 8, (x, _) => x < 4 ? ((byte)255, (byte)255, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)255, (byte)255));
        var scene = Stage();
        Wall(scene, "Wall", 0, 255, 0, 0, 1);
        var before = Render(scene);
        scene.Images = [new SceneImage { Path = bg }];
        scene.Compositing = new CompositingSettings { Background = bg }.Checked();
        var img = Render(scene);
        Assert.Equal((255, 255, 0), Rgb(img.Pixel(5, 40)));
        Assert.Equal((0, 255, 255), Rgb(img.Pixel(75, 40)));
        // The object is as it was, and the alpha stays the objects' coverage.
        Assert.Equal(before.Pixel(40, 40), img.Pixel(40, 40));
        Assert.Equal(0, img.Pixel(5, 40).A);
        Assert.Empty(scene.Evaluate(1).Warnings);

        // Through a half-dissolved object the background shows too.
        scene.Objects.Single().Dissolve.SetConstant(0.5);
        Assert.True(Render(scene).Pixel(40, 40).G > 100);
    }

    [Fact]
    public void A_background_sequence_plays_a_file_a_frame()
    {
        using var dir = new TempDir();
        string one = Solid(dir.Path, "bg001.png", 255, 0, 0);
        Solid(dir.Path, "bg002.png", 0, 255, 0);
        var scene = Stage();
        scene.Images = [new SceneImage { Path = one, Sequence = true }];
        scene.Compositing = new CompositingSettings { Background = one };
        Assert.Equal((255, 0, 0), Rgb(Render(scene, 1).Pixel(10, 10)));
        Assert.Equal((0, 255, 0), Rgb(Render(scene, 2).Pixel(10, 10)));
    }

    [Fact]
    public void A_colour_keyed_foreground_is_laid_over_except_where_its_colour_is_keyed_out()
    {
        using var dir = new TempDir();
        // Left half green screen, right half white.
        string fg = Picture(dir.Path, "fg.png", 8, 8, (x, _) => x < 4 ? ((byte)0, (byte)250, (byte)10, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var scene = Stage();
        Wall(scene, "Wall", 0, 255, 0, 0, 1);
        var before = Render(scene);
        scene.Images = [new SceneImage { Path = fg }];
        scene.Compositing = new CompositingSettings
        {
            Foreground = fg, Key = ForegroundKey.Color, KeyLow = new Vec3(0, 0.9, 0), KeyHigh = new Vec3(0.1, 1, 0.1),
        }.Checked();
        var img = Render(scene);
        Assert.Equal(before.Pixel(30, 40), img.Pixel(30, 40));
        Assert.Equal(before.Pixel(2, 40), img.Pixel(2, 40));
        Assert.Equal((255, 255, 255), Rgb(img.Pixel(50, 40)));
        // Where it's laid the alpha is covered, the backdrop's included.
        Assert.Equal(255, img.Pixel(78, 2).A);
        Assert.Equal(0, img.Pixel(2, 2).A);

        // Unkeyed it covers everything; half dissolved it's half over.
        scene.Compositing = scene.Compositing with { Key = ForegroundKey.None };
        Assert.Equal((0, 250, 10), Rgb(Render(scene).Pixel(30, 40)));
        scene.Compositing = scene.Compositing with { ForegroundDissolve = 0.5 };
        Near((128, 125, 5), Render(scene).Pixel(30, 40));
    }

    [Fact]
    public void An_alpha_keyed_foreground_is_laid_over_by_its_alpha_or_an_alpha_images_brightness()
    {
        using var dir = new TempDir();
        string fg = Picture(dir.Path, "fg.png", 8, 8, (x, _) => x < 4 ? ((byte)255, (byte)255, (byte)255, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255));
        // The alpha image: black (none) on top, white (all) below.
        string matte = Picture(dir.Path, "matte.png", 8, 8, (_, y) => y < 4 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var scene = Stage();
        var before = Render(scene);
        scene.Images = [new SceneImage { Path = fg }, new SceneImage { Path = matte }];
        scene.Compositing = new CompositingSettings { Foreground = fg, Key = ForegroundKey.Alpha }.Checked();
        var img = Render(scene);
        Assert.Equal(before.Pixel(10, 40), img.Pixel(10, 40));
        Assert.Equal((255, 255, 255), Rgb(img.Pixel(70, 40)));
        Assert.Equal(255, img.Pixel(70, 40).A);

        scene.Compositing = scene.Compositing with { ForegroundAlpha = matte };
        img = Render(scene);
        // The alpha image's brightness replaces the foreground's own alpha: none on top, all below.
        Assert.Equal(before.Pixel(70, 10), img.Pixel(70, 10));
        Assert.Equal((255, 255, 255), Rgb(img.Pixel(70, 70)));
    }

    [Fact]
    public void Missing_images_and_empty_sequences_are_render_warnings_and_are_left_out()
    {
        using var dir = new TempDir();
        var scene = Stage();
        var before = Render(scene);
        string missing = Path.Combine(dir.Path, "gone.png");
        scene.Images = [new SceneImage { Path = missing }, new SceneImage { Path = Path.Combine(dir.Path, "seq0001.png"), Sequence = true }];
        scene.Compositing = new CompositingSettings { Background = missing };
        var warnings = scene.Evaluate(1).Warnings;
        Assert.Contains(warnings, w => w.Contains("gone.png"));
        Assert.Contains(warnings, w => w.Contains("no frames"));
        Assert.Equal(before.Rgba, Render(scene).Rgba);
    }

    [Fact]
    public void Compositing_checks_its_settings()
    {
        Assert.Throws<ArgumentException>(() => new CompositingSettings { Key = ForegroundKey.Color }.Checked());
        Assert.Throws<ArgumentException>(() => new CompositingSettings { Foreground = "a.png", ForegroundDissolve = 2 }.Checked());
        Assert.Throws<ArgumentException>(() => new CompositingSettings { Foreground = "a.png", Key = ForegroundKey.Color, KeyLow = Vec3.One, KeyHigh = Vec3.Zero }.Checked());
        Assert.Throws<ArgumentException>(() => new CompositingSettings { Foreground = "a.png", ForegroundAlpha = "b.png" }.Checked());
        Assert.False(new CompositingSettings().On);
    }

    [Fact]
    public void Images_and_compositing_are_copied_snapshotted_and_kept_in_avascene_files_relative_to_the_scene()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "frames"));
        string seq = Solid(Path.Combine(dir.Path, "frames"), "sky0001.png", 1, 2, 3);
        string fg = Solid(dir.Path, "fg.png", 4, 5, 6);
        var scene = Stage();
        scene.Images = [new SceneImage { Path = seq, Sequence = true, Offset = 3, Loop = true }, new SceneImage { Path = fg }];
        var comp = new CompositingSettings
        {
            Background = seq, Foreground = fg, Key = ForegroundKey.Color, KeyLow = new Vec3(0.1, 0.2, 0.3), KeyHigh = new Vec3(0.4, 0.5, 0.6), ForegroundDissolve = 0.25,
        };
        scene.Compositing = comp;

        var copy = scene.Copy();
        Assert.Equal(scene.Images, copy.Images);
        Assert.Equal(comp, copy.Compositing);

        var snap = scene.Snapshot();
        scene.Images = [];
        scene.Compositing = new CompositingSettings();
        snap.Restore();
        Assert.Equal(2, scene.Images.Count);
        Assert.Equal(comp, scene.Compositing);

        string file = Path.Combine(dir.Path, "comp.avascene");
        SceneJson.WriteFile(scene, file, embedMeshes: true);
        string text = File.ReadAllText(file);
        Assert.Contains("\"frames/sky0001.png\"", text);
        Assert.DoesNotContain(dir.Path, text);
        var read = SceneJson.ReadFile(file).Scene;
        Assert.Equal(scene.Images, read.Images);
        Assert.Equal(comp, read.Compositing);
        // A scene without them writes neither.
        string plain = SceneJson.Write(Stage());
        Assert.DoesNotContain("images", plain);
        Assert.DoesNotContain("compositing", plain);
    }

    [Fact]
    public void Compositing_goes_through_lws_files_with_sequences_marked()
    {
        using var dir = new TempDir();
        string seq = Solid(dir.Path, "bg0001.png", 1, 2, 3);
        string fg = Solid(dir.Path, "fg.png", 4, 5, 6);
        var scene = Stage();
        scene.Images = [new SceneImage { Path = seq, Sequence = true }, new SceneImage { Path = fg }];
        scene.Compositing = new CompositingSettings
        {
            Background = seq, Foreground = fg, Key = ForegroundKey.Color, KeyLow = new Vec3(0, 0.5, 0), KeyHigh = new Vec3(0.25, 1, 0.25), ForegroundDissolve = 0.5,
        };
        var warnings = new List<string>();
        string lws = LwsWriter.Write(scene, warnings);
        Assert.Contains($"BGImage {seq} (sequence)", lws);
        Assert.Contains($"FGImage {fg}", lws);
        Assert.Contains("ForegroundKey 1", lws);
        Assert.Empty(warnings);
        var read = LwsReader.Read(lws, _ => new Document(), dir.Path).Scene;
        Assert.Equal(scene.Images, read.Images);
        Assert.Equal(scene.Compositing, read.Compositing);

        // The alpha key goes as FGAlphaImage; offsets and looping (and unused images) have no place, so they warn.
        scene.Images = [scene.Images[0] with { Loop = true }, scene.Images[1], new SceneImage { Path = Path.Combine(dir.Path, "x.png") }];
        scene.Compositing = new CompositingSettings { Background = seq, Foreground = fg, Key = ForegroundKey.Alpha };
        lws = LwsWriter.Write(scene, warnings);
        Assert.Contains($"FGAlphaImage {fg}", lws);
        Assert.Equal(2, warnings.Count);
        read = LwsReader.Read(lws, _ => new Document(), dir.Path).Scene;
        Assert.Equal(ForegroundKey.Alpha, read.Compositing.Key);
        Assert.Equal("", read.Compositing.ForegroundAlpha);
    }

    [Fact]
    public void LightWave_5s_compositing_lines_are_read_with_colours_out_of_255_and_a_prefixed_sequence()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Images"));
        Solid(Path.Combine(dir.Path, "Images"), "fire001.png", 1, 1, 1);
        Solid(Path.Combine(dir.Path, "Images"), "fire002.png", 1, 1, 1);
        Solid(Path.Combine(dir.Path, "Images"), "front.png", 1, 1, 1);
        const string text = """
            LWSC
            1

            FirstFrame 1
            LastFrame 30
            BGImage Images/fire (sequence)
            FGImage Images/front.png
            ForegroundKey 1
            LowClipColor 0 255 0
            HighClipColor 51 255 51
            """;
        var result = LwsReader.Read(text, _ => new Document(), dir.Path);
        var scene = result.Scene;
        Assert.Equal(2, scene.Images.Count);
        Assert.True(scene.Images[0].Sequence);
        Assert.EndsWith("fire001.png", scene.Images[0].Path);
        Assert.Equal(scene.Images[0].Path, scene.Compositing.Background);
        Assert.Equal(ForegroundKey.Color, scene.Compositing.Key);
        AssertNear(new Vec3(0.2, 1, 0.2), scene.Compositing.KeyHigh, 1e-9);
        Assert.Empty(result.Warnings);
    }
}
