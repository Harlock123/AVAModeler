using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// The graph editor: the chosen item's channels as curves over the frames, each key a dot. The list on the left has
/// every curve the item has (motion, a light's intensity, a camera's zoom, morph weights, displacement amplitudes,
/// path position): click a name to show it alone, Ctrl- or Shift-click to show it with the others, Keyed shows every
/// keyed one and All every one. On the graph, click a key to choose it, Shift-click to add or take it away, drag a box
/// to choose the keys in it on every shown curve; drag chosen keys up and down to change their values, or with Ctrl
/// held (or Time on) sideways in whole frames, one undo step a drag; the arrow keys nudge them. The panel on the right
/// sets a number, the span (TCB, Linear, Stepped, Bezier) or the Bezier slopes on every chosen key, and what the
/// curves do before and after their keys. Copy, Paste (at the current frame; keys copied from one curve go onto the
/// one curve shown), Delete and Scale × work on the chosen keys. Clicking empty graph moves to that frame. The work is
/// <see cref="CurveEditor"/>'s; each change is an undo step.
/// </summary>
public sealed class GraphEditor : DockPanel
{
    private readonly LayoutSession _session;
    private readonly StackPanel _channelRows = new() { Name = "GraphChannels", Spacing = 0 };
    private readonly Dictionary<string, Border> _rows = [];
    private readonly Dictionary<KeyField, TextBox> _fields = [];
    private readonly TextBlock _keyTitle = new() { Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _info = new() { Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private string _rowsFor = "";
    private bool _refreshing;

    /// <summary>The curves, keys and what is done to them.</summary>
    public CurveEditor Editor { get; }

    /// <summary>The plot.</summary>
    public GraphView View { get; }

    /// <summary>Chooses how the span into the chosen keys is drawn.</summary>
    public ComboBox SpanBox { get; } = new() { Name = "KeySpan", ItemsSource = Enum.GetNames<SpanKind>(), FontSize = 11, MinHeight = 20, Height = 24, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>What the chosen curves do before their first key.</summary>
    public ComboBox PreBox { get; } = new() { Name = "PreBehavior", ItemsSource = Enum.GetNames<EndBehavior>(), FontSize = 11, MinHeight = 20, Height = 24, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>What the chosen curves do after their last key.</summary>
    public ComboBox PostBox { get; } = new() { Name = "PostBehavior", ItemsSource = Enum.GetNames<EndBehavior>(), FontSize = 11, MinHeight = 20, Height = 24, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>How much Scale × stretches the chosen keys' timing.</summary>
    public TextBox ScaleFactor { get; } = new() { Name = "GraphScaleFactor", Text = "2", Width = 46, FontSize = 11, MinHeight = 20, Height = 22, Padding = new Thickness(4, 1) };

    /// <summary>Drag keys sideways in time instead of up and down (as Ctrl does while dragging).</summary>
    public CheckBox TimeMode { get; } = new() { Name = "GraphTimeMode", Content = "Time", Foreground = Look.Text, FontSize = 11, Margin = new Thickness(6, 0) };

    internal static readonly Color[] Colours =
    [
        Color.FromRgb(255, 90, 90), Color.FromRgb(90, 230, 90), Color.FromRgb(100, 150, 255),
        Color.FromRgb(255, 170, 60), Color.FromRgb(220, 110, 255), Color.FromRgb(60, 220, 220),
        Color.FromRgb(255, 140, 140), Color.FromRgb(150, 255, 150), Color.FromRgb(150, 180, 255),
        Color.FromRgb(255, 230, 90), Color.FromRgb(255, 120, 200), Color.FromRgb(180, 220, 120),
    ];

    public GraphEditor(LayoutSession session)
    {
        _session = session;
        Editor = new CurveEditor(session);
        View = new GraphView(this) { Name = "GraphView" };
        Background = Look.PanelDark;

        // The curves.
        var channels = new DockPanel { Width = 124 };
        var showBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(2) };
        var keyed = Look.MakeButton("Keyed", () => Editor.ShowKeyed(), "Show every keyed curve");
        keyed.Name = "ShowKeyed";
        var all = Look.MakeButton("All", Editor.ShowAll, "Show every curve the item has");
        all.Name = "ShowAll";
        showBar.Children.Add(keyed);
        showBar.Children.Add(all);
        SetDock(showBar, Dock.Top);
        channels.Children.Add(showBar);
        channels.Children.Add(new ScrollViewer { Content = _channelRows, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        SetDock(channels, Dock.Left);
        Children.Add(channels);

        // The chosen keys' panel.
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2) };
        panel.Children.Add(new TextBlock { Text = "Key", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        panel.Children.Add(_keyTitle);
        foreach (var f in Enum.GetValues<KeyField>())
        {
            if (f == KeyField.InSlope) panel.Children.Add(Row("Span", SpanBox));
            var box = new TextBox { Name = "Key" + f, FontSize = 11, PlaceholderText = "—", MinHeight = 20, Height = 22, Padding = new Thickness(4, 1) };
            _fields[f] = box;
            var field = f;
            box.LostFocus += (_, _) => ApplyField(field);
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyField(field); e.Handled = true; } };
            panel.Children.Add(Row(f switch { KeyField.InSlope => "In slope", KeyField.OutSlope => "Out slope", _ => f.ToString() }, box));
        }
        panel.Children.Add(new TextBlock { Text = "Before / after the keys", Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(PreBox);
        panel.Children.Add(PostBox);
        var keyPanel = new ScrollViewer { Width = 196, Content = panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        SetDock(keyPanel, Dock.Right);
        Children.Add(keyPanel);

        // The bar over the graph.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        bar.Children.Add(Named(Look.MakeButton("Copy", () => Editor.Copy(), "Copy the chosen keys (Ctrl+C)"), "GraphCopy"));
        bar.Children.Add(Named(Look.MakeButton("Paste", () => Editor.Paste(session.Frame), "Paste the copied keys at the current frame (Ctrl+V); keys from one curve go onto the one curve shown"), "GraphPaste"));
        bar.Children.Add(Named(Look.MakeButton("Delete", () => Editor.Delete(), "Delete the chosen keys (Delete)"), "GraphDelete"));
        bar.Children.Add(Named(Look.MakeButton("Scale ×", ApplyScale, "Stretch the chosen keys' timing by this factor, about the earliest (2 is twice as long)"), "GraphScale"));
        bar.Children.Add(ScaleFactor);
        bar.Children.Add(TimeMode);
        bar.Children.Add(_info);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(View);

        SpanBox.SelectionChanged += (_, _) =>
        {
            if (_refreshing || !Enum.TryParse<SpanKind>(SpanBox.SelectedItem as string, out var k)) return;
            Editor.SetSpan(k);
        };
        PreBox.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && Enum.TryParse<EndBehavior>(PreBox.SelectedItem as string, out var b)) Editor.SetBehavior(b, null);
        };
        PostBox.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && Enum.TryParse<EndBehavior>(PostBox.SelectedItem as string, out var b)) Editor.SetBehavior(null, b);
        };
        Editor.SelectionChanged += Refresh;
        session.Changed += () =>
        {
            Editor.Prune();
            Refresh();
        };
        Refresh();
    }

    private static Control Named(Control c, string name)
    {
        c.Name = name;
        return c;
    }

    private static Control Row(string label, Control c)
    {
        var l = new TextBlock { Text = label, Width = 62, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        SetDock(l, Dock.Left);
        return new DockPanel { Children = { l, c } };
    }

    /// <summary>A key field's box in the key panel.</summary>
    public TextBox Field(KeyField field) => _fields[field];

    /// <summary>A curve's name in the list (for tests), or null if the item hasn't that channel.</summary>
    public Control? ChannelRow(string channel) => _rows.GetValueOrDefault(channel);

    /// <summary>The colour a curve is drawn in: by its place in the item's channels.</summary>
    internal Color ColourOf(string channel)
    {
        var all = Editor.Channels();
        int i = 0;
        while (i < all.Count && all[i].Name != channel) i++;
        return Colours[i % Colours.Length];
    }

    /// <summary>Sets the number typed in a field's box on every chosen key (Enter, or leaving the box).</summary>
    public void ApplyField(KeyField field)
    {
        var box = _fields[field];
        var common = Editor.Common(field);
        if (string.IsNullOrWhiteSpace(box.Text) || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
        if (common is { } c && Format(c) == box.Text.Trim()) return;
        try { Editor.Set(field, v); }
        catch (ArgumentException e) { _session.SetStatus(e.Message); }
        Refresh();
    }

    /// <summary>Scales the chosen keys by the factor typed (Scale ×).</summary>
    public void ApplyScale()
    {
        if (!double.TryParse(ScaleFactor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) return;
        try { Editor.Scale(factor); }
        catch (ArgumentException e) { _session.SetStatus(e.Message); }
    }

    private static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            RefreshChannels();
            var keys = Editor.ChosenKeys();
            _keyTitle.Text = keys.Count switch
            {
                0 => "Click a key on a curve; Shift-click or drag a box for more.",
                1 => keys[0].Channel,
                _ => $"{keys.Count} keys on {string.Join(", ", keys.Select(k => k.Channel).Distinct())}",
            };
            foreach (var (f, box) in _fields)
            {
                box.IsEnabled = keys.Count > 0;
                if (box.IsFocused) continue;
                box.Text = Editor.Common(f) is { } v ? Format(v) : "";
            }
            SpanBox.IsEnabled = keys.Count > 0;
            SpanBox.SelectedItem = Editor.CommonSpan?.ToString();
            var curves = Editor.TargetCurves();
            PreBox.IsEnabled = PostBox.IsEnabled = curves.Count > 0;
            PreBox.SelectedItem = curves.Count > 0 && curves.All(c => c.Envelope.PreBehavior == curves[0].Envelope.PreBehavior) ? curves[0].Envelope.PreBehavior.ToString() : null;
            PostBox.SelectedItem = curves.Count > 0 && curves.All(c => c.Envelope.PostBehavior == curves[0].Envelope.PostBehavior) ? curves[0].Envelope.PostBehavior.ToString() : null;
            _info.Text = keys.Count == 0
                ? "Drag a box to choose keys; drag keys to change values (Ctrl: time)."
                : $"{keys.Count} chosen, from frame {keys.Min(k => k.Key.Frame):0.##}";
        }
        finally { _refreshing = false; }
        View.InvalidateVisual();
    }

    private void RefreshChannels()
    {
        var all = Editor.Channels();
        string sig = (Editor.Item?.GetHashCode() ?? 0) + ":" + string.Join("|", all.Select(c => c.Name));
        if (sig != _rowsFor)
        {
            _rowsFor = sig;
            _channelRows.Children.Clear();
            _rows.Clear();
            foreach (var (name, _) in all)
            {
                var n = name;
                var row = new Border
                {
                    Name = "Channel " + name,
                    Padding = new Thickness(4, 1),
                    Child = new TextBlock { Text = name, FontSize = 11, Foreground = new SolidColorBrush(ColourOf(name)) },
                };
                row.PointerPressed += (_, e) =>
                {
                    bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    Editor.ClickChannel(n, add);
                    e.Handled = true;
                };
                _rows[name] = row;
                _channelRows.Children.Add(row);
            }
            if (all.Count == 0) _channelRows.Children.Add(new TextBlock { Text = "Choose an item.", Foreground = Look.Text, FontSize = 11, Margin = new Thickness(4) });
        }
        var shown = Editor.Shown().Select(c => c.Name).ToHashSet();
        var keyed = Editor.Item == null ? [] : SceneChannels.Keyed(Editor.Item, _session.Scene).Select(c => c.Name).ToHashSet();
        foreach (var (name, row) in _rows)
        {
            row.Background = shown.Contains(name) ? new SolidColorBrush(Color.FromRgb(96, 96, 104)) : Brushes.Transparent;
            ((TextBlock)row.Child!).FontWeight = keyed.Contains(name) ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source is TextBox) return;
        if (HandleKey(e)) e.Handled = true;
    }

    /// <summary>
    /// The graph editor's keys while it has the focus: Delete, Ctrl+C, Ctrl+V (at the current frame), Ctrl+A (every key
    /// on the shown curves); the left and right arrows nudge the chosen keys a frame, up and down a step of value
    /// (<see cref="GraphView.ValueStep"/>; ten with Shift). Returns whether it used the key.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool any = Editor.Selected.Count > 0;
        double step = View.ValueStep * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
        switch (e.Key)
        {
            case Key.Delete or Key.Back when !ctrl && any: Editor.Delete(); return true;
            case Key.C when ctrl: Editor.Copy(); return true;
            case Key.V when ctrl: Editor.Paste(_session.Frame); return true;
            case Key.A when ctrl: Editor.SelectAll(); return true;
            case Key.Left when !ctrl && any: Editor.Nudge(-1, 0); return true;
            case Key.Right when !ctrl && any: Editor.Nudge(1, 0); return true;
            case Key.Up when !ctrl && any: Editor.Nudge(0, step); return true;
            case Key.Down when !ctrl && any: Editor.Nudge(0, -step); return true;
        }
        return false;
    }
}

/// <summary>The graph editor's plot: the shown curves over the scene's frames, their keys, and the mouse on them.</summary>
public sealed class GraphView : Control
{
    private readonly GraphEditor _owner;
    private (double Lo, double Hi) _range = (-1, 1);
    private Point _press, _now;
    private enum Gesture { None, Drag, Box }
    private Gesture _gesture;
    private bool _moved, _time;
    private KeyId? _pressedKey;

    internal GraphView(GraphEditor owner)
    {
        _owner = owner;
        Focusable = true;
        ClipToBounds = true;
    }

    private CurveEditor Editor => _owner.Editor;
    private Scene.Scene Scene => Editor.Session.Scene;
    private double First => Scene.FirstFrame;
    private double Last => Math.Max(Scene.LastFrame, Scene.FirstFrame + 1);

    /// <summary>Where a frame is across the plot.</summary>
    public double XOf(double frame) => 30 + (frame - First) / (Last - First) * Math.Max(1, Bounds.Width - 40);

    /// <summary>The frame at a point across the plot (not snapped).</summary>
    public double FrameAt(double x) => First + (x - 30) / Math.Max(1, Bounds.Width - 40) * (Last - First);

    /// <summary>Where a value is down the plot.</summary>
    public double YOf(double v) => 10 + (1 - (v - _range.Lo) / Math.Max(1e-9, _range.Hi - _range.Lo)) * Math.Max(1, Bounds.Height - 20);

    /// <summary>The value at a point down the plot.</summary>
    public double ValueAt(double y) => _range.Lo + (1 - (y - 10) / Math.Max(1, Bounds.Height - 20)) * (_range.Hi - _range.Lo);

    /// <summary>The values shown, bottom to top.</summary>
    public (double Lo, double Hi) Range => _range;

    /// <summary>How far the up and down arrows nudge a value: a tenth of the shown range's order of magnitude.</summary>
    public double ValueStep => Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-6, _range.Hi - _range.Lo))) - 1);

    /// <summary>Where a key's dot is drawn (for tests), or null if it isn't on a shown curve.</summary>
    public Point? PointOf(KeyId key)
    {
        Measure();
        if (key.Channel == null || key.Item != Editor.Item) return null;
        var env = Editor.Shown().FirstOrDefault(c => c.Name == key.Channel).Envelope;
        if (env == null || env.IndexOf(key.Frame) is var i && i < 0) return null;
        return new Point(XOf(key.Frame), YOf(env.Keys[i].Value));
    }

    /// <summary>The key whose dot is under a point, or null.</summary>
    public KeyId? KeyAt(Point p)
    {
        KeyId? best = null;
        double bestD = 7;
        foreach (var (name, env) in Editor.Shown())
            foreach (var k in env.Keys)
            {
                double d = Math.Max(Math.Abs(XOf(k.Frame) - p.X), Math.Abs(YOf(k.Value) - p.Y));
                if (d < bestD) { bestD = d; best = Editor.Key(name, k.Frame); }
            }
        return best;
    }

    private void Measure()
    {
        if (Editor.Dragging) return;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var (_, env) in Editor.Shown())
        {
            foreach (var (_, v) in env.Sample(First, Last, 120)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            foreach (var k in env.Keys) { lo = Math.Min(lo, k.Value); hi = Math.Max(hi, k.Value); }
        }
        if (double.IsInfinity(lo)) (lo, hi) = (-1, 1);
        double pad = Math.Max((hi - lo) * 0.1, 0.5);
        _range = (lo - pad, hi + pad);
    }

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(new SolidColorBrush(Color.FromRgb(46, 46, 50)), new Rect(Bounds.Size));
        Measure();
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 76)), 1);
        for (double f = Math.Ceiling(First / 10) * 10; f <= Last; f += 10) dc.DrawLine(grid, new Point(XOf(f), 0), new Point(XOf(f), Bounds.Height));
        dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(0, YOf(0)), new Point(Bounds.Width, YOf(0)));
        double now = Editor.Session.Frame;
        dc.DrawLine(new Pen(Brushes.Yellow, 1), new Point(XOf(now), 0), new Point(XOf(now), Bounds.Height));
        var chosenCurves = Editor.ChosenKeys().Select(k => k.Channel).ToHashSet();
        double perFrame = XOf(First + 1) - XOf(First), perValue = YOf(0) - YOf(1);
        var late = new List<(EnvelopeKey, IBrush)>();
        foreach (var (name, env) in Editor.Shown())
        {
            var colour = _owner.ColourOf(name);
            bool bright = chosenCurves.Contains(name);
            var brush = new SolidColorBrush(bright ? Lighter(colour) : colour);
            var pen = new Pen(brush, bright ? 2.5 : 1.5);
            var pts = env.Sample(First, Last, Math.Max(200, (int)(Last - First) * 4));
            for (int i = 1; i < pts.Length; i++)
                dc.DrawLine(pen, new Point(XOf(pts[i - 1].Frame), YOf(pts[i - 1].Value)), new Point(XOf(pts[i].Frame), YOf(pts[i].Value)));
            foreach (var k in env.Keys)
            {
                bool chosen = Editor.IsSelected(new KeyId(Editor.Item!, name, k.Frame));
                if (chosen) late.Add((k, brush));
                else Dot(dc, k, brush, chosen: false);
            }
        }
        // Chosen keys go on top: white, with a Bezier key's slopes as handles either side.
        foreach (var (k, brush) in late)
        {
            if (k.Kind == SpanKind.Bezier)
            {
                var at = new Point(XOf(k.Frame), YOf(k.Value));
                var handle = new Pen(Brushes.White, 1);
                var before = at - Along(k.InSlope, perFrame, perValue);
                var after = at + Along(k.OutSlope, perFrame, perValue);
                dc.DrawLine(handle, at, before);
                dc.DrawLine(handle, at, after);
                dc.DrawRectangle(Brushes.White, null, new Rect(before - new Point(2, 2), new Size(4, 4)));
                dc.DrawRectangle(Brushes.White, null, new Rect(after - new Point(2, 2), new Size(4, 4)));
            }
            Dot(dc, k, brush, chosen: true);
        }
        if (_gesture == Gesture.Box && _moved)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(Brushes.White, 1, new DashStyle([3, 3], 0)), new Rect(_press, _now).Normalize());
        var typeface = new Typeface(FontFamily.Default);
        dc.DrawText(new FormattedText($"{_range.Hi:0.##}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.LightGray), new Point(2, 2));
        dc.DrawText(new FormattedText($"{_range.Lo:0.##}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.LightGray), new Point(2, Bounds.Height - 14));
    }

    /// <summary>A key's dot: round for TCB and Bezier keys, square for linear and stepped ones; white when chosen.</summary>
    private void Dot(DrawingContext dc, EnvelopeKey k, IBrush brush, bool chosen)
    {
        var at = new Point(XOf(k.Frame), YOf(k.Value));
        double r = chosen ? 5 : 4;
        var fill = chosen ? Brushes.White : brush;
        if (k.Kind is SpanKind.Stepped or SpanKind.Linear) dc.DrawRectangle(fill, new Pen(Brushes.Black, 1), new Rect(at.X - r, at.Y - r, 2 * r, 2 * r));
        else dc.DrawEllipse(fill, new Pen(Brushes.Black, 1), at, r, r);
    }

    /// <summary>A handle 22 pixels long along a slope (value per frame) on screen.</summary>
    private static Point Along(double slope, double perFrame, double perValue)
    {
        double dx = perFrame, dy = -slope * perValue;
        double len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? new Point(22, 0) : new Point(dx / len * 22, dy / len * 22);
    }

    private static Color Lighter(Color c) => Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        _press = _now = p;
        _moved = false;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _time = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || _owner.TimeMode.IsChecked == true;
        _pressedKey = null;
        if (KeyAt(p) is { } key)
        {
            _pressedKey = key;
            // A key not chosen is chosen at once (Shift adds it); a chosen one waits to see if it's dragged.
            if (!Editor.IsSelected(key)) Editor.Click(key, shift);
            else if (shift) { Editor.Click(key, toggle: true); _pressedKey = null; }
            _gesture = Editor.Selected.Count > 0 && !shift ? Gesture.Drag : Gesture.None;
        }
        else _gesture = Gesture.Box;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_gesture == Gesture.None) return;
        _now = e.GetPosition(this);
        if (!_moved && Math.Abs(_now.X - _press.X) + Math.Abs(_now.Y - _press.Y) > 3)
        {
            _moved = true;
            // Dragging keys, press to release, is one undo step.
            if (_gesture == Gesture.Drag) Editor.BeginDrag();
        }
        if (_gesture == Gesture.Drag && _moved && Editor.Dragging)
        {
            if (_time) Editor.DragBy(FrameAt(_now.X) - FrameAt(_press.X), 0);
            else Editor.DragBy(0, Math.Round(ValueAt(_now.Y) - ValueAt(_press.Y), 3));
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _now = e.GetPosition(this);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (_gesture)
        {
            case Gesture.Drag when _moved:
                Editor.EndDrag();
                break;
            case Gesture.Drag when _pressedKey is { } key:
                // A click on a chosen key, not dragged: choose it alone.
                Editor.Click(key, toggle: false);
                break;
            case Gesture.Box when _moved:
                Editor.Box(FrameAt(_press.X), FrameAt(_now.X), ValueAt(_press.Y), ValueAt(_now.Y), add: shift);
                break;
            case Gesture.Box:
                // A click on empty graph moves to that frame.
                if (!shift) Editor.ClearSelection();
                Editor.Session.SetFrame(Math.Round(FrameAt(_now.X)));
                break;
        }
        _gesture = Gesture.None;
        _pressedKey = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (Editor.Dragging) Editor.EndDrag();
        _gesture = Gesture.None;
        InvalidateVisual();
    }
}
