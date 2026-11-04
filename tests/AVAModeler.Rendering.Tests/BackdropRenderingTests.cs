using AVAModeler.Core;
using AVAModeler.Rendering;
using static AVAModeler.Rendering.Tests.TextureRenderingTests;

namespace AVAModeler.Rendering.Tests;

/// <summary>Reference images behind the flat views, as both renderers draw them.</summary>
public class BackdropRenderingTests
{
    /// <summary>Four flat quarters, 8 pixels each: red, green across the top; blue, white below.</summary>
    private static TextureImage Quarters()
    {
        var rgba = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                (byte r, byte g, byte b) = (x < 8, y < 8) switch
                {
                    (true, true) => ((byte)255, (byte)0, (byte)0),
                    (false, true) => ((byte)0, (byte)255, (byte)0),
                    (true, false) => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
            }
        return new TextureImage(16, 16, rgba);
    }

    private static ViewCamera Camera(ViewKind kind, Vec3 center = default, double ppu = 50, int size = 200)
    {
        var c = new ViewCamera(kind) { Center = center, PixelsPerUnit = ppu };
        c.Resize(size, size);
        return c;
    }

    private static SoftwareRenderer Draw(ViewScene scene, ViewCamera cam)
    {
        var r = new SoftwareRenderer();
        r.Render(scene, cam);
        return r;
    }

    private static uint At(SoftwareRenderer r, ScreenPoint p) => r.Pixels[(int)p.Y * r.Width + (int)p.X];

    /// <summary>The colour a backdrop pixel should be: the picture's over the view's grey, at its opacity.</summary>
    private static (int, int, int) Over((int R, int G, int B) c, double opacity)
    {
        var (gr, gg, gb) = Rgb8(Palette.Background);
        return ((int)(gr + (c.R - gr) * opacity), (int)(gg + (c.G - gg) * opacity), (int)(gb + (c.B - gb) * opacity));
    }

    private static void Near((int R, int G, int B) want, uint got)
    {
        var (r, g, b) = Rgb8(got);
        Assert.True(Math.Abs(r - want.R) <= 2 && Math.Abs(g - want.G) <= 2 && Math.Abs(b - want.B) <= 2, $"wanted {want}, got {(r, g, b)}");
    }

    private static ViewScene Scene(BackdropView view, Backdrop b, bool grid = false) => new()
    {
        Backdrops = BackdropSet.Empty.With(view, b),
        ShowGrid = grid,
    };

    [Theory]
    [InlineData(ViewKind.Top, BackdropView.Top)]
    [InlineData(ViewKind.Front, BackdropView.Front)]
    [InlineData(ViewKind.Side, BackdropView.Side)]
    public void Each_flat_view_shows_its_backdrop_where_its_world_coordinates_say(ViewKind kind, BackdropView view)
    {
        TextureImages.Put("quarters.png", Quarters());
        // The views' cameras look along the axes the backdrop maths uses.
        var (r, u, _) = new ViewCamera(kind).Basis();
        Assert.Equal((r, u), BackdropMapping.Axes(view));
        Assert.Equal(view, BackdropDrawing.ViewOf(kind));

        // 2 m wide round (0.5, -0.25) on the view's axes, seen off-centre: each quarter lands where the maths puts it.
        var b = new Backdrop { Image = "quarters.png", Center = new Vec2(0.5, -0.25), Width = 2, Opacity = 0.75 };
        var cam = Camera(kind, BackdropMapping.ToWorld(view, new Vec2(0.3, 0.1)) + new ViewCamera(kind).Basis().Forward * 3);
        var pic = Draw(Scene(view, b), cam);
        Vec3 W(double a, double v) => BackdropMapping.ToWorld(view, new Vec2(a, v));
        Near(Over((255, 0, 0), 0.75), At(pic, cam.Project(W(0, 0.25))));        // top left: red
        Near(Over((0, 255, 0), 0.75), At(pic, cam.Project(W(1, 0.25))));        // top right: green
        Near(Over((0, 0, 255), 0.75), At(pic, cam.Project(W(0, -0.75))));       // bottom left: blue
        Near(Over((255, 255, 255), 0.75), At(pic, cam.Project(W(1, -0.75))));   // bottom right: white
        // Past its edge (1.5 across) the grey shows.
        Assert.Equal(Palette.Background, At(pic, cam.Project(W(1.7, 0))));
        // Its corners are where BackdropMapping says, on screen.
        var quad = BackdropDrawing.For(Scene(view, b).Backdrops, kind, null)!;
        var c0 = cam.Project(quad.Corners[0]);
        Assert.Equal(cam.Project(W(-0.5, -1.25)).X, c0.X, 9);
        Assert.Equal(cam.Project(W(-0.5, -1.25)).Y, c0.Y, 9);
    }

