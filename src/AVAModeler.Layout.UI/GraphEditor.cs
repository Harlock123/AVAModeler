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
/// The graph editor: the chosen item's channels as curves over the frames, each key a dot. Click a key to choose it
/// and edit its frame, value, tension, continuity and bias (and whether it's linear); drag it up and down to change
/// its value. The current frame is the vertical line; clicking empty graph moves to that frame.
/// </summary>
public sealed class GraphEditor : DockPanel
{
    private readonly LayoutSession _session;
    private readonly Graph _graph;
    private readonly StackPanel _channels = new() { Spacing = 1, Width = 96 };
    private readonly StackPanel _key = new() { Spacing = 2, Width = 168 };
    private readonly HashSet<Channel> _shown = [Channel.PositionX, Channel.PositionY, Channel.PositionZ];
    private (Channel Channel, int Index)? _chosen;

    private static readonly Color[] Colours =
    [
        Color.FromRgb(255, 90, 90), Color.FromRgb(90, 230, 90), Color.FromRgb(100, 150, 255),
        Color.FromRgb(255, 170, 60), Color.FromRgb(220, 110, 255), Color.FromRgb(60, 220, 220),
        Color.FromRgb(255, 140, 140), Color.FromRgb(150, 255, 150), Color.FromRgb(150, 180, 255),
    ];

