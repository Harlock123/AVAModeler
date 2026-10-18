using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.Modeler.UI;

/// <summary>The look shared by the Modeler's panels: classic grey, with yellow for what's chosen.</summary>
public static class Look
{
    public static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(88, 88, 92));
    public static readonly IBrush PanelDark = new SolidColorBrush(Color.FromRgb(62, 62, 66));
    public static readonly IBrush Button = new SolidColorBrush(Color.FromRgb(132, 132, 138));
    public static readonly IBrush ButtonOn = new SolidColorBrush(Color.FromRgb(255, 214, 74));
    public static readonly IBrush Text = new SolidColorBrush(Color.FromRgb(240, 240, 240));
    public static readonly IBrush TextDark = new SolidColorBrush(Color.FromRgb(20, 20, 20));
    public static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(160, 120, 255));

    public static Button MakeButton(string text, Action click, string? tip = null, double width = double.NaN)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(6, 2), Margin = new Thickness(1), FontSize = 12, Width = width,
            HorizontalContentAlignment = HorizontalAlignment.Center, Background = Button, Foreground = TextDark,
            CornerRadius = new CornerRadius(2),
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static void SetOn(Button b, bool on)
    {
        b.Background = on ? ButtonOn : Button;
        b.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
    }
}

/// <summary>
/// The tabs (Objects, Modify, Multiply, Polygon, Tools, Display) along the top, and the chosen tab's buttons down
/// the left, as classic Modeler had them.
/// </summary>
public sealed class ToolTabs : DockPanel
{
    private readonly ModelerSession _session;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _column = new() { Orientation = Orientation.Vertical, Width = 112 };
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly Dictionary<string, Button> _toolButtons = [];
    private string _tab = "Objects";

    public static readonly string[] TabNames = ["Objects", "Modify", "Multiply", "Polygon", "Tools", "Display"];

    /// <summary>Commands that aren't tools, by tab: (label, command name).</summary>
    public Dictionary<string, List<(string Label, string Command)>> Extras { get; } = new()
    {
        ["Objects"] = [],
        ["Modify"] = [],
        ["Multiply"] = [],
        ["Polygon"] = [],
        ["Tools"] = [("Cut", "Cut"), ("Copy", "Copy"), ("Paste", "Paste"), ("Swap Layers", "Swap Layers"), ("Run Macro", "Run Macro")],
        ["Display"] = [("Fit All", "Fit All"), ("Fit Selected", "Fit Selected"), ("Maximize", "Maximize View"), ("Wireframe", "Style Wireframe"),
            ("Shaded", "Style Shaded"), ("Shaded + Wire", "Style ShadedWireframe"), ("MetaNURBS", "MetaNURBS"), ("Backfaces", "Backface Tint"),
            ("Snap to Grid", "Snap"), ("Center: Selection", "Center Selection"), ("Center: Mouse", "Center Mouse"), ("Center: Origin", "Center Origin"),
            ("Surfaces…", "Surface Editor"), ("Preferences…", "Preferences")],
    };

    /// <summary>A tool button or command button was pressed.</summary>
    public event Action<string, bool>? Picked;

    public StackPanel Column => _column;
    public StackPanel TabStrip => _tabs;

    public ToolTabs(ModelerSession session)
    {
        _session = session;
        foreach (var name in TabNames)
        {
            var b = Look.MakeButton(name, () => ShowTab(name), width: 84);
            _tabButtons[name] = b;
            _tabs.Children.Add(b);
        }
        ShowTab(_tab);
        session.Changed += Highlight;
    }

    public void ShowTab(string tab)
    {
        _tab = tab;
        _column.Children.Clear();
        _toolButtons.Clear();
        foreach (var (name, b) in _tabButtons) Look.SetOn(b, name == tab);
        var tools = _session.Registry.ByTab().FirstOrDefault(g => g.Key == tab)?.ToList() ?? [];
        foreach (var tool in tools.OrderBy(t => Array.IndexOf(Order, t.Name) is var i && i < 0 ? 999 : i).ThenBy(t => t.Name))
        {
            string key = _session.Keymap.Bindings.TryGetValue(tool.Name, out var k) ? k : "";
            var b = Look.MakeButton(tool.Name, () => Picked?.Invoke(tool.Name, true), $"{tool.Name}{(key != "" ? $" ({key})" : "")}\n{tool.Help}", 108);
            _toolButtons[tool.Name] = b;
            _column.Children.Add(b);
        }
        if (Extras.TryGetValue(tab, out var extras) && extras.Count > 0)
        {
            if (tools.Count > 0) _column.Children.Add(new Separator { Margin = new Thickness(2, 6) });
            foreach (var (label, command) in extras)
                _column.Children.Add(Look.MakeButton(label, () => Picked?.Invoke(command, false), command, 108));
        }
        Highlight();
    }