    [Fact]
    public void It_pans_and_zooms_with_the_view_and_the_other_views_dont_show_it()
    {
        TextureImages.Put("quarters.png", Quarters());
        var b = new Backdrop { Image = "quarters.png", Width = 2, Opacity = 1 };
        var scene = Scene(BackdropView.Front, b);
        var cam = Camera(ViewKind.Front);
        var red = new Vec3(-0.5, 0.5, 0);
        Near((255, 0, 0), At(Draw(scene, cam), cam.Project(red)));
        cam.Pan(37, -21);
        cam.Zoom(1.7, 60, 80);
        Assert.NotEqual(100, (int)cam.Project(red).X);
        Near((255, 0, 0), At(Draw(scene, cam), cam.Project(red)));
        // The side, top, perspective and UV views have none of it.
        foreach (var kind in new[] { ViewKind.Side, ViewKind.Top, ViewKind.Perspective, ViewKind.UV })
        {
            var other = Camera(kind);
            var pic = Draw(scene, other);
            uint ground = kind == ViewKind.Perspective ? Palette.PerspectiveBackground : Palette.Background;
            Assert.All(pic.Pixels, p => Assert.Equal(ground, p));
        }
        // Hidden, or fully see-through, it isn't drawn.
        Assert.All(Draw(Scene(BackdropView.Front, b with { Visible = false }), cam).Pixels, p => Assert.Equal(Palette.Background, p));
        Assert.Null(BackdropDrawing.For(Scene(BackdropView.Front, b with { Opacity = 0 }).Backdrops, ViewKind.Front, null));
    }

