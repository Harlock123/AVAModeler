using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AVAModeler.Core;
using PixelFormat = Avalonia.Platform.PixelFormat;
using GradientStop = AVAModeler.Core.GradientStop;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// The node editor for a surface: its node graph drawn as boxes with their inputs on the left and outputs on the right,
/// wires between them, the chosen node's settings beside it, and a ball shaded with the graph (as renders show it; the
/// views show the surface's own settings). Drag a node by its title to move it; drag from an output to an input to link
/// them, or from a linked input to unlink it (dropping it on another input moves the link); Delete takes the chosen
/// node away. It changes the surface live; the surface editor that opened it makes the visit one undo step.
/// </summary>
public sealed class NodeEditor : Window
{
    private readonly Surface _surface;
    private readonly Action _changed;
    private readonly StackPanel _fields = new() { Spacing = 3, Width = 230 };
    private readonly Image _preview = new() { Width = 160, Height = 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _note = new() { Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _add = new() { ItemsSource = SurfaceNodes.All.Where(t => t.Kind != NodeKind.Output).Select(t => t.Name).ToList(), SelectedIndex = 0, FontSize = 11, Width = 110 };

    /// <summary>The graph drawn (the surface's own: edits change it at once).</summary>
    public NodeCanvas Canvas { get; }

    public NodeEditor(Surface surface, Action changed)
    {
        _surface = surface;
        _changed = changed;
        surface.Nodes ??= SurfaceNodeGraph.New();
        Title = $"{surface.Name}: Nodes";
        Width = 1100;
        Height = 640;
        Background = Look.Panel;
        Canvas = new NodeCanvas(surface.Nodes);
        Canvas.Edited += () => { Changed(); };
        Canvas.ChosenChanged += ShowNode;
        Canvas.Problem += m => _note.Text = m;
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                _add,
                Look.MakeButton("Add Node", () => AddNode((NodeKind)Enum.Parse(typeof(NodeKind), (string)_add.SelectedItem!)), "Add a node of the kind chosen beside"),
                Look.MakeButton("Delete Node", DeleteChosen, "Take the chosen node away (Delete)"),
                Look.MakeButton("Clear Graph", ClearGraph, "Take every node away but the output: the surface looks as its own settings say"),
            },
        };
        var side = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0), Children = { new TextBlock { Text = "Preview", Foreground = Look.Text, FontSize = 11 }, _preview, _note, _fields } };
        DockPanel.SetDock(bar, Dock.Top);
        var scroller = new ScrollViewer { Content = side, Width = 250 };
        DockPanel.SetDock(scroller, Dock.Right);
        Content = new DockPanel { Margin = new Thickness(8), Children = { bar, scroller, new Border { Background = Look.PanelDark, ClipToBounds = true, Child = Canvas, Margin = new Thickness(0, 6, 0, 0) } } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && FocusManager?.GetFocusedElement() is not TextBox) { DeleteChosen(); e.Handled = true; }
        };
        ShowNode();
        Preview();
    }

    public SurfaceNodeGraph Graph => _surface.Nodes!;

    private void Changed()
    {
        _changed();
        Preview();
        Canvas.InvalidateVisual();
    }

    /// <summary>Adds a node of a kind, chosen, a little right of the last one.</summary>
    public SurfaceNode AddNode(NodeKind kind)
    {
        var at = Graph.Nodes.Where(n => n.Kind != NodeKind.Output).Select(n => (n.X, n.Y)).DefaultIfEmpty((0, 0)).Last();
        var n = Graph.Add(kind, at.X + 30, at.Y + 30);
        Canvas.Choose(n.Id);
        Changed();
        return n;
    }

    public void DeleteChosen()
    {
        if (Canvas.Chosen is not { } n) return;
        if (n.Kind == NodeKind.Output) { _note.Text = "The output node stays: Clear Graph takes everything linked into it away."; return; }
        Graph.Remove(n.Id);
        Canvas.Choose(null);
        Changed();
    }

    private void ClearGraph()
    {
        Graph.Nodes.RemoveAll(n => n.Kind != NodeKind.Output);
        foreach (var n in Graph.Nodes) n.Links.Clear();
        Canvas.Choose(Graph.Output?.Id);
        Changed();
    }

    /// <summary>The chosen node's settings: its option, its settings, and its unlinked inputs' constants.</summary>
    private void ShowNode()
    {
        _fields.Children.Clear();
        _note.Text = "";
        if (Canvas.Chosen is not { } n) { _fields.Children.Add(Label("Click a node to set it up.")); return; }
        var t = n.Type;
        _fields.Children.Add(new TextBlock { Text = $"{n.Kind} (node {n.Id})", Foreground = Look.Text, FontWeight = FontWeight.Bold });
        _fields.Children.Add(new TextBlock { Text = t.Summary, Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (t.Options.Count > 0)
        {
            var option = new ComboBox { Name = "NodeOption", ItemsSource = t.Options, SelectedItem = t.Options.FirstOrDefault(o => o == n.Option) ?? t.Options[0], FontSize = 11 };
            option.SelectionChanged += (_, _) =>
            {
                if (option.SelectedItem is not string o || o == n.Option) return;
                n.Option = o;
                // A texture's own settings follow its procedural.
                if (n.Kind == NodeKind.Texture) foreach (var k in n.Numbers.Keys.Where(k => t.Setting(k) == null).ToList()) n.Numbers.Remove(k);
                Changed();
                ShowNode();
            };
            _fields.Children.Add(option);
        }
        var settings = t.Settings.Select(s => (s.Name, s.Min, s.Max)).ToList();
        if (n.Kind == NodeKind.Texture && Procedurals.Find(n.Option) is { } proc) settings.AddRange(proc.Parameters.Select(p => (p.Name, p.Min, p.Max)));
        foreach (var (name, min, max) in settings) _fields.Children.Add(Number(n, name, min, max));
        foreach (var p in t.Inputs)
        {
            if (n.Links.ContainsKey(p.Name)) { _fields.Children.Add(Label($"{p.Name}: linked")); continue; }
            _fields.Children.Add(p.Type == PortType.Value ? Number(n, p.Name, double.NegativeInfinity, double.PositiveInfinity) : ColourRow(n, p.Name));
        }
        if (n.Kind == NodeKind.Gradient)
        {
            _fields.Children.Add(Label("Stops: at (0–1), red, green, blue (0–255), one a line"));
            var stops = new TextBox
            {
                Name = "GradientStops", AcceptsReturn = true, FontSize = 11, Height = 90,
                Text = string.Join("\n", n.Stops.Select(s => FormattableString.Invariant($"{s.At:0.###} {s.Color.R} {s.Color.G} {s.Color.B}"))),
            };
            stops.LostFocus += (_, _) =>
            {
                try
                {
                    var list = new List<GradientStop>();
                    foreach (var line in (stops.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var v = line.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                        if (v.Length != 4 || v.Skip(1).Any(c => c < 0 || c > 255)) throw new FormatException();
                        list.Add(new GradientStop(v[0], new Rgb((byte)v[1], (byte)v[2], (byte)v[3])));
                    }
                    if (list.Count == 0) throw new FormatException();
                    n.Stops = [.. list.OrderBy(s => s.At)];
                    _note.Text = "";
                    Changed();
                }
                catch (FormatException) { _note.Text = "Each stop is a line: at (0 to 1), then red, green and blue (0 to 255)."; }
            };
            _fields.Children.Add(stops);
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private Control Number(SurfaceNode n, string name, double min, double max)
    {
        var box = new TextBox { Name = "Node" + name.Replace(" ", ""), Text = n.Number(name).ToString("0.####", CultureInfo.InvariantCulture), FontSize = 11, Width = 90 };
        box.LostFocus += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < min || v > max)
            {
                _note.Text = double.IsFinite(min) ? $"{name} is a number from {min:0.###} to {max:0.###}." : $"{name} is a number.";
                box.Text = n.Number(name).ToString("0.####", CultureInfo.InvariantCulture);
                return;
            }
            if (v == n.Number(name)) return;
            n.Numbers[name] = v;
            _note.Text = "";
            Changed();
        };
        return Row(name, box);
    }

    private Control ColourRow(SurfaceNode n, string name)
    {
        var c = n.ColorOf(name);
        var box = new TextBox { Name = "Node" + name + "Colour", Text = $"{c.R} {c.G} {c.B}", FontSize = 11, Width = 90 };
        var swatch = new Border { Width = 18, Height = 18, Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), Margin = new Thickness(4, 0, 0, 0) };
        box.LostFocus += (_, _) =>
        {
            var v = (box.Text ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (v.Length != 3 || !v.All(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k is >= 0 and <= 255))
            {
                _note.Text = $"{name} is red, green and blue, three whole numbers from 0 to 255.";
                return;
            }
            var rgb = new Rgb(byte.Parse(v[0], CultureInfo.InvariantCulture), byte.Parse(v[1], CultureInfo.InvariantCulture), byte.Parse(v[2], CultureInfo.InvariantCulture));
            n.Colors[name] = rgb;
            swatch.Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
            _note.Text = "";
            Changed();
        };
        var row = Row(name, box);
        ((StackPanel)row).Children.Add(swatch);
        return row;
    }

    private static Control Row(string label, Control field) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        Children = { new TextBlock { Text = label, Foreground = Look.Text, FontSize = 11, Width = 110, VerticalAlignment = VerticalAlignment.Center }, field },
    };

    /// <summary>The preview picture: a ball half a metre across, lit from the upper left, shaded with the graph.</summary>
    public WriteableBitmap? PreviewImage { get; private set; }

    private void Preview()
    {
        var pixels = PreviewBall.Draw(_surface, 128);
        var bmp = new WriteableBitmap(new PixelSize(128, 128), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
            for (int y = 0; y < 128; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * 128, fb.Address + y * fb.RowBytes, 128);
        PreviewImage = bmp;
        _preview.Source = bmp;
    }
}

/// <summary>A ball shaded with a surface and its node graph, for the node editor's preview (no ray tracer: one light, from the upper left).</summary>
public static class PreviewBall
{
    /// <summary>A size × size picture, BGRA as whole numbers, top row first.</summary>
    public static int[] Draw(Surface s, int size)
    {
        var program = SurfaceNodes.Compile(s.Nodes);
        var light = new Vec3(-0.5, 0.6, -0.6).Normalized();
        var px = new int[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double u = (x + 0.5) / size * 2 - 1, v = 1 - (y + 0.5) / size * 2;
                double r2 = u * u + v * v;
                Vec3 c;
                if (r2 > 1) c = ((x / 8 + y / 8) & 1) == 0 ? new Vec3(0.25, 0.25, 0.27) : new Vec3(0.32, 0.32, 0.35);
                else
                {
                    var n = new Vec3(u, v, -Math.Sqrt(1 - r2));
                    var p = n * 0.25;
                    var look = program?.Evaluate(new NodeContext(p, p, n, new Vec3(0, 0, -1)));
                    var color = look?.Color ?? s.Color.ToVec();
                    double diffuse = look?.Diffuse ?? s.Diffuse, specular = look?.Specular ?? s.Specular, lum = look?.Luminosity ?? s.Luminosity;
                    double ndl = Math.Max(0, Vec3.Dot(n, light));
                    var h = (light + new Vec3(0, 0, -1)).Normalized();
                    double spec = specular * Math.Pow(Math.Max(0, Vec3.Dot(n, h)), Math.Max(1, s.Glossiness) / 4);
                    c = color * (diffuse * (0.15 + 0.85 * ndl) + lum) + Vec3.One * spec;
                }
                int R = (int)Math.Clamp(c.X * 255, 0, 255), G = (int)Math.Clamp(c.Y * 255, 0, 255), B = (int)Math.Clamp(c.Z * 255, 0, 255);
                px[y * size + x] = unchecked((int)0xFF000000) | R << 16 | G << 8 | B;
            }
        return px;
    }
}

/// <summary>The graph's boxes and wires, and the mouse on them (<see cref="Press"/>, <see cref="Move"/>, <see cref="Release"/>, which tests call too).</summary>
public sealed class NodeCanvas : Control
{
    public const double NodeWidth = 170, Header = 22, RowHeight = 18;

    private readonly SurfaceNodeGraph _graph;
    private int? _chosen;
    private (int Node, double Dx, double Dy)? _moving;
    private (int Node, string Output)? _wire;
    private Point _mouse;

    public NodeCanvas(SurfaceNodeGraph graph)
    {
        _graph = graph;
        ClipToBounds = true;
        Focusable = true;
        _chosen = graph.Output?.Id;
    }

    /// <summary>Raised after the graph changes (a link made or taken away, a node moved).</summary>
    public event Action? Edited;
    public event Action? ChosenChanged;
    /// <summary>Raised with a line to show when a link can't be made.</summary>
    public event Action<string>? Problem;

    public SurfaceNode? Chosen => _chosen is { } id ? _graph.Find(id) : null;

    public void Choose(int? id)
    {
        _chosen = id;
        ChosenChanged?.Invoke();
        InvalidateVisual();
    }

    public static double BoxHeight(SurfaceNode n) => Header + Math.Max(1, Math.Max(n.Type.Inputs.Count, n.Type.Outputs.Count)) * RowHeight + 6;

    /// <summary>Where a node's input or output port is drawn.</summary>
    public Point PortPoint(SurfaceNode n, string port, bool output)
    {
        var list = output ? n.Type.Outputs : n.Type.Inputs;
        int i = list.ToList().FindIndex(p => p.Name == port);
        return new Point(n.X + (output ? NodeWidth : 0), n.Y + Header + RowHeight * (Math.Max(0, i) + 0.5));
    }

    private (SurfaceNode Node, string Port, bool Output)? PortAt(Point p)
    {
        foreach (var n in Enumerable.Reverse(_graph.Nodes))
        {
            foreach (var o in n.Type.Outputs)
                if (Near(PortPoint(n, o.Name, true), p)) return (n, o.Name, true);
            foreach (var i in n.Type.Inputs)
                if (Near(PortPoint(n, i.Name, false), p)) return (n, i.Name, false);
        }
        return null;
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= 8 && Math.Abs(a.Y - b.Y) <= 8;

    private SurfaceNode? NodeAt(Point p) =>
        Enumerable.Reverse(_graph.Nodes).FirstOrDefault(n => p.X >= n.X && p.X <= n.X + NodeWidth && p.Y >= n.Y && p.Y <= n.Y + BoxHeight(n));

    public void Press(Point p)
    {
        _mouse = p;
        if (PortAt(p) is { } port)
        {
            if (port.Output) _wire = (port.Node.Id, port.Port);
            else if (port.Node.Links.TryGetValue(port.Port, out var link))
            {
                // Picking a wire up off an input: unlinked, and carried to wherever it's dropped.
                port.Node.Links.Remove(port.Port);
                _wire = (link.Node, link.Output);
                Edited?.Invoke();
            }
            InvalidateVisual();
            return;
        }
        if (NodeAt(p) is { } n)
        {
            Choose(n.Id);
            _moving = (n.Id, p.X - n.X, p.Y - n.Y);
            return;
        }
        Choose(null);
    }

    public void Move(Point p)
    {
        _mouse = p;
        if (_moving is { } m && _graph.Find(m.Node) is { } n)
        {
            n.X = Math.Max(0, p.X - m.Dx);
            n.Y = Math.Max(0, p.Y - m.Dy);
        }
        if (_moving != null || _wire != null) InvalidateVisual();
    }

    public void Release(Point p)
    {
        if (_wire is { } w)
        {
            if (PortAt(p) is { Output: false } target)
            {
                try { _graph.Link(w.Node, w.Output, target.Node.Id, target.Port); }
                catch (ArgumentException e) { Problem?.Invoke(e.Message); }
            }
            Edited?.Invoke();
        }
        else if (_moving != null) Edited?.Invoke();
        _wire = null;
        _moving = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Press(e.GetPosition(this));
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Move(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Release(e.GetPosition(this));
        e.Pointer.Capture(null);
    }

    private static readonly IBrush Box = new SolidColorBrush(Color.FromRgb(108, 108, 114));
    private static readonly IBrush Title = new SolidColorBrush(Color.FromRgb(70, 92, 140));
    private static readonly IBrush OutputTitle = new SolidColorBrush(Color.FromRgb(140, 92, 60));
    private static readonly IBrush ValuePort = new SolidColorBrush(Color.FromRgb(200, 200, 200));
    private static readonly IBrush ColourPort = new SolidColorBrush(Color.FromRgb(255, 200, 80));
    private static readonly Pen Wire = new(new SolidColorBrush(Color.FromRgb(230, 230, 230)), 2);
    private static readonly Pen Edge = new(new SolidColorBrush(Color.FromRgb(40, 40, 44)), 1);
    private static readonly Pen ChosenEdge = new(new SolidColorBrush(Color.FromRgb(255, 214, 74)), 2);

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        foreach (var n in _graph.Nodes)
            foreach (var (input, link) in n.Links)
                if (_graph.Find(link.Node) is { } from) DrawWire(dc, PortPoint(from, link.Output, true), PortPoint(n, input, false));
        if (_wire is { } w && _graph.Find(w.Node) is { } start) DrawWire(dc, PortPoint(start, w.Output, true), _mouse);
        foreach (var n in _graph.Nodes) DrawNode(dc, n);
    }

    private static void DrawWire(DrawingContext dc, Point a, Point b)
    {
        double dx = Math.Max(30, Math.Abs(b.X - a.X) / 2);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false);
            c.CubicBezierTo(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), b);
        }
        dc.DrawGeometry(null, Wire, g);
    }

    private void DrawNode(DrawingContext dc, SurfaceNode n)
    {
        var t = n.Type;
        var rect = new Rect(n.X, n.Y, NodeWidth, BoxHeight(n));
        dc.DrawRectangle(Box, n.Id == _chosen ? ChosenEdge : Edge, rect, 4, 4);
        dc.DrawRectangle(n.Kind == NodeKind.Output ? OutputTitle : Title, null, new Rect(n.X, n.Y, NodeWidth, Header), 4, 4);
        string title = n.Kind == NodeKind.Output ? "Surface" : t.Options.Count > 0 ? $"{n.Kind}: {n.Option}" : n.Kind.ToString();
        Text(dc, title, n.X + 6, n.Y + 4, 12, Brushes.White);
        for (int i = 0; i < t.Inputs.Count; i++)
        {
            var p = t.Inputs[i];
            var at = PortPoint(n, p.Name, false);
            dc.DrawEllipse(p.Type == PortType.Color ? ColourPort : ValuePort, Edge, at, 5, 5);
            string label = p.Name;
            if (!n.Links.ContainsKey(p.Name) && n.Kind != NodeKind.Output)
                label += p.Type == PortType.Value ? " " + n.Number(p.Name).ToString("0.##", CultureInfo.InvariantCulture) : "";
            Text(dc, label, at.X + 9, at.Y - 7, 11, Brushes.White);
        }
        for (int i = 0; i < t.Outputs.Count; i++)
        {
            var p = t.Outputs[i];
            var at = PortPoint(n, p.Name, true);
            dc.DrawEllipse(p.Type == PortType.Color ? ColourPort : ValuePort, Edge, at, 5, 5);
            var ft = Format(p.Name, 11, Brushes.White);
            dc.DrawText(ft, new Point(at.X - 9 - ft.Width, at.Y - 7));
        }
    }

    private static FormattedText Format(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);

    private static void Text(DrawingContext dc, string text, double x, double y, double size, IBrush brush) => dc.DrawText(Format(text, size, brush), new Point(x, y));
}
