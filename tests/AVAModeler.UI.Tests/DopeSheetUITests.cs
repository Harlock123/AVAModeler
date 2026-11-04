using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>Layout's dope sheet in a window: D and View › Dope Sheet, clicking, Shift-clicking and boxing keys, dragging them in time, the keys, and undo.</summary>
public class DopeSheetUITests
{
    /// <summary>Layout with a null keyed on position.x at 1, 20 and 40 and the dope sheet showing.</summary>
    private static (LayoutWorkspace Layout, MainWindow Window, NullItem Ball) WithKeys()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var ball = s.Add(new NullItem("Ball"));
        foreach (var (f, x) in new[] { (1.0, 0.0), (20, 2), (40, -1) })
        {
            s.SetFrame(f);
            s.SetVector(ball, 0, new Vec3(x, 0, 0));
        }
        s.SetFrame(1);
        Ui.Key(w, Key.D);
        Ui.Pump();
        return (layout, w, ball);
    }

    private static double[] Frames(Envelope e) => [.. e.Keys.Select(k => k.Frame)];

    private static Point At(MainWindow w, DopeSheetView view, KeyId key) => Ui.In(w, view, view.PointOf(key)!.Value.X, view.PointOf(key)!.Value.Y);

    [AvaloniaFact]
    public void D_shows_the_dope_sheet_in_place_of_the_graph_editor()
    {
        var (layout, w, ball) = WithKeys();
        Assert.True(layout.DopeSheetShown);
        Assert.False(layout.Graph.IsVisible);
        var rows = layout.DopeSheet.Sheet.Rows();
        Assert.Contains(rows, r => r.Item == ball);
        Assert.Equal([1.0, 20, 40], layout.DopeSheet.Sheet.KeysOf(rows.Single(r => r.Item == ball)));
        Ui.Key(w, Key.D);
        Assert.False(layout.DopeSheetShown);
        Assert.True(layout.Graph.IsVisible);
        // The View menu does the same.
        var viewMenu = layout.GetLogicalDescendants().OfType<MenuItem>().Single(m => (string?)m.Header == "_View");
        var menu = ((IEnumerable<MenuItem>)viewMenu.ItemsSource!).Single(m => (string?)m.Header == "_Dope Sheet  (D)");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(layout.DopeSheetShown);
        w.Close();
    }

    [AvaloniaFact]
    public void Keys_are_chosen_and_dragged_in_time_snapped_to_frames_as_one_undo_step()
    {
        var (layout, w, ball) = WithKeys();
        var view = layout.DopeSheet.View;
        var sheet = layout.DopeSheet.Sheet;
        var x = ball[Channel.PositionX];
        // Click the key at 20, Shift-click the one at 40.
        Ui.Click(w, At(w, view, new KeyId(ball, null, 20)));
        Ui.Click(w, At(w, view, new KeyId(ball, null, 40)), RawInputModifiers.Shift);
        Assert.Equal([20.0, 40], sheet.Selected.Select(k => k.Frame).Order());
        // Drag them five frames (and a bit) later.
        double perFrame = view.XOf(2) - view.XOf(1);
        var from = At(w, view, new KeyId(ball, null, 20));
        Ui.Drag(w, from, from + new Point(perFrame * 5.3, 0));
        Assert.Equal([1.0, 25, 45], Frames(x));
        Assert.Equal("Move Keys", layout.Session.History.UndoLabel);
        Assert.Equal([25.0, 45], sheet.Selected.Select(k => k.Frame).Order());
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal([1.0, 20, 40], Frames(x));

        // A box round the first two, then the arrow keys nudge them, and Delete removes them (the last key stays).
        var a = At(w, view, new KeyId(ball, null, 1));
        var b = At(w, view, new KeyId(ball, null, 20));
        Ui.Drag(w, a - new Point(12, 8), b + new Point(12, 8));
        Assert.Equal([1.0, 20], sheet.Selected.Select(k => k.Frame).Order());
        Ui.Key(w, Key.Right);
        Assert.Equal([2.0, 21, 40], Frames(x));
        Ui.Key(w, Key.Delete);
        Assert.Equal([40.0], Frames(x));
        layout.Undo();
        layout.Undo();
        Assert.Equal([1.0, 20, 40], Frames(x));
        w.Close();
    }

    [AvaloniaFact]
    public void Copy_paste_and_scale_from_the_keyboard_and_the_bar()
    {
        var (layout, w, ball) = WithKeys();
        var view = layout.DopeSheet.View;
        var x = ball[Channel.PositionX];
        Ui.Click(w, At(w, view, new KeyId(ball, null, 20)));
        Ui.Key(w, Key.C, RawInputModifiers.Control);
        layout.Session.SetFrame(50);
        Ui.Key(w, Key.V, RawInputModifiers.Control);
        Assert.Equal([1.0, 20, 40, 50], Frames(x));
        Assert.Equal(2, x.Keys[3].Value);
        layout.Undo();
        // Ctrl+A, then Scale × 0.5 about the earliest key.
        Ui.Click(w, At(w, view, new KeyId(ball, null, 1)));
        Ui.Key(w, Key.A, RawInputModifiers.Control);
        layout.DopeSheet.ScaleFactor.Text = "0.5";
        layout.DopeSheet.ApplyScale();
        Assert.Equal([1.0, 11, 21], Frames(x));
        Assert.Equal("Scale Keys", layout.Session.History.UndoLabel);
        // Clicking the frame numbers moves the current frame.
        Ui.Click(w, Ui.In(w, view, view.XOf(30), 6));
        Assert.Equal(30, layout.Session.Frame);
        w.Close();
    }
}