    [Fact]
    public void It_is_drawn_under_the_grid_and_the_wireframe()
    {
        TextureImages.Put("white.png", new TextureImage(1, 1, [255, 255, 255, 255]));
        var b = new Backdrop { Image = "white.png", Width = 3, Height = 3, Opacity = 1 };
        // A line across the middle of the front view, over the white backdrop.
        var mesh = new Mesh();
        mesh.AddPolygon([mesh.AddPoint(new Vec3(-1, 0.31, 0)), mesh.AddPoint(new Vec3(1, 0.31, 0))]);
        mesh.Touch();
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = mesh }], Backdrops = BackdropSet.Empty.With(BackdropView.Front, b) };
        var cam = Camera(ViewKind.Front, ppu: 50);
        var pic = Draw(scene, cam);
        // The wire is black on top of it; the grid's axis line through the origin is drawn over it too.
        var wire = cam.Project(new Vec3(0.2, 0.31, 0));
        Assert.True(Rgb8(At(pic, wire)).R < 80, $"the wire should be dark, got {Rgb8(At(pic, wire))}");
        Assert.Equal(Palette.Axis, pic.Pixels[(int)cam.Project(new Vec3(0.53, 0, 0)).Y * pic.Width + 127]);
        // Between grid lines the backdrop shows.
        var free = cam.Project(new Vec3(0.53, -0.53, 0));
        Near((255, 255, 255), At(pic, free));
    }

    [Fact]
    public void Brightness_contrast_invert_and_opacity_change_what_shows()
    {
        TextureImages.Put("grey.png", new TextureImage(1, 1, [100, 150, 200, 255]));
        var b = new Backdrop { Image = "grey.png", Width = 4, Opacity = 1 };
        var cam = Camera(ViewKind.Side);
        var mid = cam.Project(Vec3.Zero) with { X = 120, Y = 80 };
        Near((100, 150, 200), At(Draw(Scene(BackdropView.Side, b), cam), mid));
        Near((155, 105, 55), At(Draw(Scene(BackdropView.Side, b with { Invert = true }), cam), mid));
        Near((126, 126, 126), At(Draw(Scene(BackdropView.Side, b with { Contrast = 0, Brightness = -0.006 }), cam), mid));
        Near(Over((100, 150, 200), 0.5), At(Draw(Scene(BackdropView.Side, b with { Opacity = 0.5 }), cam), mid));
    }

    [Fact]
    public void A_missing_image_shows_the_magenta_checker_in_full()
    {
        var b = new Backdrop { Image = "/nowhere/" + Guid.NewGuid().ToString("N") + ".png", Width = 2, Height = 2, Opacity = 0.2 };
        var cam = Camera(ViewKind.Top);
        var pic = Draw(Scene(BackdropView.Top, b), cam);
        // The checker's squares, unfaded, across the 2 m square in the middle (100 pixels across).
        int magenta = 0, grey = 0;
        for (int y = 55; y < 145; y++)
            for (int x = 55; x < 145; x++)
            {
                var (r, g, bl) = Rgb8(pic.Pixels[y * 200 + x]);
                if (r > 200 && g < 60 && bl > 180) magenta++;
                else if (Math.Abs(r - 70) < 4 && Math.Abs(bl - 76) < 4) grey++;
            }
        Assert.True(magenta > 2500 && grey > 2500, $"{magenta} magenta and {grey} grey pixels");
        Assert.Equal(Palette.Background, pic.Pixels[10 * 200 + 10]);
    }

    /// <summary>A blueprint-like scene: a picture on the front view with a wire square over it, off-centre.</summary>
    private static (ViewScene Scene, ViewCamera Camera) Blueprint()
    {
        TextureImages.Put("checker.png", Checker());
        var b = new Backdrop { Image = "checker.png", Center = new Vec2(0.2, -0.1), Width = 2.4, Height = 1.8, Opacity = 0.7, Contrast = 1.3, Invert = true };
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = Square() }], Backdrops = BackdropSet.Empty.With(BackdropView.Front, b), ShowGrid = false };
        var cam = Camera(ViewKind.Front, new Vec3(0.1, 0.05, 0), 70, 220);
        return (scene, cam);
    }

    [Fact]
    public void The_opengl_renderers_backdrop_vertices_and_shader_sums_draw_what_the_software_renderer_does()
    {
        var (scene, cam) = Blueprint();
        scene.Layers.Clear();
        var quad = BackdropDrawing.For(scene.Backdrops, cam.Kind, null)!;
        var (surface, data) = GlRenderer.ReferenceTriangles(quad);
        Assert.Equal(6 * GlRenderer.TexturedVertexFloats, data.Count);
        Assert.Same(quad.Surface, surface);
        var sw = Draw(scene, cam);
        // The shader's C# twin, through the same vertices and the GL camera, against the software picture.
        var mvp = GlRenderer.ViewProjection(cam);
        int compared = 0, close = 0;
        for (int t = 0; t < 2; t++)
        {
            var v = new (double X, double Y)[3];
            var c = new TexturedCorner[3];
            for (int k = 0; k < 3; k++)
            {
                int o = (t * 3 + k) * GlRenderer.TexturedVertexFloats;
                var (clip, w) = mvp.TransformHomogeneous(new Vec3(data[o], data[o + 1], data[o + 2]));
                clip /= w;
                v[k] = ((clip.X + 1) / 2 * cam.Width, (1 - clip.Y) / 2 * cam.Height);
                c[k] = new TexturedCorner { Mul = data[o + 6], Uv0 = new Vec2(data[o + 7], data[o + 8]), On0 = data[o + 15] };
            }
            double area = (v[1].X - v[0].X) * (v[2].Y - v[0].Y) - (v[1].Y - v[0].Y) * (v[2].X - v[0].X);
            for (int y = 0; y < cam.Height; y++)
                for (int x = 0; x < cam.Width; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double b0 = ((v[1].X - px) * (v[2].Y - py) - (v[1].Y - py) * (v[2].X - px)) / area;
                    double b1 = ((v[2].X - px) * (v[0].Y - py) - (v[2].Y - py) * (v[0].X - px)) / area;
                    double b2 = 1 - b0 - b1;
                    if (b0 < 0.01 || b1 < 0.01 || b2 < 0.01) continue;
                    var col = Texturing.Fragment(surface, TexturedCorner.Blend(c[0], c[1], c[2], b0, b1, b2));
                    var (r, g, bl) = Rgb8(sw.Pixels[y * cam.Width + x]);
                    compared++;
                    if (Math.Abs(r - col.X * 255) <= 3 && Math.Abs(g - col.Y * 255) <= 3 && Math.Abs(bl - col.Z * 255) <= 3) close++;
                }
        }
        Assert.True(compared > 15000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.99, $"{close} of {compared} agree");
    }

    [Fact]
    public void The_opengl_renderer_on_a_real_context_draws_the_backdrop_as_the_software_renderer_does()
    {
        var (scene, cam) = Blueprint();
        using var gl = OffscreenGl.TryCreate(cam.Width, cam.Height, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        using var renderer = new GlRenderer(gl.Gl, gl.IsEs);
        renderer.Render(scene, cam);
        var picture = gl.Read();
        var sw = Draw(scene, cam);
        // Away from the wire (the two renderers draw lines differently), the backdrop agrees pixel for pixel.
        var wire = new bool[sw.Pixels.Length];
        var bare = Draw(new ViewScene { Backdrops = scene.Backdrops, ShowGrid = false }, cam);
        for (int i = 0; i < wire.Length; i++)
            if (bare.Pixels[i] != sw.Pixels[i])
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int j = i + dy * cam.Width + dx;
                        if (j >= 0 && j < wire.Length) wire[j] = true;
                    }
        var (close, compared, worst) = Agreement(sw.Pixels, picture, i => !wire[i] && sw.Pixels[i] != Palette.Background);
        Assert.True(compared > 15000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.98, $"{close} of {compared} agree; the worst is {worst} levels apart");
        // The wire is drawn over it in both.
        int dark = 0, darkGl = 0;
        for (int i = 0; i < wire.Length; i++)
        {
            if (bare.Pixels[i] == sw.Pixels[i]) continue;
            if (Rgb8(sw.Pixels[i]).R < 60) dark++;
            if (Rgb8(picture[i]).R < 60) darkGl++;
        }
        Assert.True(dark > 100 && darkGl > 100, $"{dark} and {darkGl} wire pixels");
    }
}
