using AVAModeler.Core;
using AVAModeler.Rendering;

namespace AVAModeler.Rendering.Tests;

public class TextureRenderingTests
{
    /// <summary>A 2 × 2 image: red, green along the top; blue, white below.</summary>
    internal static TextureImage Four() => new(2, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);

    /// <summary>A checker of 8 × 8 squares, yellow and dark blue, 64 pixels across.</summary>
    internal static TextureImage Checker()
    {
        var rgba = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                bool odd = (x / 8 + y / 8) % 2 == 1;
                int i = (y * 64 + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = odd ? ((byte)250, (byte)220, (byte)40, (byte)255) : ((byte)30, (byte)40, (byte)110, (byte)255);
            }
        return new TextureImage(64, 64, rgba);
    }

    private static Mesh Square()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        return m;
    }

    /// <summary>A surface that shows its colour flat (fully luminous, no highlight), so pixels are the texture's own.</summary>
    private static Surface Flat(params TextureLayer[] layers) => new("Flat") { Color = new Rgb(128, 128, 128), Luminosity = 1, Textures = [.. layers] };

    private static SoftwareRenderer Front(ViewScene scene, int size = 200)
    {
        var c = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 2.5 };
        c.Resize(size, size);
        var r = new SoftwareRenderer();
        r.Render(scene, c);
        return r;
    }

    private static (int R, int G, int B) Rgb8(uint c) => ((int)(c >> 16) & 0xFF, (int)(c >> 8) & 0xFF, (int)c & 0xFF);

    /// <summary>A colour to within a level (the renderer rounds down).</summary>
    private static void Near((int R, int G, int B) want, uint got)
    {
        var (r, g, b) = Rgb8(got);
        Assert.True(Math.Abs(r - want.R) <= 1 && Math.Abs(g - want.G) <= 1 && Math.Abs(b - want.B) <= 1, $"wanted {want}, got {(r, g, b)}");
    }

    private static ViewScene Scene(Mesh mesh, Surface s) => new()
    {
        Layers = [new ViewLayer { Mesh = mesh }], Surfaces = [s], Style = DisplayStyle.Shaded, ShowGrid = false, BackfaceTint = false,
    };

    [Fact]
    public void A_planar_image_shows_on_the_face_the_right_way_up()
    {
        TextureImages.Put("four.png", Four());
        var layer = new TextureLayer { Image = "four.png", Axis = Axis.Z, Size = new Vec3(2, 2, 2), PixelBlending = false };
        var r = Front(Scene(Square(), Flat(layer)));
        Near((255, 0, 0), r.Pixels[70 * 200 + 70]);
        Near((0, 255, 0), r.Pixels[70 * 200 + 130]);
        Near((0, 0, 255), r.Pixels[130 * 200 + 70]);
        Near((255, 255, 255), r.Pixels[130 * 200 + 130]);
        // Turned off, the surface's own grey shows.
        var plain = Scene(Square(), Flat(layer));
        plain.ShowTextures = false;
        Near((128, 128, 128), Front(plain).Pixels[70 * 200 + 70]);
    }

    [Fact]
    public void A_uv_mapped_image_follows_the_map_and_unmapped_faces_keep_their_colour()
    {
        TextureImages.Put("four.png", Four());
        var m = Square();
        // The map turned a half turn: the bottom-right corner of the face gets the image's top-left.
        Vec2[] uvs = [new(1, 1), new(1, 0), new(0, 0), new(0, 1)];
        for (int i = 0; i < 4; i++) m.SetPointUV("Turned", i, uvs[i]);
        var layer = new TextureLayer { Image = "four.png", Projection = TextureProjection.UV, UVMap = "Turned", PixelBlending = false };
        var r = Front(Scene(m, Flat(layer)));
        Near((255, 255, 255), r.Pixels[70 * 200 + 70]);
        Near((255, 0, 0), r.Pixels[130 * 200 + 130]);
        layer.UVMap = "Nothing";
        Near((128, 128, 128), Front(Scene(m, Flat(layer))).Pixels[100 * 200 + 100]);
    }

    [Fact]
    public void Layers_blend_by_opacity_and_mode_and_a_missing_image_shows_a_checker()
    {
        TextureImages.Put("four.png", Four());
        // Far smaller than the image's first pixel: all red, at half strength over grey.
        var half = new TextureLayer { Image = "four.png", Size = new Vec3(200, 200, 200), Center = new Vec3(99, -99, 0), PixelBlending = false, Opacity = 0.5 };
        var r = Front(Scene(Square(), Flat(half)));
        var (cr, cg, cb) = Rgb8(r.Pixels[100 * 200 + 100]);
        Assert.InRange(cr, 190, 193);
        Assert.InRange(cg, 63, 65);
        Assert.Equal(cg, cb);
        half.Blend = TextureBlend.Multiply;
        half.Opacity = 1;
        Near((128, 0, 0), Front(Scene(Square(), Flat(half))).Pixels[100 * 200 + 100]);

        var missing = new TextureLayer { Image = "no-such-image-" + Guid.NewGuid() + ".png" };
        var m = Front(Scene(Square(), Flat(missing)));
        Assert.Contains(m.Pixels, p => Rgb8(p) == (230, 40, 200));
    }

    /// <summary>
    /// The OpenGL renderer's picture worked out without a GPU: its vertex data (from the very code that fills its
    /// buffers) through its matrix, rasterized as OpenGL does (perspective-correct by 1/w, depth-tested), each pixel
    /// coloured by the C# twin of its fragment shader.
    /// </summary>
    private static (uint[] Pixels, bool[] Covered) GlPicture(ViewScene scene, ViewCamera cam)
    {
        int w = cam.Width, h = cam.Height;
        var pixels = new uint[w * h];
        var covered = new bool[w * h];
        var depth = new double[w * h];
        Array.Fill(depth, double.PositiveInfinity);
        var m = GlRenderer.ViewProjection(cam);
        var plain = new List<float>();
        var textured = new List<(TexturedSurface Surface, List<float> Vertices)>();
        foreach (var layer in scene.Layers) GlRenderer.FaceTriangles(layer, scene, cam, plain, textured);
        Assert.Empty(plain);
        const int n = GlRenderer.TexturedVertexFloats;
        foreach (var (ts, v) in textured)
            for (int t = 0; t + 3 * n <= v.Count; t += 3 * n)
            {
                var sx = new double[3];
                var sy = new double[3];
                var sz = new double[3];
                var iw = new double[3];
                var corner = new TexturedCorner[3];
                for (int k = 0; k < 3; k++)
                {
                    int o = t + k * n;
                    var (clip, cw) = m.TransformHomogeneous(new Vec3(v[o], v[o + 1], v[o + 2]));
                    sx[k] = (clip.X / cw + 1) / 2 * w;
                    sy[k] = (1 - clip.Y / cw) / 2 * h;
                    sz[k] = clip.Z / cw;
                    iw[k] = 1 / cw;
                    corner[k] = new TexturedCorner
                    {
                        Add = new Vec3(v[o + 3], v[o + 4], v[o + 5]), Mul = v[o + 6],
                        Uv0 = new Vec2(v[o + 7], v[o + 8]), Uv1 = new Vec2(v[o + 9], v[o + 10]),
                        Uv2 = new Vec2(v[o + 11], v[o + 12]), Uv3 = new Vec2(v[o + 13], v[o + 14]),
                        On0 = v[o + 15], On1 = v[o + 16], On2 = v[o + 17], On3 = v[o + 18],
                    };
                }
                double area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sy[1] - sy[0]) * (sx[2] - sx[0]);
                if (Math.Abs(area) < 1e-9) continue;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double b0 = ((sx[1] - px) * (sy[2] - py) - (sy[1] - py) * (sx[2] - px)) / area;
                        double b1 = ((sx[2] - px) * (sy[0] - py) - (sy[2] - py) * (sx[0] - px)) / area;
                        double b2 = 1 - b0 - b1;
                        if (b0 < 0 || b1 < 0 || b2 < 0) continue;
                        double z = b0 * sz[0] + b1 * sz[1] + b2 * sz[2];
                        if (z >= depth[y * w + x]) continue;
                        depth[y * w + x] = z;
                        double p0 = b0 * iw[0], p1 = b1 * iw[1], p2 = b2 * iw[2], sum = p0 + p1 + p2;
                        var at = TexturedCorner.Blend(corner[0], corner[1], corner[2], p0 / sum, p1 / sum, p2 / sum);
                        var c = Texturing.Fragment(ts, at);
                        // OpenGL rounds to the nearest of 256 levels.
                        pixels[y * w + x] = 0xFF000000u | (uint)Math.Round(c.X * 255) << 16 | (uint)Math.Round(c.Y * 255) << 8 | (uint)Math.Round(c.Z * 255);
                        covered[y * w + x] = true;
                    }
            }
        return (pixels, covered);
    }

    /// <summary>A cylinder wrapped in a checker (its seam in view), and a lit, UV-mapped square with two layers in front of it.</summary>
    private static (ViewScene Scene, ViewCamera Camera) Still()
    {
        TextureImages.Put("checker.png", Checker());
        TextureImages.Put("four.png", Four());
        // A cylinder wrapped in a checker (with its seam in view), and a lit, UV-mapped square in front of it.
        var mesh = new Mesh();
        int sides = 16;
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            mesh.Points.Add(new Vec3(Math.Sin(a), -1, Math.Cos(a)));
            mesh.Points.Add(new Vec3(Math.Sin(a), 1, Math.Cos(a)));
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            mesh.AddPolygon([2 * i, 2 * j, 2 * j + 1, 2 * i + 1]);
        }
        mesh.Touch();
        var square = Square();
        for (int i = 0; i < 4; i++) square.SetPointUV("UV", i, new Vec2(square.Points[i].X * 0.3 + 0.5, square.Points[i].Y * 0.3 + 0.5));
        // Turned to face the camera, between it and the cylinder.
        square.Transform(Mat4.Translation(new Vec3(0.3, 0.2, 1.6)) * Mat4.RotationY(0.4 + Math.PI) * Mat4.Scaling(0.5));
        foreach (var p in square.Polygons) p.Surface = 1;
        mesh.Append(square);
        var can = new Surface("Can") { Color = new Rgb(200, 200, 200), Specular = 0.4, Smoothing = true, SmoothingAngle = 60, Textures = [new TextureLayer
            { Image = "checker.png", Projection = TextureProjection.Cylindrical, Axis = Axis.Y, Size = new Vec3(2, 2, 2), WidthWrap = 2 }] };
        var label = new Surface("Label") { Color = new Rgb(90, 160, 90), Textures =
        [
            new TextureLayer { Image = "four.png", Projection = TextureProjection.UV, UVMap = "UV", Opacity = 0.8 },
            new TextureLayer { Image = "checker.png", Size = new Vec3(0.5, 0.5, 0.5), Blend = TextureBlend.Multiply, Opacity = 0.5, WidthTiling = TextureTiling.Mirror },
        ] };
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = mesh }], Surfaces = [can, label], Style = DisplayStyle.Shaded, ShowGrid = false };
        var cam = new ViewCamera(ViewKind.Perspective) { Heading = 170, Pitch = 15, Distance = 4.5 };
        cam.Resize(240, 180);
        return (scene, cam);
    }

    /// <summary>How many of the pixels both pictures cover agree to within a few levels, of how many, and the worst.</summary>
    private static (int Close, int Compared, int Worst) Agreement(uint[] a, uint[] b, Func<int, bool> covered)
    {
        int compared = 0, close = 0, worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (!covered(i)) continue;
            compared++;
            var (r1, g1, b1) = Rgb8(a[i]);
            var (r2, g2, b2) = Rgb8(b[i]);
            int d = Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
            if (d <= 3) close++;
            worst = Math.Max(worst, d);
        }
        return (close, compared, worst);
    }

    [Fact]
    public void The_opengl_renderers_textured_vertices_and_shader_sums_draw_what_the_software_renderer_does()
    {
        var (scene, cam) = Still();
        var sw = new SoftwareRenderer();
        sw.Render(scene, cam);
        var (gl, covered) = GlPicture(scene, cam);
        var (close, compared, worst) = Agreement(sw.Pixels, gl, i => covered[i] && sw.Pixels[i] != Palette.PerspectiveBackground);
        Assert.True(compared > 8000, $"only {compared} pixels compared");
        // Every pixel agrees to within rounding, bar a few along edges where the two rasterizers split pixels differently.
        Assert.True(close >= compared * 0.99, $"{close} of {compared} agree; the worst is {worst} levels apart");
    }

    [Fact]
    public void The_opengl_renderer_on_a_real_context_draws_textures_as_the_software_renderer_does()
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
        var (close, compared, worst) = Agreement(sw.Pixels, picture, i => sw.Pixels[i] != Palette.PerspectiveBackground && picture[i] != Palette.PerspectiveBackground);
        Assert.True(compared > 8000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.98, $"{close} of {compared} agree; the worst is {worst} levels apart");
    }

    [Fact]
    public void The_opengl_renderer_on_a_real_context_draws_the_uv_views_image_too()
    {
        var scene = new ViewScene { Backdrop = Checker(), ShowGrid = false };
        var cam = new ViewCamera(ViewKind.UV) { Center = new Vec3(0.5, 0.5, 0), PixelsPerUnit = 150 };
        cam.Resize(200, 200);
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
        var (close, compared, worst) = Agreement(sw.Pixels, picture, i => sw.Pixels[i] != Palette.Background);
        Assert.True(compared > 20000, $"only {compared} pixels compared");
        Assert.True(close >= compared * 0.98, $"{close} of {compared} agree; the worst is {worst} levels apart");
    }

    [Fact]
    public void The_uv_view_backdrop_covers_the_unit_square()
    {
        var scene = new ViewScene { Backdrop = Four(), ShowGrid = false };
        var c = new ViewCamera(ViewKind.UV) { Center = new Vec3(0.5, 0.5, 0), PixelsPerUnit = 100 };
        c.Resize(200, 200);
        var r = new SoftwareRenderer();
        r.Render(scene, c);
        // 0–1 fills the middle 100 pixels, dimmed to 80%; outside it the ground shows.
        Near((204, 0, 0), r.Pixels[60 * 200 + 60]);
        Near((204, 204, 204), r.Pixels[140 * 200 + 140]);
        Assert.Equal(Palette.Background, r.Pixels[20 * 200 + 20]);
        Assert.Equal("UV", c.Title);
    }
}
