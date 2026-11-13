using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// Mesh Info (W, View › Mesh Info, or the Display tab): the mesh check of the foreground layers, kept up to date as the
/// object changes. It shows the verdict, the counts and measurements, and each kind of problem found with a button that
/// selects it (Select Problems) and, where a tool already fixes it safely, one that runs that tool. The check itself is
/// <see cref="MeshCheck"/>; this only shows it.
/// </summary>
public sealed class MeshInfoPanel : Border
{
    private readonly ModelerSession _session;
    private readonly StackPanel _rows = new() { Spacing = 3, Margin = new Thickness(8, 0, 8, 8) };
    private int _shownVersion = -1;
    private object? _shownDocument;
    private bool _queued;

    public const double PanelWidth = 260;
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(120, 220, 120));

    /// <summary>The report shown last.</summary>
    public MeshReport? Report { get; private set; }

    /// <summary>The verdict line as shown.</summary>
    public TextBlock Verdict { get; } = new() { FontWeight = FontWeight.Bold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    public MeshInfoPanel(ModelerSession session)
    {
        _session = session;
        Width = PanelWidth;
        Background = Look.PanelDark;
        BorderBrush = Look.Panel;
        BorderThickness = new Thickness(1, 0, 0, 0);
        IsVisible = false;
        var close = Look.MakeButton("×", () => IsVisible = false, "Close Mesh Info (W)");
        close.Padding = new Thickness(5, 0);
        var header = new DockPanel { Margin = new Thickness(8, 6, 4, 4) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Mesh Info", FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(new ScrollViewer { Content = _rows });
        Child = body;
        session.Changed += Queue;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Refresh(); };
    }

    /// <summary>Shows or hides the panel (checking afresh when it opens).</summary>
    public void Toggle() => IsVisible = !IsVisible;

    /// <summary>Checks again once the current burst of changes is over, if the object changed and the panel is open.</summary>
    private void Queue()
    {
        if (!IsVisible || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (IsVisible && (_shownVersion != _session.Document.Version || _shownDocument != _session.Document)) Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>Checks the foreground now and shows what it found.</summary>
    public void Refresh()
    {
        var doc = _session.Document;
        _shownVersion = doc.Version;
        _shownDocument = doc;
        var r = Report = MeshCheck.Document(doc);
        _rows.Children.Clear();
        Verdict.Text = r.Verdict;
        Verdict.Foreground = r.Printable ? Good : Look.ButtonOn;
        _rows.Children.Add(Verdict);
        // The measurements: every line of the report after the verdict that isn't a note.
        var notes = r.Notes().Count;
        var lines = r.Lines();
        foreach (var line in lines.Skip(1).Take(lines.Count - 1 - notes)) _rows.Children.Add(Text(line));
        _rows.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        var problems = Enum.GetValues<MeshProblem>().Where(p => r.Count(p) > 0).ToList();
        if (problems.Count == 0) _rows.Children.Add(Text(r.Faces > 0 ? "No problems found." : "Nothing to check."));
        foreach (var p in problems) _rows.Children.Add(Row(r, p));
        if (r.Cavities > 0) _rows.Children.Add(Text($"{r.Cavities} {(r.Cavities == 1 ? "cavity" : "cavities")}: inward shells inside others, left as they are."));
        _rows.Children.Add(Text("Checks the whole foreground layers. Select shows the problem (one undo step); Fix runs the tool named on everything in the foreground."));
        // Measurements of the selection (two points: their distance; three: the angle at the middle one).
        var measure = SelectionMeasure.Of(doc).Lines();
        if (measure.Count > 0)
        {
            _rows.Children.Add(new Separator { Margin = new Thickness(0, 6) });
            _rows.Children.Add(new TextBlock { Text = "Measure", FontWeight = FontWeight.Bold, Foreground = Look.Text, FontSize = 12 });
            foreach (var line in measure) _rows.Children.Add(Text(line));
        }
    }

    private Control Row(MeshReport r, MeshProblem p)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var select = Look.MakeButton("Select", () => Select(p), $"Select the {p} (Select Problems)");
        select.Name = $"Select{p}";
        buttons.Children.Add(select);
        if (SelectProblemsTool.FixFor(p) is { } fix)
        {
            var b = Look.MakeButton(fix, () => Fix(p, fix), $"Run {fix} on the foreground");
            b.Name = $"Fix{p}";
            buttons.Children.Add(b);
        }
        return new StackPanel { Spacing = 1, Margin = new Thickness(0, 2), Children = { Text(r.Describe(p), Look.ButtonOn), buttons } };
    }

    private void Select(MeshProblem p)
    {
        _session.CommitTool();
        var tool = new SelectProblemsTool { Problem = p };
        if (_session.Run(tool)) _session.SetStatus($"Select Problems: {tool.Selected} selected. Ctrl+Z puts the selection back.");
    }

    /// <summary>
    /// Runs a fixing tool on the whole foreground: the selection is cleared first, so it acts on everything. Align is
    /// given every shell but the cavities, which face inward on purpose and would otherwise be turned out.
    /// </summary>
    private void Fix(MeshProblem problem, string tool)
    {
        _session.CommitTool();
        var sel = _session.Document.Selection;
        sel.Clear(allModes: true);
        if (problem == MeshProblem.FlippedPolygons && Report is { Cavities: > 0 } r)
        {
            sel.Mode = AVAModeler.Core.SelectionMode.Polygons;
            foreach (var layer in r.Layers)
                sel.PolygonsIn(layer.Layer).UnionWith(layer.Shells.Where(s => !s.Cavity).SelectMany(s => s.Faces));
        }
        _session.Run(_session.Registry.Create(tool));
    }

    private static TextBlock Text(string text, IBrush? colour = null) =>
        new() { Text = text, Foreground = colour ?? Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
}
