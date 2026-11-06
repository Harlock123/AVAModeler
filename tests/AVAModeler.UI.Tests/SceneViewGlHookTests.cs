using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>
/// Layout's view for a host that draws the scene with OpenGL underneath: <see cref="SceneView.DrawsScene"/> off draws no
/// picture (what's below shows through) yet the mouse works as ever; <see cref="SceneView.CurrentView"/> gives what it
/// would draw; and a <see cref="GlSurface"/> where OpenGL can't start (headless) draws nothing and never says it's ready.
/// </summary>
public class SceneViewGlHookTests
{
    private static (Window Window, SceneView View, LayoutSession Session, ObjectItem Box, Border Under) Stage(bool drawsScene)
    {
        var session = new LayoutSession();
        var doc = new Document();
        new BoxTool().Run(new ToolContext(doc));
        var box = session.ShowModelerObject(doc);
        var view = new SceneView(session) { Mode = LayoutViewMode.Front, DrawsScene = drawsScene };
        // A red layer under the view, where a host's OpenGL picture would be.
        var under = new Border { Background = Brushes.Red };
        var w = new Window { Width = 640, Height = 480, Content = new Panel { Children = { under, view } } };
        w.Show();
        Ui.Pump();
        view.Fit();
        Ui.Pump();
        return (w, view, session, box, under);
    }

    /// <summary>How many of every fourth pixel (across and down) of the window's picture are pure red.</summary>
    private static int RedPixels(Window w)
    {
        using var frame = w.CaptureRenderedFrame()!;
        int width = frame.PixelSize.Width, height = frame.PixelSize.Height;
        var bytes = new byte[width * height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, width * 4); }
        finally { handle.Free(); }
        bool rgba = frame.Format == Avalonia.Platform.PixelFormats.Rgba8888;
        int red = 0;
        for (int y = 0; y < height; y += 4)
            for (int x = 0; x < width; x += 4)
            {
                int i = (y * width + x) * 4;
                byte r = bytes[i + (rgba ? 0 : 2)], g = bytes[i + 1], b = bytes[i + (rgba ? 2 : 0)];
                if (r > 200 && g < 40 && b < 40) red++;
            }
        return red;
    }

    [AvaloniaFact]
    public void With_draws_scene_off_nothing_is_drawn_and_what_is_below_shows()
    {
        var (w, view, _, _, _) = Stage(drawsScene: true);
        Assert.True(view.ScenesDrawn > 0);
        Assert.Equal(0, RedPixels(w));

        var (w2, view2, _, _, _) = Stage(drawsScene: false);
        int redrawn = 0;
        view2.Redrawn += () => redrawn++;
        view2.InvalidateVisual();
        int red = RedPixels(w2);
        Assert.Equal(0, view2.ScenesDrawn);
        Assert.True(redrawn > 0);
        // Nearly the whole view is the layer below (bar the label in the corner).
        Assert.True(red > 640 * 480 / 16 * 0.9, $"only {red} red samples");
        // Turned back on, it draws again.
        view2.DrawsScene = true;
        Assert.Equal(0, RedPixels(w2));
        Assert.True(view2.ScenesDrawn > 0);
    }

    [AvaloniaFact]
    public void With_draws_scene_off_the_mouse_still_picks_and_drags()
    {
        var (w, view, session, box, _) = Stage(drawsScene: false);
        session.Select(null);
        // On the box, away from the camera and light drawn near the middle.
        var s = view.Camera.Project(new Vec3(0.35, -0.35, 0));
        var at = Ui.In(w, view, s.X, s.Y);
        Ui.Click(w, at);
        Assert.Same(box, session.Selected);
        Ui.Drag(w, at, at + new Point(60, 0));
        Assert.True(box.PositionAt(session.Frame).X > 0.1);
        Assert.Equal(0, view.ScenesDrawn);
    }

    [AvaloniaFact]
    public void Current_view_is_what_the_view_draws()
    {
        var (_, view, session, box, _) = Stage(drawsScene: true);
        session.Select(box);
        var (scene, camera) = view.CurrentView();
        Assert.Same(view.Camera, camera);
        Assert.Equal(ViewKind.Front, camera.Kind);
        Assert.Equal((int)view.Bounds.Width, camera.Width);
        Assert.True(scene.ShowGrid);
        Assert.Single(scene.Layers);
        // The chosen object's yellow box and the camera's and light's drawings are overlay lines.
        Assert.Contains(scene.Overlay, l => l.Color == Palette.Selected);
        Assert.Contains(scene.Overlay, l => l.Color == SceneDrawing.Color(session.Scene.Lights.First()));
        // Software drawn from it is the view's own picture.
        var r = new SoftwareRenderer();
        r.Render(scene, camera);
        view.InvalidateVisual();
        Ui.Pump();
        using var _ = ((Window)TopLevel.GetTopLevel(view)!).CaptureRenderedFrame();
        Assert.Equal(r.Pixels, view.Renderer.Pixels);
    }

    [AvaloniaFact]
    public void A_gl_surface_where_opengl_cant_start_draws_nothing_and_isnt_ready()
    {
        // Headless there's no OpenGL: Avalonia never starts the context, so the surface stays not ready, raises
        // nothing, and the layer below shows; a host falls back to software on Ready being false.
        var session = new LayoutSession();
        var view = new SceneView(session) { DrawsScene = false };
        bool failed = false, initialised = false;
        var gl = new GlSurface { Scene = () => view.CurrentView().Scene, CameraSource = () => view.Camera };
        gl.Failed += _ => failed = true;
        gl.Initialised += () => initialised = true;
        view.Redrawn += gl.RequestNextFrameRendering;
        var w = new Window { Width = 320, Height = 240, Content = new Panel { Background = Brushes.Red, Children = { gl, view } } };
        w.Show();
        for (int i = 0; i < 5; i++)
        {
            Ui.Pump();
            using var _ = w.CaptureRenderedFrame();
        }
        Assert.False(gl.Ready);
        Assert.False(initialised);
        Assert.False(failed);
        Assert.Null(gl.Description);
        Assert.True(RedPixels(w) > 320 * 240 / 16 * 0.9);
    }
}
