using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

internal static class Ui
{
    public static MainWindow Open(int width = 1400, int height = 900)
    {
        var w = new MainWindow([], new Preferences(), loadPlugins: false) { Width = width, Height = height };
        w.Show();
        Pump();
        return w;
    }

    public static void Pump()
    {
        for (int i = 0; i < 12; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A point in a view, in the window's coordinates.</summary>
    public static Point In(Window w, Control view, double x, double y) => view.TranslatePoint(new Point(x, y), w)!.Value;

    /// <summary>The middle of a control, in its own coordinates (its Bounds are in its parent's).</summary>
    public static Point Mid(Control c) => new(c.Bounds.Width / 2, c.Bounds.Height / 2);

    /// <summary>One of the four views by its place in the quad (whatever it's showing).</summary>
    public static ViewportControl View(MainWindow w, ViewKind kind) => w.Modeler.Views.Views.Single(v => v.Kind == kind);

    public static void Drag(Window w, Point from, Point to, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        // While it's held, the button is one of the move events' modifiers.
        var held = mods | button switch
        {
            MouseButton.Right => RawInputModifiers.RightMouseButton,
            MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
            _ => RawInputModifiers.LeftMouseButton,
        };
        w.MouseMove(from, mods);
        w.MouseDown(from, button, mods);
        for (int i = 1; i <= 6; i++) w.MouseMove(from + (to - from) * (i / 6.0), held);
        w.MouseUp(to, button, held);
        Pump();
    }

    public static void Click(Window w, Point at, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.MouseMove(at, mods);
        w.MouseDown(at, MouseButton.Left, mods);
        w.MouseUp(at, MouseButton.Left, mods);
        Pump();
    }

    public static void Key(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "scripts", name);

    /// <summary>Frames every view on what's there.</summary>
    public static void Fit(MainWindow w)
    {
        w.Modeler.Views.FitAll();
        Pump();
    }
}

public class WindowTests
{
    [AvaloniaFact]
    public void The_window_opens_with_the_quad_view_tabs_and_numeric_panel()
    {
        var w = Ui.Open();
        Assert.NotNull(w.CaptureRenderedFrame());
        Assert.Equal(4, w.Modeler.Views.Views.Count);
        Assert.All(w.Modeler.Views.Views, v => Assert.True(v.Bounds.Width > 200));
        Assert.StartsWith("AVA Modeler", w.Title);
        Assert.Contains("Box", w.Modeler.Tabs.Column.Children.OfType<Button>().Select(b => b.Content as string));
    }

    [AvaloniaFact]
    public void A_box_dragged_in_the_front_view_then_its_depth_in_the_top_view()
    {
        var w = Ui.Open();
        var front = Ui.View(w, ViewKind.Front);
        w.Modeler.PickTool("Box");
        Assert.IsType<BoxTool>(w.Session.ActiveTool);
        var c = Ui.Mid(front);
        Ui.Drag(w, Ui.In(w, front, c.X - 80, c.Y + 40), Ui.In(w, front, c.X + 80, c.Y - 40));
        var box = (BoxTool)w.Session.ActiveTool!;
        Assert.Equal(160 / front.Camera.PixelsPerUnit, box.Size.X, 6);
        Assert.Equal(0, box.Size.Z);
        var top = Ui.View(w, ViewKind.Top);
        var t = Ui.Mid(top);
        Ui.Drag(w, Ui.In(w, top, t.X - 80, t.Y - 30), Ui.In(w, top, t.X + 80, t.Y + 30));
        Assert.Equal(60 / top.Camera.PixelsPerUnit, box.Size.Z, 6);
        Ui.Key(w, Key.Enter);
        Assert.Null(w.Session.ActiveTool);
        Assert.Equal(6, w.Session.Document.Current.Mesh.Polygons.Count);
        Assert.Equal("Box", w.Session.Document.Undo.UndoLabel);
    }

    [AvaloniaFact]
    public void Clicking_selects_points_and_polygons_and_a_box_drag_selects_many()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["SegmentsX"] = 2 });
        Ui.Fit(w);
        var front = Ui.View(w, ViewKind.Front);
        var c = front.Camera.Project(new Vec3(-0.25, 0, -0.5));
        Ui.Key(w, Key.Space); // Points → Polygons
        Assert.Equal(SelectionMode.Polygons, w.Session.Document.Selection.Mode);
        Ui.Click(w, Ui.In(w, front, c.X, c.Y));
        var picked = Assert.Single(w.Session.Document.Selection.PolygonsIn(0));
        Assert.True(w.Session.Document.Current.Mesh.Normal(picked).Z < -0.9);
        Ui.Click(w, Ui.In(w, front, 2, 2));
        Assert.Empty(w.Session.Document.Selection.PolygonsIn(0));
        w.Session.SetSelectionMode(SelectionMode.Points);
        var a = front.Camera.Project(new Vec3(-0.55, -0.55, 0));
        var b = front.Camera.Project(new Vec3(0.1, 0.55, 0));
        Ui.Drag(w, Ui.In(w, front, a.X, a.Y), Ui.In(w, front, b.X, b.Y));
        Assert.True(8 == w.Session.Document.Selection.PointsIn(0).Count, $"{w.Session.Document.Selection.Mode} {a} {b} active {w.Session.ActiveTool} {w.Session.Counts()} fg {string.Join(",", w.Session.Document.ForegroundLayers)}"); // the left and middle columns, front and back
        Assert.Contains("8 of 12 points", w.Session.Counts());
    }

    [AvaloniaFact]
    public void Keys_arm_tools_and_a_drag_moves_the_selection_one_undo_step_at_a_time()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        Ui.Fit(w);
        Ui.Key(w, Key.T);
        Assert.IsType<MoveTool>(w.Session.ActiveTool);
        var front = Ui.View(w, ViewKind.Front);
        var c = Ui.Mid(front);
        Ui.Drag(w, Ui.In(w, front, c.X, c.Y), Ui.In(w, front, c.X + 50, c.Y));
        Ui.Drag(w, Ui.In(w, front, c.X, c.Y), Ui.In(w, front, c.X, c.Y + 50));
        Ui.Key(w, Key.Enter);
        var bounds = w.Session.Document.Current.Mesh.Bounds().Center;
        Assert.Equal(50 / front.Camera.PixelsPerUnit, bounds.X, 6);
        Assert.Equal(-50 / front.Camera.PixelsPerUnit, bounds.Y, 6);
        Assert.Equal(3, w.Session.Document.Undo.Count);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal(0, w.Session.Document.Current.Mesh.Bounds().Center.Y, 9);
        Ui.Key(w, Key.Y);
        Assert.IsType<RotateTool>(w.Session.ActiveTool);
        Ui.Key(w, Key.Escape);
        Assert.Null(w.Session.ActiveTool);
    }

    [AvaloniaFact]
    public void File_quit_closes_the_window()
    {
        var w = Ui.Open();
        var file = w.Modeler.Menu.Items.OfType<MenuItem>().First();
        Assert.Contains(file.Items.OfType<MenuItem>(), i => i.Header as string == "_Quit");
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        // Only the press: the window is gone before a release could reach it.
        w.KeyPress(Key.Q, RawInputModifiers.Control, PhysicalKey.None, null);
        Ui.Pump();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void The_wheel_zooms_and_a_double_click_maximizes()
    {
        var w = Ui.Open();
        var top = Ui.View(w, ViewKind.Top);
        double before = top.Camera.PixelsPerUnit;
        w.MouseWheel(Ui.In(w, top, top.Bounds.Width / 2, top.Bounds.Height / 2), new Vector(0, 2));
        Ui.Pump();
        Assert.True(top.Camera.PixelsPerUnit > before);
        w.Modeler.Views.ToggleMaximize(top);
        Ui.Pump();
        Assert.Equal(top, w.Modeler.Views.Maximized);
        Assert.False(Ui.View(w, ViewKind.Front).IsVisible);
        w.Modeler.Views.ToggleMaximize(top);
        Assert.Null(w.Modeler.Views.Maximized);
    }

    [AvaloniaFact]
    public void A_requester_tool_shows_its_numbers_then_applies()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        w.Modeler.PickTool("Subdivide");
        Assert.Null(w.Session.ActiveTool);
        var tool = Assert.IsType<SubdivideTool>(w.Modeler.Numeric.Tool);
        tool.Mode = SubdivideMode.Smooth;
        w.Session.Run(tool);
        Assert.Equal(24, w.Session.Document.Current.Mesh.Polygons.Count);
    }

    [AvaloniaFact]
    public void Layers_switch_with_the_number_keys_and_show_the_background()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        Ui.Key(w, Key.D2);
        Assert.Equal([1], w.Session.Document.ForegroundLayers);
        Ui.Key(w, Key.D1, RawInputModifiers.Alt);
        Assert.Equal([0], w.Session.Document.BackgroundLayers);
        var scene = w.Session.BuildScene(false);
        Assert.True(Assert.Single(scene.Layers).Background);
    }

    [AvaloniaFact]
    public void Metanurbs_shows_the_smoothed_cage()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        Ui.Key(w, Key.Tab);
        Assert.True(w.Session.MetaNurbs);
        var layer = w.Session.BuildScene(true).Layers.Single();
        Assert.NotNull(layer.Subdivided);
        Assert.True(layer.Subdivided!.Polygons.Count > 6);
    }

    [AvaloniaFact]
    public void A_macro_runs_from_the_workspace()
    {
        var w = Ui.Open();
        // Pump the UI thread until it's done (waiting on it would block the thread it needs).
        var run = w.Modeler.RunMacroFile(Ui.Sample("spaceship.lua"));
        for (int i = 0; i < 2000 && !run.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(1); }
        Assert.True(run.IsCompleted);
        Assert.True(w.Session.Document.Current.Mesh.Polygons.Count > 100);
        Assert.Contains("Spaceship", w.Session.Status);
    }
}