    public GraphEditor(LayoutSession session)
    {
        _session = session;
        Background = Look.PanelDark;
        _graph = new Graph(this);
        SetDock(_channels, Dock.Left);
        SetDock(_key, Dock.Right);
        Children.Add(_channels);
        Children.Add(_key);
        Children.Add(_graph);
        foreach (var ch in Enum.GetValues<Channel>())
        {
            var c = ch;
            var cb = new CheckBox { Content = ch.ToString(), IsChecked = _shown.Contains(ch), Foreground = new SolidColorBrush(Colours[(int)ch]), FontSize = 11 };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) _shown.Add(c); else _shown.Remove(c); _graph.InvalidateVisual(); };
            _channels.Children.Add(cb);
        }
        session.Changed += () => { _graph.InvalidateVisual(); ShowKey(); };
        ShowKey();
    }

    private Envelope? Env(Channel c) => _session.Selected?[c];

    private void ShowKey()
    {
        _key.Children.Clear();
        _key.Children.Add(new TextBlock { Text = "Key", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        if (_chosen is not { } k || Env(k.Channel) is not { } env || k.Index >= env.Keys.Count)
        {
            _key.Children.Add(new TextBlock { Text = "Click a key on a curve.", Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var key = env.Keys[k.Index];
        _key.Children.Add(new TextBlock { Text = k.Channel.ToString(), Foreground = new SolidColorBrush(Colours[(int)k.Channel]), FontSize = 11 });
        void Field(string label, double value, Func<EnvelopeKey, double, EnvelopeKey> set)
        {
            var box = new TextBox { Text = value.ToString("0.####", CultureInfo.InvariantCulture), FontSize = 11 };
            void Apply()
            {
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
                if (_chosen is not { } ch || Env(ch.Channel) is not { } e || ch.Index >= e.Keys.Count) return;
                _chosen = (ch.Channel, e.SetKey(ch.Index, set(e.Keys[ch.Index], v)));
                _session.Scene.Notify();
            }
            box.LostFocus += (_, _) => Apply();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            _key.Children.Add(new DockPanel { Children = { Docked(new TextBlock { Text = label, Width = 70, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, Dock.Left), box } });
        }
        Field("Frame", key.Frame, (k, v) => k with { Frame = v });
        Field("Value", key.Value, (k, v) => k with { Value = v });
        Field("Tension", key.Tension, (k, v) => k with { Tension = Math.Clamp(v, -1, 1) });
        Field("Continuity", key.Continuity, (k, v) => k with { Continuity = Math.Clamp(v, -1, 1) });
        Field("Bias", key.Bias, (k, v) => k with { Bias = Math.Clamp(v, -1, 1) });
        var linear = new CheckBox { Content = "Linear in", IsChecked = key.Linear, Foreground = Look.Text, FontSize = 11 };
        linear.IsCheckedChanged += (_, _) =>
        {
            if (_chosen is not { } ch || Env(ch.Channel) is not { } e || ch.Index >= e.Keys.Count) return;
            _chosen = (ch.Channel, e.SetKey(ch.Index, e.Keys[ch.Index] with { Linear = linear.IsChecked == true }));
            _session.Scene.Notify();
        };
        _key.Children.Add(linear);
        _key.Children.Add(Look.MakeButton("Delete key", () =>
        {
            if (_chosen is not { } ch || Env(ch.Channel) is not { } e || ch.Index >= e.Keys.Count || e.Keys.Count <= 1) return;
            e.RemoveKey(ch.Index);
            _chosen = null;
            _session.Scene.Notify();
        }));
        var pre = new ComboBox { ItemsSource = Enum.GetNames<EndBehavior>(), SelectedItem = env.PreBehavior.ToString(), FontSize = 11 };
        var post = new ComboBox { ItemsSource = Enum.GetNames<EndBehavior>(), SelectedItem = env.PostBehavior.ToString(), FontSize = 11 };
        pre.SelectionChanged += (_, _) => { if (Enum.TryParse<EndBehavior>(pre.SelectedItem as string, out var b)) { env.PreBehavior = b; _session.Scene.Notify(); } };
        post.SelectionChanged += (_, _) => { if (Enum.TryParse<EndBehavior>(post.SelectedItem as string, out var b)) { env.PostBehavior = b; _session.Scene.Notify(); } };
        _key.Children.Add(new TextBlock { Text = "Before / after the keys", Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        _key.Children.Add(pre);
        _key.Children.Add(post);
    }

    private static Control Docked(Control c, Dock d)
    {
        SetDock(c, d);
        return c;
    }

    /// <summary>The plot itself.</summary>
    private sealed class Graph(GraphEditor editor) : Control
    {
        private (double Lo, double Hi) _range = (-1, 1);
        private bool _dragging;

        private Scene.Scene Scene => editor._session.Scene;
        private double First => Scene.FirstFrame;
        private double Last => Math.Max(Scene.LastFrame, Scene.FirstFrame + 1);

        private double X(double frame) => 30 + (frame - First) / (Last - First) * Math.Max(1, Bounds.Width - 40);
        private double Frame(double x) => First + (x - 30) / Math.Max(1, Bounds.Width - 40) * (Last - First);
        private double Y(double v) => 10 + (1 - (v - _range.Lo) / Math.Max(1e-9, _range.Hi - _range.Lo)) * Math.Max(1, Bounds.Height - 20);
        private double Value(double y) => _range.Lo + (1 - (y - 10) / Math.Max(1, Bounds.Height - 20)) * (_range.Hi - _range.Lo);

        private void Measure()
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var ch in editor._shown)
                if (editor.Env(ch) is { } env)
                    foreach (var (_, v) in env.Sample(First, Last, 120)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            if (double.IsInfinity(lo)) (lo, hi) = (-1, 1);
            double pad = Math.Max((hi - lo) * 0.1, 0.5);
            _range = (lo - pad, hi + pad);
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(new SolidColorBrush(Color.FromRgb(46, 46, 50)), new Rect(Bounds.Size));
            if (!_dragging) Measure();
            var grid = new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 76)), 1);
            for (double f = Math.Ceiling(First / 10) * 10; f <= Last; f += 10) dc.DrawLine(grid, new Point(X(f), 0), new Point(X(f), Bounds.Height));
            dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(0, Y(0)), new Point(Bounds.Width, Y(0)));
            dc.DrawLine(new Pen(Brushes.Yellow, 1), new Point(X(editor._session.Frame), 0), new Point(X(editor._session.Frame), Bounds.Height));
            foreach (var ch in editor._shown)
            {
                if (editor.Env(ch) is not { } env) continue;
                var brush = new SolidColorBrush(Colours[(int)ch]);
                var pen = new Pen(brush, 1.5);
                var pts = env.Sample(First, Last, 200);
                for (int i = 1; i < pts.Length; i++)
                    dc.DrawLine(pen, new Point(X(pts[i - 1].Frame), Y(pts[i - 1].Value)), new Point(X(pts[i].Frame), Y(pts[i].Value)));
                for (int i = 0; i < env.Keys.Count; i++)
                {
                    var k = env.Keys[i];
                    bool chosen = editor._chosen == (ch, i);
                    dc.DrawEllipse(chosen ? Brushes.White : brush, new Pen(Brushes.Black, 1), new Point(X(k.Frame), Y(k.Value)), chosen ? 5 : 4, chosen ? 5 : 4);
                }
            }
            dc.DrawText(new FormattedText($"{_range.Hi:0.##}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10, Brushes.LightGray), new Point(2, 2));
            dc.DrawText(new FormattedText($"{_range.Lo:0.##}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10, Brushes.LightGray), new Point(2, Bounds.Height - 14));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(this);
            editor._chosen = null;
            foreach (var ch in editor._shown)
            {
                if (editor.Env(ch) is not { } env) continue;
                for (int i = 0; i < env.Keys.Count; i++)
                {
                    var k = env.Keys[i];
                    if (Math.Abs(X(k.Frame) - p.X) < 6 && Math.Abs(Y(k.Value) - p.Y) < 6) editor._chosen = (ch, i);
                }
            }
            if (editor._chosen == null) editor._session.SetFrame(Math.Round(Frame(p.X)));
            else
            {
                _dragging = true;
                e.Pointer.Capture(this);
            }
            editor.ShowKey();
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging || editor._chosen is not { } k || editor.Env(k.Channel) is not { } env) return;
            var p = e.GetPosition(this);
            editor._chosen = (k.Channel, env.SetKey(k.Index, env.Keys[k.Index] with { Value = Math.Round(Value(p.Y), 3) }));
            editor._session.Scene.Notify();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = false;
            e.Pointer.Capture(null);
            editor.ShowKey();
            InvalidateVisual();
        }
    }
}
