using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>The surface node editor, and combing hair with the mouse in Layout.</summary>
public class NodeEditorUITests
{
    [AvaloniaFact]
    public void The_node_editor_wires_nodes_into_a_surface_and_the_visit_is_one_undo_step()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Ball", new Dictionary<string, object?> { ["Surface"] = "Rock" });
        var editor = new SurfaceEditor(w.Session);
        editor.Show(w);
        Ui.Pump();
        var rock = w.Session.Document.Surfaces.First(s => s.Name == "Rock");
        editor.Choose(w.Session.Document.Surfaces.IndexOf(rock));
        Ui.Pump();
        var nodes = editor.OpenNodes(rock, editor.NodesButton);
        Ui.Pump();
        var before = PreviewBall.Draw(rock, 32);
        var g = nodes.Graph;
        var output = g.Output!;
        var noise = nodes.AddNode(NodeKind.Noise);
        var ramp = nodes.AddNode(NodeKind.Gradient);
        (noise.X, noise.Y, ramp.X, ramp.Y, output.X, output.Y) = (30, 60, 260, 80, 500, 60);
        noise.Numbers["Scale"] = 0.1;
        ramp.Stops = [new(0.3, new Rgb(70, 45, 30)), new(0.7, new Rgb(220, 200, 160))];
        // Dragging an output onto an input links them.
        var c = nodes.Canvas;
        void Wire(SurfaceNode from, string output, SurfaceNode to, string input)
        {
            c.Press(c.PortPoint(from, output, true));
            c.Move(new Point(300, 300));
            c.Release(c.PortPoint(to, input, false));
        }
        Wire(noise, "Value", ramp, "Value");
        Wire(ramp, "Color", output, "Color");
        Wire(noise, "Value", output, "Bump");
        Assert.Equal(new NodeLink(noise.Id, "Value"), ramp.Links["Value"]);
        Assert.Equal(new NodeLink(ramp.Id, "Color"), output.Links["Color"]);
        Assert.NotEqual(before, PreviewBall.Draw(rock, 32));
        Assert.NotNull(nodes.PreviewImage);
        // A loop is refused, with the reason shown.
        Wire(ramp, "Alpha", ramp, "Value");
        Assert.Equal(new NodeLink(noise.Id, "Value"), ramp.Links["Value"]);
        Assert.Contains(nodes.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("loop") == true);
        // Picking a wire up off an input and dropping it on nothing unlinks it.
        c.Press(c.PortPoint(output, "Bump", false));
        c.Release(new Point(5, 400));
        Assert.False(output.Links.ContainsKey("Bump"));
        // Dragging a node by its title moves it; choosing it shows its settings.
        c.Press(new Point(noise.X + 20, noise.Y + 8));
        c.Move(new Point(noise.X + 60, noise.Y + 48));
        c.Release(new Point(noise.X + 60, noise.Y + 48));
        Assert.Equal((70.0, 100.0), (noise.X, noise.Y));
        Assert.Equal(noise, c.Chosen);
        Ui.Pump();
        Assert.Contains(nodes.GetLogicalDescendants().OfType<TextBox>(), t => t.Name == "NodeScale");
        (noise.X, noise.Y) = (30, 60);
        if (Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } dir)
        {
            c.Choose(ramp.Id);
            Ui.Pump();
            var frame = nodes.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, "AVAModeler-node-editor.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        // The output node stays; another goes with Delete Node.
        c.Choose(output.Id);
        nodes.DeleteChosen();
        Assert.NotNull(g.Output);
        c.Choose(noise.Id);
        nodes.DeleteChosen();
        Assert.Null(g.Find(noise.Id));
        Assert.False(ramp.Links.ContainsKey("Value"));
        nodes.Close();
        editor.Close();
        Ui.Pump();
        Assert.Equal("Surfaces", w.Session.Document.Undo.UndoLabel);
        Assert.NotNull(rock.Nodes);
        w.Session.Document.UndoLast();
        Assert.Null(w.Session.Document.Surfaces.First(s => s.Name == "Rock").Nodes);
    }

    [AvaloniaFact]
    public void Dragging_across_a_view_with_the_brush_on_combs_the_hair_as_one_undo_step()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var doc = new Document();
        new Commands(doc).Run("Ball", new Dictionary<string, object?> { ["Radius"] = new[] { 0.5, 0.5, 0.5 }, ["Sides"] = 16, ["Segments"] = 8 });
        var ball = s.Add(new ObjectItem("Ball", doc) { Hair = new HairSettings { Density = 200 }.Checked() });
        s.Select(ball);
        layout.View.Mode = LayoutViewMode.Front;
        layout.View.Fit();
        Ui.Pump();
        var brush = layout.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "CombBrush");
        brush.IsChecked = true;
        Ui.Pump();
        Assert.True(s.HairBrush.On);
        s.HairBrush.Radius = 80;
        var mid = Ui.Mid(layout.View);
        Ui.Drag(w, Ui.In(w, layout.View, mid.X - 40, mid.Y), Ui.In(w, layout.View, mid.X + 40, mid.Y));
        // Combed toward +X (the front view's right), the ball not moved, one step.
        var combing = ball.Hair!.Combing;
        Assert.NotNull(combing);
        Assert.True(combing.Combed > 0);
        var mean = combing.Directions.Aggregate(Vec3.Zero, (a, d) => a + d);
        Assert.True(mean.X > 0 && Math.Abs(mean.X) > Math.Abs(mean.Y), $"{mean}");
        Assert.Equal(Vec3.Zero, ball.PositionAt(s.Frame));
        Assert.Equal("Comb Ball's Hair", s.History.UndoLabel);
        // Shift uncombs.
        int combed = combing.Combed;
        Ui.Drag(w, Ui.In(w, layout.View, mid.X - 40, mid.Y), Ui.In(w, layout.View, mid.X + 40, mid.Y), mods: Avalonia.Input.RawInputModifiers.Shift);
        Assert.True((ball.Hair.Combing?.Directions.Sum(d => d.Length) ?? 0) < combing.Directions.Sum(d => d.Length));
        s.Undo();
        Assert.Equal(combed, ball.Hair.Combing!.Combed);
        // Off again, a drag moves the ball as ever.
        brush.IsChecked = false;
        Ui.Pump();
        Assert.False(s.HairBrush.On);
        Assert.NotNull(w.CaptureRenderedFrame());
    }
}
