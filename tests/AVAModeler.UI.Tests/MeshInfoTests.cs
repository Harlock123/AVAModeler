using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

/// <summary>The Mesh Info panel: opened by its key, showing the check, selecting and fixing problems.</summary>
public class MeshInfoTests
{
    private static Button Button(MainWindow w, string name) =>
        w.Modeler.MeshInfo.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);

    private static void Press(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Ui.Pump();
    }

    [AvaloniaFact]
    public void W_opens_mesh_info_which_follows_the_object_and_selects_its_holes()
    {
        var w = Ui.Open();
        var panel = w.Modeler.MeshInfo;
        Assert.False(panel.IsVisible);
        w.Session.Commands.Run("Box");
        Ui.Fit(w);
        Ui.Key(w, Key.W);
        Ui.Pump();
        Assert.True(panel.IsVisible);
        Assert.Equal("Closed and printable (1 shell)", panel.Verdict.Text);
        Assert.True(w.Modeler.Views.Views[0].Bounds.Width > 200); // the views keep their room

        // Take a face away: the panel checks again by itself.
        w.Session.Commands.SelectPolygons([5]);
        w.Session.Commands.Run("Remove");
        Ui.Pump();
        Assert.Equal("Open: 1 hole along 4 edges", panel.Verdict.Text);
        Assert.Equal(4, panel.Report!.BoundaryEdges);

        // Select shows the rim in Edges mode, as one undo step that Ctrl+Z takes back.
        var doc = w.Session.Document;
        Press(Button(w, "SelectBoundaryEdges"));
        Assert.Equal(SelectionMode.Edges, doc.Selection.Mode);
        Assert.Equal(4, doc.Selection.EdgesIn(0).Count);
        Assert.NotNull(w.CaptureRenderedFrame());
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Empty(doc.Selection.EdgesIn(0));
        Assert.Equal(5, doc.Current.Mesh.Polygons.Count);

        Ui.Key(w, Key.W);
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void Fix_buttons_run_the_tools_that_already_fix_things()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        var mesh = w.Session.Document.Current.Mesh;
        for (int i = 0; i < 6; i++) mesh.Flip(i);
        mesh.AddPolygon([0, 1, 1]);
        w.Modeler.Execute("Mesh Info");
        Ui.Pump();
        var panel = w.Modeler.MeshInfo;
        Assert.Contains("degenerate", panel.Verdict.Text);
        Press(Button(w, "FixDegeneratePolygons"));
        Assert.Equal("Inside out: 1 closed shell facing inward", panel.Verdict.Text);
        Assert.Equal("Align", Button(w, "FixFlippedPolygons").Content);
        Press(Button(w, "FixFlippedPolygons"));
        Assert.Equal("Closed and printable (1 shell)", panel.Verdict.Text);
        Assert.Equal(1, panel.Report!.Volume!.Value, 9);
        // Two fixes, two undo steps.
        Assert.Equal("Align", w.Session.Document.Undo.UndoLabel);
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "No problems found.");
    }

    [AvaloniaFact]
    public void Its_align_button_leaves_a_cavity_facing_inward()
    {
        var w = Ui.Open();
        var cmd = w.Session.Commands;
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 4.0, 4, 4 } });
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 } });
        var mesh = w.Session.Document.Current.Mesh;
        for (int i = 6; i < 12; i++) mesh.Flip(i); // the inner box faces in: a hollow
        mesh.Flip(0);                              // and one outer face is wrong
        w.Modeler.Execute("Mesh Info");
        Ui.Pump();
        var panel = w.Modeler.MeshInfo;
        Assert.Equal(1, panel.Report!.FlippedPolygons);
        Assert.Equal(1, panel.Report.Cavities);
        Press(Button(w, "FixFlippedPolygons"));
        Assert.Equal("Closed and printable (2 shells)", panel.Verdict.Text);
        Assert.Equal(64 - 8, panel.Report.Volume!.Value, 9);
    }

    [AvaloniaFact]
    public void The_view_menu_and_display_tab_have_mesh_info()
    {
        var w = Ui.Open();
        var view = w.Modeler.Menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "_View");
        Assert.Contains(view.Items.OfType<MenuItem>(), i => i.Header as string == "Mesh _Info");
        w.Modeler.Tabs.ShowTab("Display");
        Assert.Contains("Mesh Info", w.Modeler.Tabs.Column.Children.OfType<Button>().Select(b => b.Content as string));
        w.Modeler.Tabs.ShowTab("Polygon");
        var polygon = w.Modeler.Tabs.Column.Children.OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Select Problems", polygon);
        Assert.Contains("Remove Degenerate", polygon);
    }
}
