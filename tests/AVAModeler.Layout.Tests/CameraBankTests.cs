using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>A banked (rolled) camera: Layout's camera view tilts the way the ray-traced picture does.</summary>
public class CameraBankTests
{
    /// <summary>A scene with a long, thin, glowing red bar across the camera's view, 5 m in front of it: a horizon line.</summary>
    private static (Scene.Scene Scene, CameraItem Camera) Horizon(double bank)
    {
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(-2, -0.04, 5), new Vec3(-2, 0.04, 5), new Vec3(2, 0.04, 5), new Vec3(2, -0.04, 5));
        doc.Surfaces[0].Color = new Rgb(255, 0, 0);
        doc.Surfaces[0].Luminosity = 1;
        doc.Surfaces[0].Diffuse = 0;
        doc.Surfaces[0].DoubleSided = true;
        var scene = new Scene.Scene { AmbientIntensity = 0 };
        scene.AddObject("Bar", doc);
        var cam = scene.AddCamera("Camera");
        cam.Width = 320;
        cam.Height = 240;
        cam.SetRotation(0, new Vec3(0, 0, bank));
        return (scene, cam);
    }

    /// <summary>The red pixels' line through the picture: its slope (down the picture per pixel across) and middle.</summary>
    private static (double Slope, double X, double Y, int Count) Line(Func<int, int, (int R, int G, int B)> pixel, int w, int h)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                if (r < 150 || g > 90 || b > 90) continue;
                n++;
                sx += x; sy += y; sxx += (double)x * x; sxy += (double)x * y;
            }
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        return (slope, sx / n, sy / n, n);
    }

    private static (double Slope, double X, double Y, int Count) ViewLine(Scene.Scene scene, CameraItem cam)
    {
        var view = new ViewCamera(ViewKind.Perspective);
        view.Resize(320, 240);
        SceneDrawing.LookThrough(view, cam, 1);
        var drawn = SceneDrawing.Build(scene.Evaluate(1), [], shaded: true);
        drawn.ShowGrid = false;
        drawn.BackfaceTint = false;
        drawn.Style = DisplayStyle.Shaded;
        var r = new SoftwareRenderer();
        r.Render(drawn, view);
        return Line((x, y) => { uint p = r.Pixels[y * 320 + x]; return ((int)(p >> 16) & 0xFF, (int)(p >> 8) & 0xFF, (int)p & 0xFF); }, 320, 240);
    }

    private static (double Slope, double X, double Y, int Count) RenderedLine(Scene.Scene scene, CameraItem cam)
    {
        var image = RayTracer.Render(scene, 1, new RenderSettings { Camera = cam, Width = 320, Height = 240 });
        return Line((x, y) => { var p = image.Pixel(x, y); return (p.R, p.G, p.B); }, 320, 240);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(-35)]
    [InlineData(0)]
    public void A_banked_cameras_view_tilts_the_horizon_as_the_ray_traced_render_does(double bank)
    {
        var (scene, cam) = Horizon(bank);
        var seen = ViewLine(scene, cam);
        var rendered = RenderedLine(scene, cam);
        Assert.True(seen.Count > 200 && rendered.Count > 200, $"The bar is too small: {seen.Count} and {rendered.Count} pixels.");
        // Bank turns the camera's right toward its up, so the world's level line runs down to the right in the picture
        // (screen y grows downward): slope tan(bank), in the view and the render alike.
        double expected = Math.Tan(bank * Math.PI / 180);
        Assert.Equal(expected, seen.Slope, 2);
        Assert.Equal(expected, rendered.Slope, 2);
        Assert.Equal(rendered.Slope, seen.Slope, 2);
        // Both centred where the camera looks.
        Assert.Equal(rendered.X, seen.X, 0);
        Assert.Equal(rendered.Y, seen.Y, 0);
    }

    [Fact]
    public void Looking_through_a_banked_camera_puts_its_own_axes_where_the_render_has_them()
    {
        var scene = Scene.Scene.CreateDefault();
        var cam = scene.Cameras.Single();
        cam.SetPosition(0, new Vec3(3, 2, -5));
        var target = new Vec3(0, 0.3, 0);
        var aim = cam.RotationToward(target, 1);
        cam.SetRotation(0, new Vec3(aim.X, aim.Y, 30));
        var view = new ViewCamera(ViewKind.Perspective);
        view.Resize(640, 480);
        SceneDrawing.LookThrough(view, cam, 1);
        Assert.Equal(30, view.Bank, 6);
        var toWorld = cam.CameraToWorld(1);
        // The camera's right (+X) is straight to the right in the picture and its up (+Y) straight up, bank and all.
        var middle = view.Project(target);
        var right = view.Project(target + toWorld.TransformVector(Vec3.UnitX));
        var up = view.Project(target + toWorld.TransformVector(Vec3.UnitY));
        Assert.Equal(320, middle.X, 6);
        Assert.Equal(240, middle.Y, 6);
        Assert.True(right.X > 330);
        Assert.Equal(middle.Y, right.Y, 6);
        Assert.True(up.Y < 230);
        Assert.Equal(middle.X, up.X, 6);
    }

    [Fact]
    public void A_free_view_has_no_bank_unless_given()
    {
        var view = new ViewCamera(ViewKind.Perspective) { Heading = 30, Pitch = 10 };
        var (r, u, f) = view.Basis();
        Assert.Equal(0, r.Y, 12);
        view.Bank = 90;
        var (r2, u2, f2) = view.Basis();
        // A quarter turn: right becomes the old up, up the old left; the line of sight stays.
        Assert.True((r2 - u).Length < 1e-12);
        Assert.True((u2 + r).Length < 1e-12);
        Assert.True((f2 - f).Length < 1e-12);
    }
}
