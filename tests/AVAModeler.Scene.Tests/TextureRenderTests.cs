using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class TextureRenderTests
{
    /// <summary>A 2 × 2 image: red, green along the top; blue, white below.</summary>
    private static TextureImage Four() => new(2, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);

    /// <summary>An image of n × n pixels from a function of the pixel.</summary>
    private static TextureImage Image(int n, Func<int, int, (byte, byte, byte)> f)
    {
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (r, g, b) = f(x, y);
                int i = (y * n + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
            }
        return new TextureImage(n, n, rgba);
    }

    private static string Name(string what) => $"{what}-{Guid.NewGuid():N}.png";

    /// <summary>A camera at z = -4 looking down +Z at 80 × 80, no ambient light, a black backdrop.</summary>
    private static Scene Stage(Vec3? lightRotation = null)
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 80;
        cam.Height = 80;
        var light = scene.AddLight("Light");
        if (lightRotation is { } r) light.SetRotation(0, r);
        return scene;
    }

    /// <summary>A 2 × 2 square at the origin facing the camera, its surface set up by the caller.</summary>
    private static Document Wall(Action<Surface> setup) => Doc(Square(Vec3.Zero, 2, -Vec3.UnitZ), setup);

    private static RenderImage Render(Scene scene, int samples = 1) =>
        RayTracer.Render(scene, 0, new RenderSettings { SamplesPerPixel = samples }, null, TestContext.Current.CancellationToken);

    private static (int, int, int) Rgb((byte R, byte G, byte B, byte A) p) => (p.R, p.G, p.B);

    /// <summary>A surface that shows its colour as it is: fully luminous, not lit.</summary>
    private static void Flat(Surface s, params TextureLayer[] layers)
    {
        s.Color = new Rgb(128, 128, 128);
        s.Diffuse = 0;
        s.Luminosity = 1;
        s.Textures = [.. layers];
    }

    [Fact]
    public void A_planar_image_lands_on_the_face_the_right_way_up()
    {
        string four = Name("four");
        TextureImages.Put(four, Four());
        var scene = Stage();
        scene.AddObject("Wall", Wall(s => Flat(s, new TextureLayer { Image = four, Size = new Vec3(2, 2, 2), PixelBlending = false })));
        var img = Render(scene);
        // The square fills the middle of the picture (32 pixels a metre); each quarter is a pixel of the image.
        Assert.Equal((255, 0, 0), Rgb(img.Pixel(24, 24)));
        Assert.Equal((0, 255, 0), Rgb(img.Pixel(56, 24)));
        Assert.Equal((0, 0, 255), Rgb(img.Pixel(24, 56)));
        Assert.Equal((255, 255, 255), Rgb(img.Pixel(56, 56)));
    }

    [Fact]
    public void Pixels_blend_bilinearly_unless_pixel_blending_is_off()
    {
        string four = Name("four");
        TextureImages.Put(four, Four());
        var layer = new TextureLayer { Image = four, Size = new Vec3(2, 2, 2), WidthTiling = TextureTiling.Edge, HeightTiling = TextureTiling.Edge };
        var scene = Stage();
        scene.AddObject("Wall", Wall(s => Flat(s, layer)));
        // Halfway across the top row of the image, red and green meet.
        var mid = Rgb(Render(scene).Pixel(40, 24));
        Assert.InRange(mid.Item1, 100, 160);
        Assert.InRange(mid.Item2, 100, 160);
        layer.PixelBlending = false;
        var sharp = Rgb(Render(scene).Pixel(40, 24));
        Assert.True(sharp == (255, 0, 0) || sharp == (0, 255, 0), $"{sharp}");
    }

    [Fact]
    public void A_uv_map_places_the_image_and_textures_move_with_their_object()
    {
        string four = Name("four");
        TextureImages.Put(four, Four());
        var mesh = Square(Vec3.Zero, 2, -Vec3.UnitZ);
        // Each corner gets the UVs of the opposite corner of the image: a half turn.
        foreach (int i in Enumerable.Range(0, 4))
            mesh.SetPointUV("UV", i, new Vec2(0.5 - mesh.Points[i].X / 2, 0.5 - mesh.Points[i].Y / 2));
        var scene = Stage();
        var obj = scene.AddObject("Wall", Doc(mesh, s => Flat(s, new TextureLayer { Image = four, Projection = TextureProjection.UV, UVMap = "UV", PixelBlending = false })));
        var img = Render(scene);
        Assert.Equal((255, 255, 255), Rgb(img.Pixel(24, 24)));
        Assert.Equal((255, 0, 0), Rgb(img.Pixel(56, 56)));

        // A planar map is in the object's coordinates: moved, the object takes its texture along.
        var planar = Stage();
        var wall = planar.AddObject("Wall", Wall(s => Flat(s, new TextureLayer { Image = four, Size = new Vec3(2, 2, 2), PixelBlending = false })));
        wall.SetPosition(0, new Vec3(0.5, 0, 0));
        var moved = Render(planar);
        Assert.Equal((255, 0, 0), Rgb(moved.Pixel(41, 24)));
        Assert.Equal((0, 255, 0), Rgb(moved.Pixel(70, 24)));
        _ = obj;
    }

    [Fact]
    public void Value_textures_change_diffuse_luminosity_and_transparency()
    {
        string black = Name("black"), white = Name("white");
        TextureImages.Put(black, Image(2, (_, _) => (0, 0, 0)));
        TextureImages.Put(white, Image(2, (_, _) => (255, 255, 255)));
        var scene = Stage();
        scene.AddObject("Wall", Wall(s => { s.Color = new Rgb(200, 200, 200); }));
        int lit = Rgb(Render(scene).Pixel(40, 40)).Item1;
        Assert.True(lit > 150, $"{lit}");
        scene.Objects.Single().Surfaces[0].Textures = [new TextureLayer { Channel = TextureChannel.Diffuse, Image = black }];
        Assert.Equal(0, Rgb(Render(scene).Pixel(40, 40)).Item1);
        scene.Objects.Single().Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Luminosity, Image = white, Opacity = 0.5 });
        Assert.InRange(Rgb(Render(scene).Pixel(40, 40)).Item1, 99, 101);
        scene.BackgroundColor = new Vec3(0, 0, 1);
        scene.Objects.Single().Surfaces[0].Textures = [new TextureLayer { Channel = TextureChannel.Transparency, Image = white }];
        Assert.Equal((0, 0, 255), Rgb(Render(scene).Pixel(40, 40)));
    }

    [Fact]
    public void A_bump_map_tilts_the_shading_toward_its_slope()
    {
        // Brightness rising steadily to the right: the surface leans left, toward a light from the left.
        string ramp = Name("ramp");
        TextureImages.Put(ramp, Image(64, (x, _) => ((byte)(x * 4), (byte)(x * 4), (byte)(x * 4))));
        var fromLeft = new Vec3(60, 0, 0);
        int Brightness(double amplitude)
        {
            var scene = Stage(fromLeft);
            scene.AddObject("Wall", Wall(s =>
            {
                s.Color = Core.Rgb.White;
                if (amplitude != 0) s.Textures = [new TextureLayer { Channel = TextureChannel.Bump, Image = ramp, Size = new Vec3(2, 2, 2), Amplitude = amplitude }];
            }));
            return Rgb(Render(scene).Pixel(40, 40)).Item1;
        }
        int flat = Brightness(0), bumped = Brightness(16), sunk = Brightness(-16);
        Assert.True(bumped > flat + 20, $"flat {flat}, bumped {bumped}");
        Assert.True(sunk < flat - 20, $"flat {flat}, sunk {sunk}");
    }

    [Fact]
    public void Mipmapping_averages_a_fine_pattern_seen_from_far_away()
    {
        // One-pixel black and white checks, 256 across a square that covers a few dozen pixels.
        string fine = Name("fine");
        TextureImages.Put(fine, Image(256, (x, y) => (x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)));
        var scene = Stage();
        scene.AddObject("Wall", Wall(s => Flat(s, new TextureLayer { Image = fine, Size = new Vec3(2, 2, 2) })));
        var img = Render(scene);
        for (int x = 30; x < 50; x += 3)
            Assert.InRange(Rgb(img.Pixel(x, 40)).Item1, 100, 155);
        Assert.Equal(64, TextureImages.Load(fine).Level(2).Width);
        Assert.Equal(1, TextureImages.Load(fine).Level(99).Width);
    }

    [Fact]
    public void A_missing_image_renders_as_a_checker_and_is_reported()
    {
        var scene = Stage();
        scene.AddObject("Wall", Wall(s => Flat(s, new TextureLayer { Image = Name("not-there"), Size = new Vec3(0.25, 0.25, 0.25), PixelBlending = false })));
        var img = Render(scene);
        var colours = Enumerable.Range(25, 30).Select(x => Rgb(img.Pixel(x, 40))).ToHashSet();
        Assert.Contains((230, 40, 200), colours);
        Assert.Contains(scene.Evaluate(0).Warnings, w => w.Contains("not-there") && w.Contains("checker"));
    }

    [Fact]
    public void Scenes_keep_uv_maps_and_textures()
    {
        var mesh = Square(Vec3.Zero, 2, -Vec3.UnitZ);
        mesh.SetPointUV("UV", 0, new Vec2(0.125, 0.25));
        mesh.SetCornerUV("UV", mesh.Polygons[0], 1, new Vec2(0.5, 0.75));
        mesh.SetPointUV("UV", 1, new Vec2(0, 0));
        mesh.SetCornerUV("UV", mesh.Polygons[0], 1, new Vec2(0.5, 0.75));
        var scene = Stage();
        var doc = Doc(mesh, s => s.Textures =
        [
            new TextureLayer { Image = "Images/brick.png", Projection = TextureProjection.UV, UVMap = "UV", Opacity = 0.5, Blend = TextureBlend.Multiply },
            new TextureLayer { Channel = TextureChannel.Bump, Image = "dents.png", Projection = TextureProjection.Cylindrical, Axis = Axis.X, Center = new Vec3(1, 2, 3), Size = new Vec3(4, 5, 6), WidthTiling = TextureTiling.Mirror, Amplitude = 2.5 },
        ]);
        scene.AddObject("Wall", doc);
        string json = SceneJson.Write(scene, embedMeshes: true);
        var back = SceneJson.Read(json).Scene;
        Assert.Equal(json, SceneJson.Write(back, embedMeshes: true));
        var bm = back.Objects.Single().Document!.Layers[0].Mesh;
        Assert.Equal(new Vec2(0.125, 0.25), bm.FindUVMap("UV")!.Points[0]);
        Assert.Equal(new Vec2(0.5, 0.75), bm.Polygons[0].CornerUVs!["UV"][1]);
        var t = back.Objects.Single().Surfaces[0].Textures;
        Assert.Equal(doc.Surfaces[0].Textures.Select(x => x.Signature()), t.Select(x => x.Signature()));
    }
}
