using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class ProceduralRenderTests
{
    /// <summary>A camera at z = -4 looking down +Z at 80 × 80, no ambient light, a black backdrop, a light over the camera.</summary>
    private static Scene Stage()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 80;
        cam.Height = 80;
        scene.AddLight("Light");
        return scene;
    }

    private static RenderImage Render(Scene scene, int samples = 1) =>
        RayTracer.Render(scene, 0, new RenderSettings { SamplesPerPixel = samples }, null, TestContext.Current.CancellationToken);

    private static (int, int, int) Rgb((byte R, byte G, byte B, byte A) p) => (p.R, p.G, p.B);

    /// <summary>How many pixels two pictures differ in.</summary>
    private static int Differences(RenderImage a, RenderImage b)
    {
        int n = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (a.Rgba[i] != b.Rgba[i] || a.Rgba[i + 1] != b.Rgba[i + 1] || a.Rgba[i + 2] != b.Rgba[i + 2]) n++;
        return n;
    }

    private static Scene Ball(params TextureLayer[] layers)
    {
        var scene = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1, 48, 24), s =>
        {
            s.Color = new Rgb(200, 200, 200);
            s.Smoothing = true;
            s.Textures = [.. layers];
        }));
        return scene;
    }

    [Fact]
    public void A_procedural_textured_ball_differs_from_a_plain_one_and_renders_the_same_every_time()
    {
        var marble = TextureLayer.NewProcedural(ProceduralKind.Marble);
        marble.TextureColor = new Rgb(20, 60, 40);
        marble.Size = new Vec3(0.4, 0.4, 0.4);
        var plain = Render(Ball());
        var first = Render(Ball(marble), samples: 4);
        var again = Render(Ball(marble.Clone()), samples: 4);
        Assert.Equal(first.Rgba, again.Rgba);
        Assert.True(Differences(Render(Ball(marble)), plain) > 400);
        // The veins are the texture colour: dark green-grey pixels where the plain ball is grey.
        bool Veiny((byte R, byte G, byte B, byte A) p) => p.G > p.R + 15;
        int veins = 0;
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 80; x++)
                if (Veiny(first.Pixel(x, y))) veins++;
        Assert.InRange(veins, 50, 3000);
    }

    [Fact]
    public void A_checkerboard_on_a_wall_shows_its_squares_and_follows_the_object()
    {
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.TextureColor = new Rgb(255, 0, 0);
        var scene = Stage();
        var wall = scene.AddObject("Wall", Doc(Square(Vec3.Zero, 2, -Vec3.UnitZ), s =>
        {
            s.Color = new Rgb(128, 128, 128);
            s.Diffuse = 0;
            s.Luminosity = 1;
            s.Textures = [checks];
        }));
        var img = Render(scene);
        // 32 pixels a metre: the four quarters of the wall alternate, red where floor(x) + floor(y) is odd.
        Assert.Equal((255, 0, 0), Rgb(img.Pixel(24, 24)));
        Assert.Equal((128, 128, 128), Rgb(img.Pixel(56, 24)));
        Assert.Equal((128, 128, 128), Rgb(img.Pixel(24, 56)));
        Assert.Equal((255, 0, 0), Rgb(img.Pixel(56, 56)));
        // Moved half a metre right, the pattern moves with it (object coordinates)…
        wall.SetPosition(0, new Vec3(0.5, 0, 0));
        Assert.Equal((255, 0, 0), Rgb(Render(scene).Pixel(40, 24)));
        // …unless the layer is in world coordinates.
        checks.WorldCoordinates = true;
        Assert.Equal((255, 0, 0), Rgb(Render(scene).Pixel(24, 24)));
        Assert.Equal((128, 128, 128), Rgb(Render(scene).Pixel(40, 24)));
    }

    [Fact]
    public void Procedurals_change_value_channels_by_their_amount()
    {
        // A luminosity checkerboard on an unlit wall: lit where the squares are, dark between.
        var glow = TextureLayer.NewProcedural(ProceduralKind.Checkerboard, TextureChannel.Luminosity);
        glow.Value = 1;
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = cam.Height = 80;
        scene.AddObject("Wall", Doc(Square(Vec3.Zero, 2, -Vec3.UnitZ), s =>
        {
            s.Color = new Rgb(0, 200, 0);
            s.Luminosity = 0;
            s.Textures = [glow];
        }));
        var img = Render(scene);
        Assert.Equal((0, 200, 0), Rgb(img.Pixel(24, 24)));
        Assert.Equal((0, 0, 0), Rgb(img.Pixel(56, 24)));
    }

    [Fact]
    public void A_procedural_bump_changes_the_shading_but_not_the_silhouette()
    {
        var bump = TextureLayer.NewProcedural(ProceduralKind.FractalNoise, TextureChannel.Bump);
        bump.Size = new Vec3(0.3, 0.3, 0.3);
        bump.Amplitude = 0.3;
        var plain = Render(Ball());
        var bumped = Render(Ball(bump));
        Assert.True(Differences(plain, bumped) > 500);
        // The same pixels are covered: a bump only tilts the normal.
        for (int i = 3; i < plain.Rgba.Length; i += 4) Assert.Equal(plain.Rgba[i], bumped.Rgba[i]);
        // Repeatable, and no bump at all with no amplitude.
        Assert.Equal(bumped.Rgba, Render(Ball(bump.Clone())).Rgba);
        bump.Amplitude = 0;
        Assert.Equal(plain.Rgba, Render(Ball(bump)).Rgba);
    }

    [Fact]
    public void A_ripples_bump_tilts_a_flat_wall_into_rings()
    {
        var ripples = TextureLayer.NewProcedural(ProceduralKind.Ripples, TextureChannel.Bump);
        ripples.Amplitude = 0.05;
        Procedurals.Set(ripples, "Wave Sources", 1);
        var scene = Stage();
        scene.AddObject("Wall", Doc(Square(Vec3.Zero, 2, -Vec3.UnitZ), s => s.Textures = [ripples]));
        var img = Render(scene);
        var levels = new HashSet<int>();
        for (int x = 4; x < 76; x++) levels.Add(img.Pixel(x, 40).R);
        // A flat wall lit straight on is one level; the rings make many.
        Assert.True(levels.Count > 10, $"{levels.Count} levels");
    }

    [Fact]
    public void Procedural_layers_go_into_avascene_files_and_back()
    {
        var marble = TextureLayer.NewProcedural(ProceduralKind.Marble);
        marble.TextureColor = new Rgb(1, 2, 3);
        marble.Falloff = new Vec3(0.1, 0.2, 0.3);
        Procedurals.Set(marble, "Vein Spacing", 0.25);
        var value = TextureLayer.NewProcedural(ProceduralKind.Value, TextureChannel.Specular);
        value.Value = 0.4;
        var scene = Ball(marble, value);
        string json = SceneJson.Write(scene, embedMeshes: true);
        var (read, _) = SceneJson.Read(json);
        var t = read.Objects.Single().Document!.Surfaces[0].Textures;
        Assert.Equal(marble.Signature(), t[0].Signature());
        Assert.Equal(value.Signature(), t[1].Signature());
        Assert.Equal(json, SceneJson.Write(read, embedMeshes: true));
    }
}
