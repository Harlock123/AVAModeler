using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

/// <summary>The edge and face tools in the window: their buttons, keys and menu items, and a failing drag that says why.</summary>
public class EdgeToolsUITests
{
    private static List<string?> Buttons(MainWindow w, string tab)
    {
        w.Modeler.Tabs.ShowTab(tab);
        return w.Modeler.Tabs.Column.Children.OfType<Button>().Select(b => b.Content as string).ToList();
    }

    [AvaloniaFact]
    public void The_tabs_and_the_edit_menu_have_the_new_tools()
    {
        var w = Ui.Open();
        var multiply = Buttons(w, "Multiply");
        Assert.Equal(["Bevel", "Smooth Shift", "Inset", "Chamfer", "Bridge"], multiply.SkipWhile(b => b != "Bevel").Take(5));
        var polygon = Buttons(w, "Polygon");
        Assert.Contains("Loop Cut", polygon);
        Assert.Contains("Fill Hole", polygon);
        Assert.Contains("Select Loop", polygon);
        Assert.Contains("Select Ring", polygon);
        var edit = w.Modeler.Menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "_Edit");
        Assert.Contains(edit.Items.OfType<MenuItem>(), i => i.Header as string == "Select _Loop");
        Assert.Contains(edit.Items.OfType<MenuItem>(), i => i.Header as string == "Select Ri_ng");
    }

    [AvaloniaFact]
    public void Keys_select_loops_and_rings_and_arm_inset_and_chamfer()
    {
        var w = Ui.Open();
        var cmd = w.Session.Commands;
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 }, ["SegmentsX"] = 3 });
        Ui.Fit(w);
        // Alt+L with nothing selected says what it needs.
        Ui.Key(w, Key.L, RawInputModifiers.Alt);
        Assert.Contains("Select an edge first", w.Session.Status);
        var m = w.Session.Document.Current.Mesh;
        // An edge along X on the top front: its loop goes round the box's middle band of faces (three quads a side).
        var edge = m.EdgeMap().Keys.First(e => Math.Abs(m.Points[e.A].Y - 1) < 1e-9 && Math.Abs(m.Points[e.B].Y - 1) < 1e-9
            && Math.Abs(m.Points[e.A].Z + 1) < 1e-9 && Math.Abs(m.Points[e.B].Z + 1) < 1e-9 && Math.Abs(m.Points[e.A].X + m.Points[e.B].X) < 1e-9);
        cmd.SelectEdges([(edge.A, edge.B)]);
        Ui.Key(w, Key.R, RawInputModifiers.Alt);
        Assert.Equal(SelectionMode.Edges, w.Session.Document.Selection.Mode);
        Assert.Equal(4, w.Session.Document.Selection.EdgesIn(0).Count);
        Assert.Contains("4 edges selected", w.Session.Status);
        cmd.SelectNone();
        cmd.SelectEdges([(edge.A, edge.B)]);
        Ui.Key(w, Key.L, RawInputModifiers.Alt);
        Assert.Equal(3, w.Session.Document.Selection.EdgesIn(0).Count); // along the top front edge, ended by the corners
        Ui.Key(w, Key.I);
        Assert.IsType<InsetTool>(w.Session.ActiveTool);
        Ui.Key(w, Key.Escape);
        Ui.Key(w, Key.B, RawInputModifiers.Control);
        Assert.IsType<ChamferTool>(w.Session.ActiveTool);
        Ui.Key(w, Key.Escape);
    }

    [AvaloniaFact]
    public void A_loop_cut_dragged_with_no_edge_says_why_and_changes_nothing()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        Ui.Fit(w);
        int steps = w.Session.Document.Undo.Count;
        Ui.Key(w, Key.R, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.IsType<LoopCutTool>(w.Session.ActiveTool);
        var front = Ui.View(w, ViewKind.Front);
        var c = Ui.Mid(front);
        Ui.Drag(w, Ui.In(w, front, c.X, c.Y), Ui.In(w, front, c.X + 40, c.Y));
        Assert.Null(w.Session.ActiveTool);
        Assert.Contains("Loop Cut needs an edge", w.Session.Status);
        Assert.Equal(6, w.Session.Document.Current.Mesh.Polygons.Count);
        Assert.Equal(steps, w.Session.Document.Undo.Count);

        // With an edge chosen, the drag slides the cut and Enter keeps it.
        w.Session.Commands.SelectEdges([(0, 1)]);
        Ui.Key(w, Key.R, RawInputModifiers.Control | RawInputModifiers.Shift);
        Ui.Drag(w, Ui.In(w, front, c.X, c.Y), Ui.In(w, front, c.X + 40, c.Y));
        Ui.Key(w, Key.Enter);
        Assert.Equal(10, w.Session.Document.Current.Mesh.Polygons.Count);
        Assert.True(MeshCheck.Document(w.Session.Document).Printable);
        Assert.Equal(steps + 1, w.Session.Document.Undo.Count);
    }

    [AvaloniaFact]
    public void Mesh_infos_fill_hole_button_closes_a_hole()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        w.Session.Commands.SelectPolygons([2]);
        w.Session.Commands.Run("Remove");
        w.Modeler.Execute("Mesh Info");
        Ui.Pump();
        var panel = w.Modeler.MeshInfo;
        Assert.StartsWith("Open: 1 hole", panel.Verdict.Text);
        var fill = panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "FixBoundaryEdges");
        Assert.Equal("Fill Hole", fill.Content);
        fill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        Assert.Equal("Closed and printable (1 shell)", panel.Verdict.Text);
        Assert.Equal("Fill Hole", w.Session.Document.Undo.UndoLabel);
    }
}
