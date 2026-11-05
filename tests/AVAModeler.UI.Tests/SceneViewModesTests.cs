using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The view's seams for a standalone Layout: mouse modes (move, rotate, size, stretch) with axis limits, which items a
/// click may choose, locked items, the Light view, and the item panel's extra controls. The defaults are what the
/// Layout workspace always did.
/// </summary>
public class SceneViewModesTests
{
    /// <summary>A view of a scene with a box at the origin, in a window of its own, looking from the front.</summary>
    private static (Window Window, SceneView View, LayoutSession Session, ObjectItem Box) Stage()
    {
        var session = new LayoutSession();
        var doc = new Document();
        new BoxTool().Run(new ToolContext(doc));
        var box = session.ShowModelerObject(doc);
        var view = new SceneView(session) { Mode = LayoutViewMode.Front };
        var w = new Window { Width = 640, Height = 480, Content = view };
        w.Show();
        Ui.Pump();
        view.Fit();
        Ui.Pump();
        return (w, view, session, box);
    }

    private static Point At(Window w, SceneView view, Vec3 world)
    {
        var s = view.Camera.Project(world);
        return Ui.In(w, view, s.X, s.Y);
    }

    [AvaloniaFact]
    public void The_default_drag_moves_with_the_left_button_and_turns_with_the_right()
    {
        var (w, view, session, box) = Stage();
        Assert.Equal(ItemDrag.MoveOrRotate, view.DragMode);
        Assert.Equal(DragAxes.All, view.Axes);
        var from = At(w, view, Vec3.Zero);
        Ui.Drag(w, from, from + new Point(60, 0));
        Assert.True(box.PositionAt(session.Frame).X > 0.1);
        Ui.Drag(w, from, from + new Point(40, 0), MouseButton.Right);
        Assert.Equal(20, box.RotationAt(session.Frame).X, 6);
    }

    [AvaloniaFact]
    public void Size_and_stretch_scale_and_the_axes_limit_what_changes()
    {
        var (w, view, session, box) = Stage();
        var from = At(w, view, Vec3.Zero);
        view.DragMode = ItemDrag.Size;
        Ui.Drag(w, from, from + new Point(50, 0));
        var s = box.ScaleAt(session.Frame);
        Assert.Equal(Math.Exp(0.5), s.X, 6);
        Assert.Equal(s.X, s.Z, 9);
        Assert.Equal("_Undo Size Modeler object", MenuHeader(session));
        session.Undo();
        view.DragMode = ItemDrag.Stretch;
        view.Axes = DragAxes.Y;
        Ui.Drag(w, from, from + new Point(50, -50));
        s = box.ScaleAt(session.Frame);
        Assert.Equal(1, s.X, 9);
        Assert.Equal(Math.Exp(0.5), s.Y, 6);
        view.DragMode = ItemDrag.Move;
        view.Axes = DragAxes.X;
        Ui.Drag(w, from, from + new Point(60, -60));
        var p = box.PositionAt(session.Frame);
        Assert.True(p.X > 0.1);
        Assert.Equal(0, p.Y, 9);
    }

    private static string MenuHeader(LayoutSession session) => "_Undo " + session.History.UndoLabel;

    [AvaloniaFact]
    public void Rotate_mode_banks_with_the_right_button()
    {
        var (w, view, session, box) = Stage();
        view.DragMode = ItemDrag.Rotate;
        var from = At(w, view, Vec3.Zero);
        Ui.Drag(w, from, from + new Point(30, 0), MouseButton.Right);
        var r = box.RotationAt(session.Frame);
        Assert.Equal(0, r.X, 9);
        Assert.Equal(15, r.Z, 6);
        Ui.Drag(w, from, from + new Point(20, 10));
        r = box.RotationAt(session.Frame);
        Assert.Equal(10, r.X, 6);
        Assert.Equal(5, r.Y, 6);
    }

    [AvaloniaFact]
    public void Clicks_choose_only_what_the_edit_mode_allows_and_never_a_locked_item()
    {
        var (w, view, session, box) = Stage();
        var light = session.Scene.Lights.First();
        var at = view.Camera.Project(light.WorldPosition(session.Frame));
        Assert.Equal(light, view.Pick(new Point(at.X, at.Y)));
        view.CanPick = i => i is ObjectItem;
        Assert.Null(view.Pick(new Point(at.X, at.Y)));
        var middle = view.Camera.Project(new Vec3(0.1, 0.1, -0.5));
        Assert.Equal(box, view.Pick(new Point(middle.X, middle.Y)));
        box.Locked = true;
        Assert.Null(view.Pick(new Point(middle.X, middle.Y)));
        // A locked item the session has chosen isn't dragged either.
        session.Select(box);
        var from = At(w, view, Vec3.Zero);
        Ui.Drag(w, from, from + new Point(60, 0));
        Assert.Equal(Vec3.Zero, box.PositionAt(session.Frame));
        Assert.False(session.History.CanUndo && session.History.UndoLabel!.StartsWith("Move", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void The_light_view_looks_through_the_chosen_light()
    {
        var (_, view, session, _) = Stage();
        var spot = session.Add(new LightItem("Spot", LightType.Spot) { ConeAngle = 20, EdgeAngle = 5 });
        spot.SetPosition(session.Frame, new Vec3(0, 3, -3));
        spot.SetRotation(session.Frame, new Vec3(0, 45, 0));
        view.Mode = LayoutViewMode.Light;
        Ui.Pump();
        Assert.Equal(spot, view.LightShown);
        var cam = view.Camera;
        Assert.Equal(50, cam.FieldOfView, 6);
        Assert.Equal(45, cam.Pitch, 6);
        // The view's middle is where the light points: down its +Z, toward the origin.
        var centre = cam.Project(new Vec3(0, 0, 0));
        Assert.Equal(cam.Width / 2.0, centre.X, 1);
        Assert.Equal(cam.Height / 2.0, centre.Y, 1);
    }

    [AvaloniaFact]
    public void The_item_panel_shows_a_hosts_extra_controls()
    {
        var session = new LayoutSession();
        var panel = new ItemPropertiesPanel(session) { Extras = (item, p) => p.Children.Add(new Button { Name = "Extra", Content = item.Name }) };
        session.Select(session.Scene.CurrentCamera);
        panel.Show();
        Assert.Equal(session.Scene.CurrentCamera, panel.ShownItem);
        var extra = panel.Children.OfType<Button>().Single(b => b.Name == "Extra");
        Assert.Equal("Camera", extra.Content);
        // After the item's own settings and before Remove.
        Assert.Equal("Remove item", panel.Children.OfType<Button>().Last().Content);
    }
}
