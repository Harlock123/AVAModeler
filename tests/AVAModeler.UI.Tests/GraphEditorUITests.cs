using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>
/// Layout's graph editor in a window: the curve list, choosing keys across curves (click, Shift-click, a box), dragging
/// them, the key panel's numbers and spans on several keys as one undo step, copy and paste onto another channel,
/// Scale ×, and Ctrl+Z.
/// </summary>
public class GraphEditorUITests
{
    /// <summary>Layout with a null keyed on position.x (0, 2, -1) and position.y (1, 3, 0) at 1, 20 and 40, chosen.</summary>
    private static (LayoutWorkspace Layout, MainWindow Window, NullItem Ball) WithKeys()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var ball = s.Add(new NullItem("Ball"));
        foreach (var (f, x, y) in new[] { (1.0, 0.0, 1.0), (20, 2, 3), (40, -1, 0) })
        {
            s.SetFrame(f);
            s.SetVector(ball, 0, new Vec3(x, y, 0));
        }
        s.SetFrame(1);
        s.Select(ball);
        Ui.Pump();
        return (layout, w, ball);
    }

    private static double[] Frames(Envelope e) => [.. e.Keys.Select(k => k.Frame)];

    private static Point At(MainWindow w, GraphView view, KeyId key) => Ui.In(w, view, view.PointOf(key)!.Value.X, view.PointOf(key)!.Value.Y);

    [AvaloniaFact]
    public void The_curve_list_shows_every_channel_and_clicks_choose_curves()
    {
        var (layout, w, ball) = WithKeys();
        var g = layout.Graph;
        Assert.True(g.IsVisible);
        Assert.Equal(["position.x", "position.y", "position.z"], g.Editor.Shown().Select(c => c.Name));
        Assert.NotNull(g.ChannelRow("bank"));
        Ui.Click(w, Ui.In(w, g.ChannelRow("heading")!, 10, 5));
        Assert.Equal(["heading"], g.Editor.Shown().Select(c => c.Name));
        Ui.Click(w, Ui.In(w, g.ChannelRow("position.y")!, 10, 5), RawInputModifiers.Control);
        Assert.Equal(["position.y", "heading"], g.Editor.Shown().Select(c => c.Name));
        g.Editor.ShowKeyed();
        Assert.Equal(["position.x", "position.y", "position.z"], g.Editor.Shown().Select(c => c.Name));
        // A light's intensity is in its list.
        layout.Session.Select(layout.Session.Scene.Lights.First());
        Ui.Pump();
        Assert.NotNull(g.ChannelRow("intensity"));
        Assert.Null(g.ChannelRow("zoom"));
        w.Close();
    }

    [AvaloniaFact]
    public void Keys_are_chosen_across_curves_and_by_a_box_and_dragged_as_one_undo_step()
    {
        var (layout, w, ball) = WithKeys();
        var g = layout.Graph;
        var view = g.View;
        Ui.Click(w, At(w, view, new KeyId(ball, "position.x", 20)));
        Ui.Click(w, At(w, view, new KeyId(ball, "position.y", 40)), RawInputModifiers.Shift);
        Assert.Equal([("position.x", 20.0), ("position.y", 40.0)], g.Editor.ChosenKeys().Select(k => (k.Channel, k.Key.Frame)));

        // A box round the frame-1 keys of x (0) and y (1), and z's (0) too.
        var a = At(w, view, new KeyId(ball, "position.x", 1));
        var b = At(w, view, new KeyId(ball, "position.y", 1));
        Ui.Drag(w, new Point(a.X - 8, Math.Min(a.Y, b.Y) - 8), new Point(a.X + 8, Math.Max(a.Y, b.Y) + 8));
        Assert.Equal([("position.x", 1.0), ("position.y", 1.0), ("position.z", 1.0)], g.Editor.ChosenKeys().Select(k => (k.Channel, k.Key.Frame)));

        // Drag x's key at 20 up: its value goes up, one step.
        Ui.Click(w, At(w, view, new KeyId(ball, "position.x", 20)));
        var x = ball[Channel.PositionX];
        int depth = layout.Session.History.Count;
        var from = At(w, view, new KeyId(ball, "position.x", 20));
        Ui.Drag(w, from, from - new Point(0, 30));
        Assert.True(x.Keys[1].Value > 2.1);
        Assert.Equal(depth + 1, layout.Session.History.Count);
        Assert.Equal("Drag Keys", layout.Session.History.UndoLabel);
        // With Ctrl, sideways in whole frames.
        from = At(w, view, new KeyId(ball, "position.x", 20));
        double perFrame = view.XOf(2) - view.XOf(1);
        Ui.Drag(w, from, from + new Point(perFrame * 5.2, 0), mods: RawInputModifiers.Control);
        Assert.Equal([1.0, 25, 40], Frames(x));
        // The arrows nudge the chosen key a frame.
        Ui.Key(w, Key.Left);
        Assert.Equal([1.0, 24, 40], Frames(x));
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal([1.0, 20, 40], Frames(x));
        Assert.Equal(2, x.Keys[1].Value);
        w.Close();
    }

    [AvaloniaFact]
    public void The_key_panel_sets_tcb_and_spans_on_several_keys_as_one_undo_step()
    {
        var (layout, w, ball) = WithKeys();
        var g = layout.Graph;
        var x = ball[Channel.PositionX];
        var y = ball[Channel.PositionY];
        g.Editor.Choose([g.Editor.Key("position.x", 20), g.Editor.Key("position.y", 20)]);
        Ui.Pump();
        Assert.Equal("20", g.Field(KeyField.Frame).Text);
        Assert.Equal("", g.Field(KeyField.Value).Text);
        int depth = layout.Session.History.Count;
        g.Field(KeyField.Tension).Text = "0.5";
        g.ApplyField(KeyField.Tension);
        Assert.Equal(depth + 1, layout.Session.History.Count);
        Assert.Equal((0.5, 0.5), (x.Keys[1].Tension, y.Keys[1].Tension));

        // Span: Bezier keeps the curve, then the slopes shape it.
        double mid = x.Evaluate(10);
        g.SpanBox.SelectedItem = nameof(SpanKind.Bezier);
        Assert.Equal((SpanKind.Bezier, SpanKind.Bezier), (x.Keys[1].Kind, y.Keys[1].Kind));
        Assert.Equal(mid, x.Evaluate(10), 9);
        g.Field(KeyField.InSlope).Text = "0";
        g.ApplyField(KeyField.InSlope);
        Assert.Equal((0.0, 0.0), (x.Keys[1].InSlope, y.Keys[1].InSlope));
        g.SpanBox.SelectedItem = nameof(SpanKind.Stepped);
        Assert.Equal(0, x.Evaluate(19));
        // Behaviours on the chosen keys' curves.
        g.PostBox.SelectedItem = nameof(EndBehavior.Repeat);
        Assert.Equal((EndBehavior.Repeat, EndBehavior.Repeat), (x.PostBehavior, y.PostBehavior));
        Assert.Equal(EndBehavior.Constant, ball[Channel.PositionZ].PostBehavior);
        Assert.Equal(depth + 5, layout.Session.History.Count);

        // Ctrl+Z, from the graph, undoes them one at a time.
        Ui.Click(w, Ui.In(w, g.View, g.View.Bounds.Width - 4, 4));
        for (int i = 0; i < 5; i++) Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal((0.0, SpanKind.Tcb, EndBehavior.Constant), (x.Keys[1].Tension, x.Keys[1].Kind, x.PostBehavior));
        Assert.Equal((0.0, SpanKind.Tcb), (y.Keys[1].Tension, y.Keys[1].Kind));
        w.Close();
    }

    [AvaloniaFact]
    public void Copy_a_curve_onto_another_channel_and_scale_time()
    {
        var (layout, w, ball) = WithKeys();
        var g = layout.Graph;
        var view = g.View;
        var x = ball[Channel.PositionX];
        // Shift-click the x keys at 20 and 40, copy them, show scale.y alone and paste at frame 5.
        Ui.Click(w, At(w, view, new KeyId(ball, "position.x", 20)));
        Ui.Click(w, At(w, view, new KeyId(ball, "position.x", 40)), RawInputModifiers.Shift);
        Ui.Key(w, Key.C, RawInputModifiers.Control);
        Ui.Click(w, Ui.In(w, g.ChannelRow("scale.y")!, 10, 5));
        layout.Session.SetFrame(5);
        view.Focus();
        Ui.Key(w, Key.V, RawInputModifiers.Control);
        var sy = ball[Channel.ScaleY];
        Assert.Equal([5.0, 25], Frames(sy));
        Assert.Equal([2.0, -1], sy.Keys.Select(k => k.Value));
        Assert.Equal("Paste Keys", layout.Session.History.UndoLabel);

        // Scale × 2 on the pasted keys, about the first.
        g.ScaleFactor.Text = "2";
        g.ApplyScale();
        Assert.Equal([5.0, 45], Frames(sy));
        Assert.Equal("Scale Keys", layout.Session.History.UndoLabel);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal([5.0, 25], Frames(sy));
        // Delete takes the chosen keys (a curve's last stays).
        g.Editor.Choose([g.Editor.Key("scale.y", 5), g.Editor.Key("scale.y", 25)]);
        Ui.Key(w, Key.Delete);
        Assert.Single(sy.Keys);
        Assert.Equal([1.0, 20, 40], Frames(x));
        w.Close();
    }
}