    /// <summary>The classic order of each tab's buttons.</summary>
    private static readonly string[] Order =
    [
        "Box", "Ball", "Disc", "Cone", "Points", "Make Polygon", "Text",
        "Move", "Rotate", "Size", "Stretch", "Shear", "Bend", "Twist", "Taper", "Magnet", "Drag", "Jitter", "Smooth", "Center", "Quantize",
        "Extrude", "Lathe", "Mirror", "Clone", "Array", "Path Extrude", "Rail Extrude", "Bevel", "Smooth Shift",
        "Flip", "Triple", "Merge Points", "Align", "Unify", "Subdivide", "Metaform", "Surface", "Merge Polygons", "Split", "Remove",
        "Boolean", "Drill",
    ];

    private void Highlight()
    {
        foreach (var (name, b) in _toolButtons) Look.SetOn(b, _session.ActiveTool?.Name == name);
    }
}

/// <summary>
/// The numeric panel: an editor for each of a tool's parameters (numbers, X/Y/Z, on/off, choices, text), kept in
/// step with dragging. For an armed tool, edits update the preview at once; a requester tool's Apply runs it.
/// </summary>
public sealed class NumericPanel : Border
{
    private readonly ModelerSession _session;
    private readonly StackPanel _rows = new() { Spacing = 3, Margin = new Thickness(6) };
    private Tool? _tool;
    private bool _syncing;
    private readonly List<Action> _refresh = [];

    /// <summary>The Apply button was pressed with a tool that isn't armed (a requester).</summary>
    public event Action<Tool>? ApplyRequested;

    public NumericPanel(ModelerSession session)
    {
        _session = session;
        Background = Look.PanelDark;
        Width = 236;
        Child = new ScrollViewer { Content = _rows };
        session.Changed += Sync;
        Show(null);
    }

    public Tool? Tool => _tool;

    /// <summary>Shows a tool's parameters (or a note that there's nothing to show).</summary>
    public void Show(Tool? tool)
    {
        _tool = tool;
        _rows.Children.Clear();
        _refresh.Clear();
        _rows.Children.Add(new TextBlock { Text = tool == null ? "Numeric" : tool.Name, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Look.Text });
        if (tool == null)
        {
            _rows.Children.Add(Note("Pick a tool to see and type its numbers here."));
            return;
        }
        if (tool.Help != "") _rows.Children.Add(Note(tool.Help));
        foreach (var p in tool.Parameters) _rows.Children.Add(Editor(p));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (tool.Kind == ToolKind.Interactive)
        {
            buttons.Children.Add(Look.MakeButton("Apply", () => _session.CommitTool(), "Keep it (Enter)"));
            buttons.Children.Add(Look.MakeButton("Cancel", () => _session.CancelTool(), "Throw it away (Esc)"));
        }
        else buttons.Children.Add(Look.MakeButton("Apply", () => ApplyRequested?.Invoke(tool), "Run the tool with these numbers"));
        buttons.Children.Add(Look.MakeButton("Reset", () => { tool.Reset(); Show(tool); Changed(); }, "Back to the defaults"));
        _rows.Children.Add(buttons);
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 11, Opacity = 0.8 };

    private void Changed()
    {
        if (_syncing) return;
        _session.ToolParametersChanged();
    }

    /// <summary>Re-reads the values (a drag changed them).</summary>
    private void Sync()
    {
        if (_tool == null) return;
        _syncing = true;
        try { foreach (var r in _refresh) r(); }
        finally { _syncing = false; }
    }

    private Control Editor(ToolParameter p)
    {
        var label = new TextBlock { Text = p.Label + (p.Info.Distance ? " (m)" : p.Info.Angle ? " (°)" : ""), Foreground = Look.Text, FontSize = 11 };
        var row = new StackPanel { Spacing = 1 };
        row.Children.Add(label);
        var t = p.Type;
        if (t == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)p.Value!, Content = "", Foreground = Look.Text };
            cb.IsCheckedChanged += (_, _) => { p.Value = cb.IsChecked == true; Changed(); };
            _refresh.Add(() => cb.IsChecked = (bool)p.Value!);
            row.Children.Add(cb);
        }
        else if (t.IsEnum)
        {
            var names = Enum.GetNames(t);
            var combo = new ComboBox { ItemsSource = names, SelectedItem = p.Value!.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) { p.Value = s; Changed(); } };
            _refresh.Add(() => combo.SelectedItem = p.Value!.ToString());
            row.Children.Add(combo);
        }
        else if (t == typeof(string))
        {
            var box = new TextBox { Text = (string?)p.Value ?? "" };
            box.LostFocus += (_, _) => { p.Value = box.Text ?? ""; Changed(); };
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { p.Value = box.Text ?? ""; Changed(); } };
            _refresh.Add(() => { if (!box.IsFocused) box.Text = (string?)p.Value ?? ""; });
            row.Children.Add(box);
        }
        else if (t == typeof(Vec3))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var box = Number(() => ((Vec3)p.Value!)[axis], v =>
                {
                    var c = (Vec3)p.Value!;
                    p.Value = axis switch { 0 => c with { X = v }, 1 => c with { Y = v }, _ => c with { Z = v } };
                }, p.Info);
                Grid.SetColumn(box, i);
                grid.Children.Add(box);
            }
            row.Children.Add(grid);
        }
        else row.Children.Add(Number(() => Convert.ToDouble(p.Value, CultureInfo.InvariantCulture), v => p.Value = t == typeof(int) ? (object)(int)Math.Round(v) : v, p.Info));
        return row;
    }

    /// <summary>A number box: type a value (Enter or leaving the box applies it); the wheel steps it.</summary>
    private TextBox Number(Func<double> get, Action<double> set, ParamAttribute info)
    {
        string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var box = new TextBox { Text = Format(get()), FontSize = 11, Margin = new Thickness(1, 0), MinWidth = 0 };
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                v = Math.Clamp(v, info.Min, info.Max);
                set(v);
                Changed();
            }
            box.Text = Format(get());
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
        box.PointerWheelChanged += (_, e) =>
        {
            set(Math.Clamp(get() + Math.Sign(e.Delta.Y) * info.Step, info.Min, info.Max));
            box.Text = Format(get());
            Changed();
            e.Handled = true;
        };
        _refresh.Add(() => { if (!box.IsFocused) box.Text = Format(get()); });
        return box;
    }
}

