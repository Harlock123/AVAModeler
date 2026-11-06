using AVAModeler.Core;
using AVAModeler.Rendering;

namespace AVAModeler.Rendering.Tests;

[Collection("OpenGL")]
public class ProceduralRenderingTests
{
    private static (int R, int G, int B) Rgb8(uint c) => TextureRenderingTests.Rgb8(c);

    private static SoftwareRenderer Front(ViewScene scene, int size = 200)
    {
        var c = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 2.5 };
        c.Resize(size, size);
        var r = new SoftwareRenderer();
        r.Render(scene, c);
        return r;
    }

    private static ViewScene Scene(Mesh mesh, params Surface[] surfaces) => new()
    {
        Layers = [new ViewLayer { Mesh = mesh }], Surfaces = [.. surfaces], Style = DisplayStyle.Shaded, ShowGrid = false, BackfaceTint = false,
    };

    [Fact]
    public void The_perspective_view_shows_a_colour_procedural_worked_out_at_every_pixel()
    {
        // One face, two units square, with a red checkerboard of one-unit cubes: four squares, alternating, though
        // the face has only four corners.
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.TextureColor = new Rgb(255, 0, 0);
        var flat = new Surface("Flat") { Color = new Rgb(128, 128, 128), Luminosity = 1, Textures = [checks] };
        var r = Front(Scene(TextureRenderingTests.Square(), flat));
        // The front view shows X right and Y up: the square at top left (x < 0, y > 0) is floor -1 + 0 + 0: odd, red.
        Assert.Equal((255, 0, 0), Rgb8(r.Pixels[70 * 200 + 70]));
        Assert.Equal((128, 128, 128), Rgb8(r.Pixels[70 * 200 + 130]));
        Assert.Equal((128, 128, 128), Rgb8(r.Pixels[130 * 200 + 70]));
        Assert.Equal((255, 0, 0), Rgb8(r.Pixels[130 * 200 + 130]));

        // Turned off, the surface's own grey shows; at half opacity, half the red.
        var plain = Scene(TextureRenderingTests.Square(), flat);
        plain.ShowTextures = false;
        Assert.Equal((128, 128, 128), Rgb8(Front(plain).Pixels[70 * 200 + 70]));
        checks.Opacity = 0.5;
        var (red, green, _) = Rgb8(Front(Scene(TextureRenderingTests.Square(), flat)).Pixels[70 * 200 + 70]);
        Assert.InRange(red, 190, 192);
        Assert.InRange(green, 63, 64);
    }

    [Fact]
    public void Value_channel_procedurals_and_unknown_ones_dont_change_the_view()
    {
        var noise = TextureLayer.NewProcedural(ProceduralKind.FractalNoise, TextureChannel.Diffuse);
        var unknown = new TextureLayer { Procedural = "Brick" };
        var flat = new Surface("Flat") { Color = new Rgb(128, 128, 128), Luminosity = 1, Textures = [noise, unknown] };
        var r = Front(Scene(TextureRenderingTests.Square(), flat));
        Assert.All(new[] { 70 * 200 + 70, 100 * 200 + 100, 130 * 200 + 130 }, i => Assert.Equal((128, 128, 128), Rgb8(r.Pixels[i])));
    }

    /// <summary>A globe of quads (triangles at the poles) round the origin.</summary>
    internal static Mesh Ball(double radius, int sides, int bands)
    {
        var m = new Mesh();
        for (int j = 0; j <= bands; j++)
        {
            double lat = Math.PI * j / bands - Math.PI / 2;
            for (int i = 0; i < sides; i++)
            {
                double lon = 2 * Math.PI * i / sides;
                m.Points.Add(new Vec3(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon)) * radius);
            }
        }
        for (int j = 0; j < bands; j++)
            for (int i = 0; i < sides; i++)
            {
                int a = j * sides + i, b = j * sides + (i + 1) % sides;
                m.AddPolygon([a, a + sides, b + sides, b]);
            }
        m.Touch();
        return m;
    }

    /// <summary>A ball with marble, fractal noise and a checkerboard, and a floor with wood and dots, in a turned view.</summary>
    private static (ViewScene Scene, ViewCamera Camera) Still()
    {
        var mesh = Ball(0.8, 24, 12);
        var floor = new Mesh();
        floor.AddFace(1, new Vec3(-2, -0.8, -2), new Vec3(-2, -0.8, 2), new Vec3(2, -0.8, 2), new Vec3(2, -0.8, -2));
        mesh.Append(floor);
        var marble = TextureLayer.NewProcedural(ProceduralKind.Marble);
        marble.TextureColor = new Rgb(60, 60, 70);
        marble.Size = new Vec3(0.6, 0.6, 0.6);
        var noise = TextureLayer.NewProcedural(ProceduralKind.FractalNoise);
        noise.TextureColor = new Rgb(200, 120, 40);
        noise.Opacity = 0.5;
        noise.Size = new Vec3(0.4, 0.4, 0.4);
        var ball = new Surface("Ball") { Color = new Rgb(230, 225, 215), Specular = 0.3, Smoothing = true, Textures = [marble, noise] };
        var wood = TextureLayer.NewProcedural(ProceduralKind.Wood);
        wood.TextureColor = new Rgb(90, 50, 20);
        wood.Axis = Axis.Z;
        wood.Size = new Vec3(1, 1, 1);
        var dots = TextureLayer.NewProcedural(ProceduralKind.Dots);
        dots.TextureColor = new Rgb(20, 20, 120);
        dots.Axis = Axis.Y;
        dots.Size = new Vec3(0.5, 0.5, 0.5);
        Procedurals.Set(dots, "Fuzziness", 0.3);
        var checks = TextureLayer.NewProcedural(ProceduralKind.Checkerboard);
        checks.TextureColor = new Rgb(40, 40, 40);
        checks.Size = new Vec3(1, 1, 1);
        checks.Blend = TextureBlend.Multiply;
        checks.Opacity = 0.3;
        var ground = new Surface("Floor") { Color = new Rgb(200, 160, 110), Textures = [wood, dots, checks] };
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = mesh }], Surfaces = [ball, ground], Style = DisplayStyle.Shaded, ShowGrid = false, BackfaceTint = false };
        var cam = new ViewCamera(ViewKind.Perspective) { Heading = 30, Pitch = 25, Distance = 5 };
        cam.Resize(240, 180);
        return (scene, cam);
    }

    [Fact]
    public void The_opengl_renderers_vertices_and_shader_sums_draw_procedurals_as_the_software_renderer_does()
    {
        var (scene, cam) = Still();
        var sw = new SoftwareRenderer();
        sw.Render(scene, cam);
        var (gl, covered) = TextureRenderingTests.GlPicture(scene, cam);
        var (close, compared, worst) = TextureRenderingTests.Agreement(sw.Pixels, gl, i => covered[i] && sw.Pixels[i] != Palette.PerspectiveBackground);
        Assert.True(compared > 8000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.99, $"{close} of {compared} agree; the worst is {worst} levels apart");
    }

    [Fact]
    public void The_opengl_renderer_on_a_real_context_draws_procedurals_as_the_software_renderer_does()
    {
        var (scene, cam) = Still();
        using var gl = OffscreenGl.TryCreate(cam.Width, cam.Height, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        using var renderer = new GlRenderer(gl.Gl, gl.IsEs);
        renderer.Render(scene, cam);
        var picture = gl.Read();
        var sw = new SoftwareRenderer();
        sw.Render(scene, cam);
        var (close, compared, worst) = TextureRenderingTests.Agreement(sw.Pixels, picture,
            i => sw.Pixels[i] != Palette.PerspectiveBackground && picture[i] != Palette.PerspectiveBackground);
        Assert.True(compared > 8000, $"only {compared} pixels compared");
        // Single-precision noise and pattern edges differ by a hair, so a few pixels along the checks' and dots' edges differ.
        Assert.True(close >= compared * 0.97, $"{close} of {compared} agree; the worst is {worst} levels apart");
    }
}