public class SessionTests
{
    [Fact]
    public void The_key_map_has_classic_defaults_and_takes_overrides()
    {
        var k = new Keymap();
        Assert.Equal("Move", k.CommandFor("T"));
        Assert.Equal("Box", k.CommandFor("shift+x"));
        Assert.Equal("Undo", k.CommandFor("Ctrl+Z"));
        Assert.Equal("Ctrl+Shift+Z", Keymap.Normalise("shift+ctrl+z"));
        var custom = new Keymap(new Dictionary<string, string> { ["Box"] = "B" });
        Assert.Equal("Box", custom.CommandFor("B"));
        Assert.Null(custom.CommandFor("Shift+X"));
    }

    [Fact]
    public void Preferences_round_trip_and_keep_recent_files()
    {
        var path = Path.Combine(Path.GetTempPath(), "avamodeler-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var p = new Preferences { Renderer = "OpenGL", UndoLevels = 42 };
            for (int i = 0; i < 12; i++) p.AddRecent($"/tmp/{i}.lwo");
            p.AddRecent("/tmp/3.lwo");
            p.Save(path);
            var q = Preferences.Load(path);
            Assert.Equal("OpenGL", q.Renderer);
            Assert.Equal(42, q.UndoLevels);
            Assert.Equal(10, q.RecentFiles.Count);
            Assert.Equal("/tmp/3.lwo", q.RecentFiles[0]);
            File.WriteAllText(path, "{ not json");
            Assert.Equal("Software", Preferences.Load(path).Renderer);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tools_activate_by_kind()
    {
        var s = new ModelerSession();
        Assert.True(s.Activate("Move"));
        Assert.IsType<MoveTool>(s.ActiveTool);
        s.CancelTool();
        s.Commands.Run("Box");
        Assert.True(s.Activate("Flip"));
        Assert.Null(s.ActiveTool);
        Assert.Equal("Flip", s.Document.Undo.UndoLabel);
        Assert.False(s.Activate("Jitter"));
        s.CycleSelectionMode();
        Assert.Equal(SelectionMode.Polygons, s.Document.Selection.Mode);
        Assert.False(s.Run(new BooleanTool()));
        Assert.Contains("background", s.Status);
    }
}

public class LayoutTests
{
    [AvaloniaFact]
    public void Layout_shows_the_modeler_object_live_and_keys_it()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Ball");
        w.ShowLayout();
        Ui.Pump();
        var layout = Assert.IsType<LayoutWorkspace>(w.Content);
        var obj = layout.Session.Scene.Objects.Single(o => ReferenceEquals(o.Document, w.Session.Document));
        Assert.Equal(obj, layout.Session.Selected);
        layout.Session.SetFrame(1);
        layout.Session.SetVector(obj, 0, new Vec3(0, 0, 0));
        layout.Session.SetFrame(30);
        layout.Session.SetVector(obj, 0, new Vec3(2, 0, 0));
        layout.Session.SetFrame(15);
        double x = obj.PositionAt(15).X;
        Assert.InRange(x, 0.2, 1.8);
        Assert.Equal([0.0, 1.0, 30.0], obj.KeyFrames()); // a new channel starts with a key at frame 0, as in LightWave
        // The Modeler's edits show at once.
        int before = obj.BaseMesh().Polygons.Count;
        w.ShowModeler();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Center"] = new[] { 2.0, 0, 0 } });
        Assert.Equal(before + 6, obj.BaseMesh().Polygons.Count);
    }

    [AvaloniaFact]
    public void Clicking_an_item_in_the_view_chooses_it()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        w.ShowLayout();
        var layout = (LayoutWorkspace)w.Content!;
        layout.View.Mode = LayoutViewMode.Front;
        layout.View.Fit();
        Ui.Pump();
        var light = layout.Session.Scene.Lights.First();
        var at = layout.View.Camera.Project(light.WorldPosition(layout.Session.Frame));
        Assert.Equal(light, layout.View.Pick(new Point(at.X, at.Y)));
        var box = layout.View.Camera.Project(new Vec3(0.1, 0.1, -0.5));
        Assert.IsType<ObjectItem>(layout.View.Pick(new Point(box.X, box.Y)));
    }

    [Fact]
    public void A_frame_renders_through_the_scene_camera()
    {
        var s = new LayoutSession();
        var doc = new Document();
        new BallTool().Run(new ToolContext(doc));
        var obj = s.ShowModelerObject(doc);
        Assert.Equal(obj, s.Selected);
        var img = RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 64, Height = 48 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(64 * 48 * 4, img.Rgba.Length);
        var centre = img.Pixel(32, 24);
        var corner = img.Pixel(1, 1);
        Assert.NotEqual(centre, corner);
    }

    [Fact]
    public void Scenes_save_and_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avamodeler-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new LayoutSession();
            var doc = new Document();
            new BoxTool().Run(new ToolContext(doc));
            var obj = s.ShowModelerObject(doc);
            s.SetVector(obj, 0, new Vec3(1, 2, 3));
            s.SaveScene(Path.Combine(dir, "a.avascene"));
            var t = new LayoutSession();
            t.LoadScene(Path.Combine(dir, "a.avascene"));
            var back = t.Scene.Objects.Single();
            Assert.Equal(new Vec3(1, 2, 3), back.PositionAt(1));
            Assert.Equal(6, back.BaseMesh().Polygons.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
