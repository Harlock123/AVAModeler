using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// History (Ctrl+H, View › History): the undo steps by name, the newest at the top, with the steps that can be redone
/// below the current state, greyed. Clicking a step goes back (or forward) to just after it, as so many Undos or Redos.
/// </summary>
public sealed class HistoryPanel : Border
{
    private readonly ModelerSession _session;
    private readonly StackPanel _rows = new() { Spacing = 1, Margin = new Thickness(6, 0, 6, 8) };
    private bool _queued;
    private static readonly IBrush Faint = new SolidColorBrush(Color.FromRgb(140, 140, 140));

    public const double PanelWidth = 220;

    /// <summary>The buttons shown: the redo steps (furthest first), then the current state, then the undo steps (newest first).</summary>
    public IReadOnlyList<Button> Steps => [.. _rows.Children.OfType<Button>()];

    public HistoryPanel(ModelerSession session)
    {
        _session = session;
        Width = PanelWidth;
        Background = Look.PanelDark;
        BorderBrush = Look.Panel;
        BorderThickness = new Thickness(1, 0, 0, 0);
        IsVisible = false;
        var close = Look.MakeButton("×", () => IsVisible = false, "Close History (Ctrl+H)");
        close.Padding = new Thickness(5, 0);
        var header = new DockPanel { Margin = new Thickness(8, 6, 4, 4) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "History", FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(new ScrollViewer { Content = _rows });
        Child = body;
        session.Changed += Queue;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Refresh(); };
    }

    public void Toggle() => IsVisible = !IsVisible;

    private void Queue()
    {
        if (!IsVisible || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; if (IsVisible) Refresh(); }, DispatcherPriority.Background);
    }

    /// <summary>Lists the steps afresh.</summary>
    public void Refresh()
    {
        var undo = _session.Document.Undo;
        _rows.Children.Clear();
        var redo = undo.RedoLabels;
        for (int i = redo.Count - 1; i >= 0; i--)
        {
            int forward = i + 1;
            _rows.Children.Add(Step(redo[i], Faint, () => Go(forward), $"Redo to here ({forward} step{(forward == 1 ? "" : "s")} forward)"));
        }
        _rows.Children.Add(new TextBlock { Text = "▶ now", Foreground = Look.ButtonOn, FontSize = 11, Margin = new Thickness(4, 2) });
        var done = undo.UndoLabels;
        for (int i = done.Count - 1; i >= 0; i--)
        {
            int back = done.Count - 1 - i;
            _rows.Children.Add(Step(done[i], Look.Text, () => Go(-back), back == 0 ? "The latest step" : $"Undo back to just after this ({back} step{(back == 1 ? "" : "s")})"));
        }
        if (done.Count == 0 && redo.Count == 0) _rows.Children.Add(new TextBlock { Text = "Nothing done yet.", Foreground = Faint, FontSize = 11 });
    }

    private void Go(int steps)
    {
        if (steps == 0) return;
        _session.CancelTool();
        int went = _session.Document.Step(steps);
        _session.SetStatus(steps < 0 ? $"Undid {went} step{(went == 1 ? "" : "s")}." : $"Redid {went} step{(went == 1 ? "" : "s")}.");
        Refresh();
    }

    private static Button Step(string label, IBrush colour, Action click, string tip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = label, Foreground = colour, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
            Background = Brushes.Transparent, Padding = new Thickness(4, 1), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
