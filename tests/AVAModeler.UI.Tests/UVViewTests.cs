using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

public class UVViewTests
{
    /// <summary>A window with a 2 × 1 × 1 box laid out front-planar in the UV map "UV", the Side view showing it.</summary>
    private static (MainWindow W, ViewportControl UV) Mapped()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 1, 1 } });
        w.Session.Commands.MakeUVs("UV");
        Ui.Fit(w);
        var side = Ui.View(w, ViewKind.Side);
        w.Modeler.Views.ToggleUV(side);
        Ui.Pump();
        side.FitAll();
        Ui.Pump();
        return (w, side);
    }

    /// <summary>Where a UV lands in the window.</summary>
    private static Point At(MainWindow w, ViewportControl view, double u, double v)
    {
        var s = view.Camera.Project(new Vec3(u, v, 0));
        return Ui.In(w, view, s.X, s.Y);
    }

    [AvaloniaFact]
    public void The_u_key_switches_the_view_under_the_mouse_to_the_uv_map_and_back()
    {
        var w = Ui.Open();
        var top = Ui.View(w, ViewKind.Top);
        w.MouseMove(Ui.In(w, top, 50, 50));
        Ui.Key(w, Key.U);
        Assert.True(top.IsUV);
        Assert.Equal(ViewKind.UV, top.Camera.Kind);
        Assert.Equal(ViewKind.Top, top.Kind);
        Assert.Same(top, w.Modeler.Views.UVView);
        Assert.NotNull(w.CaptureRenderedFrame());
        Ui.Key(w, Key.U);
        Assert.False(top.IsUV);
        Assert.Equal(ViewKind.Top, top.Camera.Kind);
        // From the menu, with the mouse elsewhere, the Side view takes it.
        w.MouseMove(new Point(2, 2));
        w.Modeler.Execute("UV View");
        Assert.True(Ui.View(w, ViewKind.Side).IsUV);
    }

    [AvaloniaFact]
    public void Clicking_and_dragging_in_the_uv_view_select_in_the_model()
    {
        var (w, uv) = Mapped();
        var doc = w.Session.Document;
        var mesh = doc.Current.Mesh;
        // The corner at the image's bottom left is a point at x = -1, y = -0.5 (front and back share it).
        Ui.Click(w, At(w, uv, 0, 0));
        var picked = Assert.Single(doc.Selection.PointsIn(0));
        Assert.Equal(-1, mesh.Points[picked].X, 9);
        Assert.Equal(-0.5, mesh.Points[picked].Y, 9);
        // A box round the left half takes the points there.
        Ui.Drag(w, At(w, uv, -0.04, -0.04), At(w, uv, 0.3, 1.04));
        Assert.All(doc.Selection.PointsIn(0), p => Assert.True(mesh.Points[p].X < 0));
        Assert.Equal(4, doc.Selection.PointsIn(0).Count);
        // In Polygons mode a click picks a face.
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        Ui.Click(w, At(w, uv, 0.75, 0.5));
        Assert.NotEmpty(doc.Selection.PolygonsIn(0));
        Assert.All(doc.Selection.PolygonsIn(0), p => Assert.True(mesh.Centroid(mesh.Polygons[p]).X >= 0));
    }

    [AvaloniaFact]
    public void Moving_points_in_the_uv_view_moves_their_uvs_as_one_undo_step()
    {
        var (w, uv) = Mapped();
        var doc = w.Session.Document;
        var mesh = doc.Current.Mesh;
        var map = mesh.FindUVMap("UV")!;
        var before = map.Points.ToDictionary();
        Ui.Click(w, At(w, uv, 1, 1));
        int p = doc.Selection.PointsIn(0).First();
        w.MouseMove(At(w, uv, 0.5, 0.5));
        Ui.Key(w, Key.T);
        Assert.IsType<MoveTool>(w.Session.ActiveTool);
        Ui.Drag(w, At(w, uv, 0.5, 0.5), At(w, uv, 0.25, 0.4));
        Assert.True(w.Session.InUVEdit);
        // The points stay where they were in the model; their UVs moved by the drag.
        var moved = doc.Current.Mesh.FindUVMap("UV")!.Points[p] - (before[p] + new Vec2(-0.25, -0.1));
        Assert.True(moved.Length < 1e-9, $"{moved}");
        Assert.Equal(1, doc.Current.Mesh.Bounds().Max.X, 9);
        Ui.Key(w, Key.Enter);
        Assert.False(w.Session.InUVEdit);
        Assert.Equal("Move UVs", doc.Undo.UndoLabel);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal(before, w.Session.Document.Current.Mesh.FindUVMap("UV")!.Points);
    }

    [AvaloniaFact]
    public void A_tool_used_in_the_uv_view_then_a_model_view_makes_a_step_in_each()
    {
        var (w, uv) = Mapped();
        var doc = w.Session.Document;
        var before = doc.Current.Mesh.FindUVMap("UV")!.Points.ToDictionary();
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        w.Modeler.PickTool("Size");
        Ui.Drag(w, At(w, uv, 0.5, 0.5), At(w, uv, 0.8, 0.5));
        var front = Ui.View(w, ViewKind.Front);
        var c = Ui.Mid(front);
        Ui.Drag(w, Ui.In(w, front, c.X, c.Y), Ui.In(w, front, c.X + 40, c.Y));
        Ui.Key(w, Key.Enter);
        // The UVs were sized about their middle, then the box itself: two steps after the box and its UVs.
        Assert.Equal(4, doc.Undo.Count);
        Assert.True(doc.Current.Mesh.Bounds().Size.X > 2.1);
        var map = doc.Current.Mesh.FindUVMap("UV")!;
        Assert.True(map.Points.Values.Max(p => p.X) > 1.05);
        doc.UndoLast();
        Assert.Equal("Size UVs", doc.Undo.UndoLabel);
        doc.UndoLast();
        Assert.Equal(before, doc.Current.Mesh.FindUVMap("UV")!.Points);
    }

    [AvaloniaFact]
    public void A_tool_that_makes_geometry_is_turned_away_from_the_uv_view()
    {
        var (w, uv) = Mapped();
        int polygons = w.Session.Document.Current.Mesh.Polygons.Count;
        w.Modeler.PickTool("Box");
        Ui.Drag(w, At(w, uv, 0.2, 0.2), At(w, uv, 0.6, 0.6));
        Assert.Null(w.Session.ActiveTool);
        Assert.Contains("UV view", w.Session.Status);
        Assert.Equal(polygons, w.Session.Document.Current.Mesh.Polygons.Count);
        // With no UV map at all, nothing happens but a note.
        var w2 = Ui.Open();
        w2.Session.Commands.Run("Box");
        var side = Ui.View(w2, ViewKind.Side);
        w2.Modeler.Views.ToggleUV(side);
        w2.Modeler.PickTool("Move");
        Ui.Drag(w2, Ui.In(w2, side, 50, 50), Ui.In(w2, side, 80, 80));
        Assert.Contains("Make UVs", w2.Session.Status);
    }

    [AvaloniaFact]
    public void The_uv_view_draws_the_map_over_its_texture()
    {
        var (w, uv) = Mapped();
        TextureImages.Put("uv-view-red.png", new TextureImage(1, 1, [255, 0, 0, 255]));
        w.Session.Commands.SetTexture("Default", "uv-view-red.png", setup: t => { t.Projection = TextureProjection.UV; t.UVMap = "UV"; });
        Ui.Pump();
        Assert.NotNull(w.CaptureRenderedFrame());
        var scene = w.Session.BuildUVScene(out var space);
        Assert.NotNull(space);
        Assert.NotNull(scene.Backdrop);
        Assert.Equal(4, scene.Overlay.Count);
        var mid = uv.Camera.Project(new Vec3(0.3, 0.3, 0));
        uint px = uv.Software!.Pixels[(int)mid.Y * uv.Software.Width + (int)mid.X];
        Assert.True(((px >> 16) & 0xFF) > 150 && ((px >> 8) & 0xFF) < 40, $"{px:X8}");
        w.Modeler.Execute("Next UV Map");
        Assert.Equal("UV", w.Session.Document.CurrentUVMap);
    }

    [AvaloniaFact]
    public void The_perspective_view_shows_textures()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 } });
        TextureImages.Put("persp-green.png", new TextureImage(1, 1, [0, 255, 0, 255]));
        w.Session.Commands.SetTexture("Default", "persp-green.png", setup: t => t.Size = new Vec3(2, 2, 2));
        Ui.Fit(w);
        var persp = Ui.View(w, ViewKind.Perspective);
        persp.Redraw();
        Ui.Pump();
        Assert.NotNull(w.CaptureRenderedFrame());
        var c = persp.Camera.Project(Vec3.Zero);
        uint px = persp.Software!.Pixels[(int)c.Y * persp.Software.Width + (int)c.X];
        Assert.True(((px >> 8) & 0xFF) > 80 && ((px >> 16) & 0xFF) < 30 && (px & 0xFF) < 30, $"{px:X8}");
    }

    [AvaloniaFact]
    public void Layout_shows_textures_moving_with_their_object()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 } });
        TextureImages.Put("layout-green.png", new TextureImage(1, 1, [0, 255, 0, 255]));
        w.Session.Commands.SetTexture("Default", "layout-green.png", setup: t => t.Size = new Vec3(2, 2, 2));
        w.ShowLayout();
        var layout = (AVAModeler.Layout.UI.LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var obj = s.Scene.Objects.Single();
        s.SetVector(obj, 0, new Vec3(3, 0, 0));
        var scene = s.BuildScene(s.Scene.Evaluate(s.Frame), true);
        var layer = scene.Layers.Single();
        // Drawn where it is, textured where it was modeled.
        Assert.Equal(4, layer.Mesh.Bounds().Max.X, 9);
        Assert.Equal(1, layer.TextureSpace!.Bounds().Max.X, 9);
        layout.View.Mode = AVAModeler.Layout.UI.LayoutViewMode.Perspective;
        layout.View.Fit();
        Ui.Pump();
        Assert.NotNull(w.CaptureRenderedFrame());
        var c = layout.View.Camera.Project(new Vec3(3, 0, 0));
        uint px = layout.View.Renderer.Pixels[(int)c.Y * layout.View.Renderer.Width + (int)c.X];
        Assert.True(((px >> 8) & 0xFF) > 80 && ((px >> 16) & 0xFF) < 30, $"{px:X8}");
    }

    [AvaloniaFact]
    public void The_surface_editors_t_buttons_open_the_texture_panel()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 4.0, 2, 0.5 }, ["Center"] = new[] { 1.0, 0, 0 } });
        var editor = new SurfaceEditor(w.Session);
        editor.Show(w);
        Ui.Pump();
        Assert.Equal(7, editor.TextureButtons.Count);
        var panel = editor.OpenTexture(w.Session.Document.Surfaces[0], TextureChannel.Color);
        Ui.Pump();
        panel.AddLayer();
        var layer = Assert.Single(panel.Layers);
        // Planar along the box's thinnest side, sized to it.
        Assert.Equal((TextureProjection.Planar, Axis.Z), (layer.Projection, layer.Axis));
        Assert.Equal(new Vec3(1, 0, 0), layer.Center);
        Assert.Equal(new Vec3(4, 2, 0.5), layer.Size);
        layer.Size = Vec3.One;
        panel.AutomaticSizing();
        Assert.Equal(new Vec3(4, 2, 0.5), layer.Size);
        panel.SetImage(Path.Combine(Path.GetTempPath(), "somewhere", "brick.png"));
        Assert.EndsWith("brick.png", layer.Image);
        Ui.Pump();
        Assert.NotNull(panel.CaptureRenderedFrame());
        panel.Close();
        editor.Close();
        Ui.Pump();
        Assert.Equal("Surfaces", w.Session.Document.Undo.UndoLabel);
        w.Session.Document.UndoLast();
        Assert.Empty(w.Session.Document.Surfaces[0].Textures);
    }

    [AvaloniaFact]
    public void Opening_an_object_with_a_missing_image_says_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avamodeler-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var doc = new Document();
            new BoxTool().Run(new ToolContext(doc));
            doc.Surfaces[0].Textures.Add(new TextureLayer { Image = "gone.png" });
            string path = Path.Combine(dir, "box.lwo");
            AVAModeler.Formats.ObjectFiles.Save(doc, path);
            var w = Ui.Open();
            var open = w.Modeler.Open(path);
            for (int i = 0; i < 200 && !open.IsCompleted; i++) Ui.Pump();
            Assert.Contains("gone.png", w.Session.Status);
            Assert.Contains("checker", w.Session.Status);
        }
        finally { Directory.Delete(dir, true); }
    }
}