/// <summary>
/// The layer buttons: for each of the ten layers, its top half puts it in the foreground and its bottom half in the
/// background (Shift adds to what's there); a dot shows a layer has something in it.
/// </summary>
public sealed class LayerBar : StackPanel
{
    private readonly ModelerSession _session;
    private readonly List<(Button Fg, Button Bg)> _buttons = [];

    public LayerBar(ModelerSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        Spacing = 1;
        Children.Add(new TextBlock { Text = "Layers", Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), FontSize = 11 });
        for (int i = 0; i < Document.DefaultLayers; i++)
        {
            int layer = i;
            var fg = Look.MakeButton((i + 1).ToString(), () => Pick(layer, foreground: true), $"Layer {i + 1}: foreground (Shift: add)", 26);
            var bg = Look.MakeButton(" ", () => Pick(layer, foreground: false), $"Layer {i + 1}: background (Shift: add)", 26);
            fg.Padding = bg.Padding = new Thickness(0);
            fg.Height = 18;
            bg.Height = 10;
            _buttons.Add((fg, bg));
            Children.Add(new StackPanel { Spacing = 0, Children = { fg, bg } });
        }
        session.Changed += Refresh;
        Refresh();
    }

    private static bool Shift() => LastModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);

    /// <summary>The keyboard's modifiers when last seen (the workspace keeps this up to date).</summary>
    public static Avalonia.Input.KeyModifiers LastModifiers { get; set; }

    private void Pick(int layer, bool foreground)
    {
        var doc = _session.Document;
        _session.CommitTool();
        var fg = doc.ForegroundLayers.ToHashSet();
        var bg = doc.BackgroundLayers.ToHashSet();
        if (foreground)
        {
            if (!Shift()) fg.Clear();
            fg.Add(layer);
            bg.Remove(layer);
        }
        else
        {
            if (!Shift()) bg.Clear();
            if (!bg.Add(layer)) bg.Remove(layer);
            fg.Remove(layer);
        }
        doc.SetLayers(fg, bg);
    }

    private void Refresh()
    {
        var doc = _session.Document;
        for (int i = 0; i < _buttons.Count && i < doc.Layers.Count; i++)
        {
            var (fg, bg) = _buttons[i];
            var state = doc.Layers[i].State;
            Look.SetOn(fg, state == LayerState.Foreground);
            bg.Background = state == LayerState.Background ? Look.Background : Look.Button;
            fg.Content = (i + 1) + (doc.Layers[i].Mesh.IsEmpty ? "" : "•");
        }
    }
}

/// <summary>The status bar: the selection mode, counts, the grid and the latest message.</summary>
public sealed class StatusBar : DockPanel
{
    private readonly ModelerSession _session;
    private readonly TextBlock _counts = new() { Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _message = new() { Foreground = Look.ButtonOn, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<SelectionMode, Button> _modes = [];

    public StatusBar(ModelerSession session, Control layers)
    {
        _session = session;
        Background = Look.Panel;
        Height = 34;
        var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var m in new[] { SelectionMode.Points, SelectionMode.Polygons, SelectionMode.Volume, SelectionMode.Edges })
        {
            var b = Look.MakeButton(m.ToString(), () => session.SetSelectionMode(m), $"Select {m.ToString().ToLowerInvariant()} (Space cycles)", 70);
            _modes[m] = b;
            modes.Children.Add(b);
        }
        SetDock(modes, Dock.Left);
        Children.Add(modes);
        SetDock(layers, Dock.Right);
        Children.Add(layers);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _counts, _message } };
        Children.Add(text);
        session.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (m, b) in _modes) Look.SetOn(b, _session.Document.Selection.Mode == m);
        _counts.Text = _session.Counts();
        _message.Text = _session.Status;
    }
}
