using AVAModeler.Core;
using static AVAModeler.Rendering.Tests.TextureRenderingTests;

namespace AVAModeler.Rendering.Tests;

/// <summary>A dissolved layer (a Layout object's dissolve) drawn with screen-door transparency, by both renderers alike.</summary>
public class DissolveRenderingTests
{
    /// <summary>A 4 × 4 square facing the viewer at the origin.</summary>
    private static Mesh Square()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-2, -2, 0), new Vec3(-2, 2, 0), new Vec3(2, 2, 0), new Vec3(2, -2, 0));
        return m;
    }

    private static ViewCamera Camera()
    {
        var c = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 5 };
        c.Resize(160, 120);
        return c;
    }

    private static ViewScene Scene(double dissolve, DisplayStyle style = DisplayStyle.Shaded) => new()
    {
        Layers = [new ViewLayer { Mesh = Square(), Dissolve = dissolve }],
        Surfaces = [new Surface("Red") { Color = new Rgb(255, 0, 0) }],
        Style = style,
        ShowGrid = false,
    };

    private static uint[] Draw(ViewScene scene, ViewCamera cam)
    {
        var r = new SoftwareRenderer();
        r.Render(scene, cam);
        return r.Pixels;
    }

    [Fact]
    public void The_dither_leaves_out_the_dissolved_share_of_every_8_by_8_block()
    {
        Assert.Equal(Enumerable.Range(0, 64), Dither.Bayer.Order());
        foreach (double d in new[] { 0, 0.1, 0.25, 0.5, 0.6, 0.75, 0.99, 1 })
            for (int ox = 0; ox < 16; ox += 8)
            {
                int kept = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        if (Dither.Keeps(ox + x, y + 3 * ox, d)) kept++;
                Assert.InRange(kept / 64.0, 1 - d - 1 / 64.0, 1 - d + 1 / 64.0);
            }
        Assert.True(Dither.Keeps(5, 7, 0));
        Assert.False(Dither.Keeps(5, 7, 1));
        // The shader's table is the same one.
        Assert.Equal(Dither.Bayer, Dither.Glsl.Split(',').Select(s => int.Parse(s.Trim())));
    }

    [Fact]
    public void A_dissolved_layers_faces_are_drawn_on_the_dithers_kept_pixels_only()
    {
        var cam = Camera();
        var solid = Draw(Scene(0), cam);
        var half = Draw(Scene(0.5), cam);
        int face = 0, kept = 0;
        for (int y = 0; y < cam.Height; y++)
            for (int x = 0; x < cam.Width; x++)
            {
                int i = y * cam.Width + x;
                if (solid[i] == Palette.PerspectiveBackground) continue;
                face++;
                bool drawn = half[i] != Palette.PerspectiveBackground;
                Assert.Equal(Dither.Keeps(x, y, 0.5), drawn);
                if (drawn)
                {
                    kept++;
                    Assert.Equal(solid[i], half[i]);
                }
            }
        Assert.True(face > 5000, $"{face} face pixels");
        Assert.InRange((double)kept / face, 0.48, 0.52);

        // Wholly dissolved: not drawn; not dissolved: as ever.
        Assert.All(Draw(Scene(1, DisplayStyle.ShadedWireframe), cam), p => Assert.Equal(Palette.PerspectiveBackground, p));
        Assert.Equal(solid, Draw(new ViewScene { Layers = [new ViewLayer { Mesh = Square() }], Surfaces = Scene(0).Surfaces, Style = DisplayStyle.Shaded, ShowGrid = false }, cam));
    }

    [Fact]
    public void A_dissolved_layers_wire_is_dithered_too()
    {
        var cam = new ViewCamera(ViewKind.Front) { PixelsPerUnit = 30 };
        cam.Resize(160, 160);
        int Wire(double d) => Draw(Scene(d, DisplayStyle.Wireframe), cam).Count(p => p != Palette.Background);
        int all = Wire(0), half = Wire(0.5);
        Assert.True(all > 300, $"{all} wire pixels");
        Assert.InRange(half, all * 0.35, all * 0.65);
        Assert.Equal(0, Wire(1));
    }

    [Fact]
    public void The_opengl_renderer_on_a_real_context_dithers_a_dissolved_layer_as_the_software_renderer_does()
    {
        var cam = Camera();
        using var gl = OffscreenGl.TryCreate(cam.Width, cam.Height, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        // A plain and a textured square, both half dissolved, a third (behind) not: the screen doors and depth agree.
        var image = new TextureImage(2, 2, [0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255, 255, 255, 255, 255]);
        TextureImages.Put("dissolve-four.png", image);
        var textured = new Mesh();
        textured.AddFace(1, new Vec3(-0.5, -1.5, -0.5), new Vec3(-0.5, 0.5, -0.5), new Vec3(1.5, 0.5, -0.5), new Vec3(1.5, -1.5, -0.5));
        var back = new Mesh();
        back.AddFace(2, new Vec3(-3, -3, 1), new Vec3(-3, 3, 1), new Vec3(3, 3, 1), new Vec3(3, -3, 1));
        var scene = new ViewScene
        {
            Layers =
            [
                new ViewLayer { Mesh = Square(), Dissolve = 0.5 },
                new ViewLayer { Mesh = textured, Dissolve = 0.3 },
                new ViewLayer { Mesh = back },
            ],
            Surfaces =
            [
                new Surface("Red") { Color = new Rgb(255, 0, 0) },
                new Surface("Four") { Textures = [new TextureLayer { Image = "dissolve-four.png", Size = new Vec3(2, 2, 2), Center = new Vec3(0.5, -0.5, 0), PixelBlending = false }] },
                new Surface("Blue") { Color = new Rgb(0, 0, 255) },
            ],
            Style = DisplayStyle.Shaded,
            ShowGrid = false,
        };
        using var renderer = new GlRenderer(gl.Gl, gl.IsEs);
        renderer.Render(scene, cam);
        var picture = gl.Read();
        var sw = Draw(scene, cam);
        // Away from the faces' edges (where the two rasterizers may differ by a pixel), every pixel agrees.
        var edge = new bool[sw.Length];
        var outline = Draw(new ViewScene { Layers = scene.Layers.Select(l => new ViewLayer { Mesh = l.Mesh }).ToList(), Surfaces = scene.Surfaces, Style = DisplayStyle.Wireframe, ShowGrid = false }, cam);
        for (int i = 0; i < edge.Length; i++)
            if (outline[i] != Palette.PerspectiveBackground)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int j = i + dy * cam.Width + dx;
                        if (j >= 0 && j < edge.Length) edge[j] = true;
                    }
        var (close, compared, worst) = Agreement(sw, picture, i => !edge[i]);
        Assert.True(compared > 10000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.99, $"{close} of {compared} agree; the worst is {worst} levels apart");
        // Both show the blue face through the red one's screen door.
        Assert.Contains(picture, p => Rgb8(p).B > 150 && Rgb8(p).R < 60);
    }
}
