using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>
/// Layout's view drawn with OpenGL on a real context (<see cref="OffscreenGl"/>) against the software picture: the
/// objects' shaded faces, the items' drawings (overlay lines), the chosen object's yellow box and the grid, in a
/// perspective and a flat view. Where there's no OpenGL (no EGL, no driver), they skip.
/// </summary>
public class LayoutGlTests
{
    private const int W = 320, H = 240;

    /// <summary>A box object (chosen, so boxed in yellow), the default camera and light, and a null, at frame 1.</summary>
    private static (ViewScene Scene, SceneFrame Frame) Staged()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        var c = new[]
        {
            new Vec3(-1, 0, -1), new Vec3(1, 0, -1), new Vec3(1, 2, -1), new Vec3(-1, 2, -1),
            new Vec3(-1, 0, 1), new Vec3(1, 0, 1), new Vec3(1, 2, 1), new Vec3(-1, 2, 1),
        };
        foreach (var p in c) m.AddPoint(p);
        // Clockwise seen from outside, as LightWave winds them.
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]];
        foreach (var f in faces) m.Polygons.Add(new Polygon(f, 0));
        m.Touch();
        var s = new LayoutSession();
        var obj = s.ShowModelerObject(doc);
        var n = new NullItem("Marker");
        s.Add(n);
        s.SetVector(n, 0, new Vec3(2.5, 1, 0));
        s.Select(obj);
        var frame = s.Scene.Evaluate(s.Frame);
        return (s.BuildScene(frame, shaded: true), frame);
    }

    private static ViewCamera Camera(ViewKind kind, SceneFrame frame)
    {
        var cam = kind == ViewKind.Perspective ? new ViewCamera(kind) { Distance = 8 } : new ViewCamera(kind) { PixelsPerUnit = 50 };
        cam.Resize(W, H);
        // The perspective view close on the object, for its faces; the flat views take in every item.
        cam.Fit(kind == ViewKind.Perspective ? frame.Objects[0].WorldMesh.Bounds().Expanded(0.5) : SceneDrawing.Bounds(frame));
        return cam;
    }

    private static (int R, int G, int B) Rgb8(uint c) => ((int)(c >> 16) & 0xFF, (int)(c >> 8) & 0xFF, (int)c & 0xFF);

    private static int Distance(uint a, uint b)
    {
        var (r1, g1, b1) = Rgb8(a);
        var (r2, g2, b2) = Rgb8(b);
        return Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
    }

    private static uint[] Software(ViewScene scene, ViewCamera cam)
    {
        var r = new SoftwareRenderer();
        r.Render(scene, cam);
        return [.. r.Pixels];
    }

    /// <summary>Pixels within two of one where <paramref name="hit"/> holds.</summary>
    private static bool[] Near(uint[] pixels, Func<uint, bool> hit, int reach = 2)
    {
        var near = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (!hit(pixels[i])) continue;
            int x = i % W, y = i / W;
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (x + dx >= 0 && x + dx < W && y + dy >= 0 && y + dy < H) near[(y + dy) * W + x + dx] = true;
        }
        return near;
    }

    /// <summary>
    /// The two pictures of one view compared: the faces (away from every line) agree to within a few levels, and the
    /// lines of each colour asked for are drawn in both, in the same places.
    /// </summary>
    private static void Compare(ViewScene scene, ViewCamera cam, uint[] gl, params (string Name, uint Color)[] lines)
    {
        var sw = Software(scene, cam);
        uint bg = cam.IsPerspective ? Palette.PerspectiveBackground : Palette.Background;
        // The faces alone (no grid, wire or overlay), to tell faces from lines.
        var bare = Software(new ViewScene { Layers = scene.Layers, Surfaces = scene.Surfaces, Style = DisplayStyle.Shaded, ShowGrid = false }, cam);
        var lined = Near(sw, _ => false);
        for (int i = 0; i < sw.Length; i++)
            if (sw[i] != bare[i])
            {
                int x = i % W, y = i / W;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        if (x + dx >= 0 && x + dx < W && y + dy >= 0 && y + dy < H) lined[(y + dy) * W + x + dx] = true;
            }
        int compared = 0, close = 0;
        for (int i = 0; i < sw.Length; i++)
        {
            if (lined[i] || bare[i] == bg) continue;
            compared++;
            if (Distance(sw[i], gl[i]) <= 4) close++;
        }
        if (cam.IsPerspective)
        {
            Assert.True(compared > 2000, $"only {compared} face pixels compared");
            Assert.True(close >= compared * 0.95, $"{close} of {compared} face pixels agree");
        }
        // Coverage: what isn't background in one isn't in the other, bar the lines' edges.
        int swCovered = sw.Count(p => p != bg), glCovered = gl.Count(p => p != bg);
        Assert.InRange(glCovered, swCovered * 0.6, swCovered * 1.4);
        foreach (var (name, color) in lines)
        {
            bool Hit(uint p) => Distance(p, color) <= 40;
            int inSw = sw.Count(Hit), inGl = gl.Count(Hit);
            Assert.True(inSw > 20, $"the software picture has only {inSw} pixels of {name}");
            Assert.True(inGl > 20, $"the OpenGL picture has only {inGl} pixels of {name}");
            // OpenGL draws one-pixel lines, the software renderer wider antialiased ones: much the same amount, in the same places.
            Assert.InRange(inGl, inSw * 0.25, inSw * 2.5);
            var nearSw = Near(sw, Hit);
            int placed = 0;
            for (int i = 0; i < gl.Length; i++)
                if (Hit(gl[i]) && nearSw[i]) placed++;
            Assert.True(placed >= inGl * 0.85, $"{placed} of {inGl} OpenGL pixels of {name} are where the software picture has them");
        }
    }

    [Theory]
    [InlineData(ViewKind.Perspective)]
    [InlineData(ViewKind.Top)]
    [InlineData(ViewKind.Front)]
    public void The_opengl_renderer_draws_a_layout_scene_as_the_software_renderer_does(ViewKind kind)
    {
        var (scene, frame) = Staged();
        var cam = Camera(kind, frame);
        using var gl = OffscreenGl.TryCreate(W, H, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        using var renderer = new GlRenderer(gl.Gl, gl.IsEs);
        renderer.Render(scene, cam);
        var picture = gl.Read();
        (string, uint)[] lines = cam.IsPerspective
            ? [("the chosen object's box", Palette.Selected)]
            : [("the chosen object's box", Palette.Selected), ("the light", SceneDrawing.Color(new LightItem("L"))), ("the camera", SceneDrawing.Color(new CameraItem("C")))];
        Compare(scene, cam, picture, lines);
        // The grid shows in both (a flat view's grid lines span the picture).
        if (!cam.IsPerspective)
        {
            var sw = Software(scene, cam);
            int Grid(uint[] px) => px.Count(p => p == Palette.Grid || p == Palette.GridMajor || p == Palette.Axis);
            Assert.True(Grid(sw) > 200 && Grid(picture) > 200, $"grid pixels: {Grid(sw)} in software, {Grid(picture)} in OpenGL");
        }
    }

    [Fact]
    public void An_offscreen_context_is_had_or_the_reason_given()
    {
        using var gl = OffscreenGl.TryCreate(16, 16, out string why);
        if (gl == null)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            return;
        }
        Assert.Equal(16, gl.Width);
        Assert.Equal(16 * 16, gl.Read().Length);
    }
}
