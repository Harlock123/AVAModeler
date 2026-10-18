using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Rendering;
using AVAModeler.Tools;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// The Modeler workspace: menus and tool tabs along the top, the chosen tab's tools down the left, the quad view,
/// the numeric panel on the right, and the status bar with the selection modes and layer buttons below.
/// </summary>
public sealed class ModelerWorkspace : DockPanel
{
    public ModelerSession Session { get; }
    public QuadView Views { get; private set; }
    public NumericPanel Numeric { get; }
    public ToolTabs Tabs { get; }
    public Menu Menu { get; }
    private readonly Dictionary<string, Action> _commands;
    private readonly MenuItem _recent = new() { Header = "Recent" };

    /// <summary>Raised to switch to the Layout workspace.</summary>
    public event Action? ShowLayout;

    public ModelerWorkspace(ModelerSession session)
    {
        Session = session;
        Background = Look.Panel;
        Views = new QuadView(session, session.Preferences.Renderer == "OpenGL");
        Numeric = new NumericPanel(session);
        Tabs = new ToolTabs(session);
        _commands = BuildCommands();
        Menu = BuildMenu();

        var header = new StackPanel { Background = Look.PanelDark };
        header.Children.Add(Menu);
        header.Children.Add(new DockPanel { Children = { Tabs.TabStrip } });
        SetDock(header, Dock.Top);
        Children.Add(header);
        var status = new StatusBar(session, new LayerBar(session));
        SetDock(status, Dock.Bottom);
        Children.Add(status);
        var left = new ScrollViewer { Content = Tabs.Column, Background = Look.Panel };
        SetDock(left, Dock.Left);
        Children.Add(left);
        SetDock(Numeric, Dock.Right);
        Children.Add(Numeric);
        Children.Add(Views);

        Tabs.Picked += (name, isTool) => { if (isTool) PickTool(name); else Execute(name); };
        Numeric.ApplyRequested += tool => session.Run(tool);
        session.DocumentReplaced += () => { Numeric.Show(null); Views.FitAll(); };
        session.Changed += () => { if (session.ActiveTool != null && Numeric.Tool != session.ActiveTool) Numeric.Show(session.ActiveTool); };
        Focusable = true;
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Views.FitAll, Avalonia.Threading.DispatcherPriority.Background);
        RefreshRecent();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ───────────────────────────── tools and commands ─────────────────────────────

    /// <summary>Picks a tool by name: arms it, runs it, or (for a requester) shows its numbers to fill in and apply.</summary>
    public void PickTool(string name)
    {
        if (!Session.Registry.Has(name)) return;
        var tool = Session.Registry.Create(name);
        if (tool.Kind == ToolKind.Requester)
        {
            Session.CommitTool();
            Numeric.Show(tool);
            Session.SetStatus($"{tool.Name}: set its numbers, then Apply.");
            return;
        }
        Session.Activate(tool);
        Views.ToolArmed();
        Numeric.Show(tool.Kind == ToolKind.Interactive ? tool : null);
    }

    public void Execute(string command)
    {
        if (_commands.TryGetValue(command, out var action)) action();
        else if (Session.Registry.Has(command)) PickTool(command);
    }

    public IReadOnlyCollection<string> CommandNames => _commands.Keys;

    private Dictionary<string, Action> BuildCommands()
    {
        var s = Session;
        Document D() => s.Document;
        return new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
        {
            ["New"] = () => _ = NewDocument(),
            ["Open"] = () => _ = OpenDocument(),
            ["Save"] = () => _ = SaveDocument(false),
            ["Save As"] = () => _ = SaveDocument(true),
            ["Export OBJ"] = () => _ = Export(".obj"),
            ["Export GEO"] = () => _ = Export(".geo"),
            ["Export Plugin Format"] = () => _ = ExportPlugin(),
            ["Undo"] = () => { s.CancelTool(); D().UndoLast(); },
            ["Redo"] = () => { s.CancelTool(); D().RedoLast(); },
            ["Cut"] = () => { s.CommitTool(); LayerOps.Cut(D()); },
            ["Copy"] = () => { s.CommitTool(); LayerOps.Copy(D()); s.SetStatus("Copied."); },
            ["Paste"] = () => { s.CommitTool(); LayerOps.Paste(D()); },
            ["Swap Layers"] = () => { s.CommitTool(); LayerOps.SwapForegroundBackground(D()); },
            ["Select All"] = () => SelectionOps.All(D()),
            ["Deselect"] = () => SelectionOps.None(D()),
            ["Invert Selection"] = () => SelectionOps.Invert(D()),
            ["Select Connected"] = () => SelectionOps.Connected(D()),
            ["Grow Selection"] = () => SelectionOps.Grow(D()),
            ["Shrink Selection"] = () => SelectionOps.Shrink(D()),
            ["Cycle Selection Mode"] = s.CycleSelectionMode,
            ["Commit"] = () => { if (s.ActiveTool != null) s.CommitTool(); else if (D().Selection.Mode == SelectionMode.Points && D().Selection.Any) PickTool("Make Polygon"); },
            ["Cancel"] = () => { if (s.ActiveTool != null) s.CancelTool(); else SelectionOps.None(D()); },
            ["Fit All"] = Views.FitAll,
            ["Fit Selected"] = Views.FitSelected,
            ["Maximize View"] = () => Views.ToggleMaximize(Views.Focused),
            ["Zoom In"] = () => Zoom(1.5),
            ["Zoom Out"] = () => Zoom(1 / 1.5),
            ["MetaNURBS"] = s.ToggleMetaNurbs,
            ["Style Wireframe"] = () => SetStyle(DisplayStyle.Wireframe),
            ["Style Shaded"] = () => SetStyle(DisplayStyle.Shaded),
            ["Style ShadedWireframe"] = () => SetStyle(DisplayStyle.ShadedWireframe),
            ["Backface Tint"] = () => { s.Preferences.BackfaceTint = !s.Preferences.BackfaceTint; D().Notify(); },
            ["Snap"] = ToggleSnap,
            ["Center Selection"] = () => SetCenter(ActionCenter.Selection),
            ["Center Mouse"] = () => SetCenter(ActionCenter.Mouse),
            ["Center Origin"] = () => SetCenter(ActionCenter.Origin),
            ["Surface Editor"] = () => { var w = new SurfaceEditor(s); if (Owner != null) w.Show(Owner); else w.Show(); },
            ["Preferences"] = () => { var w = new PreferencesWindow(s, ApplyPreferences); if (Owner != null) w.ShowDialog(Owner); else w.Show(); },
            ["Run Macro"] = () => _ = RunMacro(),
            ["Macro Reference"] = () => _ = Dialogs.Message(Owner, "Macros", MacroReference()),
            ["Load Plugin"] = () => _ = LoadPlugin(),
            ["Keyboard"] = () => _ = Dialogs.Message(Owner, "Keyboard", KeyboardReference()),
            ["Show Layout"] = () => ShowLayout?.Invoke(),
            ["About"] = () => _ = Dialogs.Message(Owner, "About AVA Modeler", About),
        };
    }

    public const string About = """
        AVA Modeler: a recreation of the classic standalone 3D modeler of the mid-1990s, inspired by classic LightWave Modeler
        (versions 4 and 5), with a Layout workspace for animating and rendering.

        © 2026 Lonnie Watson, MIT License. LightWave is a trademark of its owner; AVA Modeler is an independent project, not
        affiliated with or endorsed by it. It reads and writes the classic object formats for compatibility.
        """;

    private void Zoom(double factor)
    {
        var v = Views.Focused;
        v.Camera.Zoom(factor, v.Bounds.Width / 2, v.Bounds.Height / 2);
        v.Redraw();
    }

    private void SetStyle(DisplayStyle style)
    {
        Session.Preferences.PerspectiveStyle = style;
        Session.Document.Notify();
    }

    private void ToggleSnap()
    {
        Session.Preferences.SnapToGrid = !Session.Preferences.SnapToGrid;
        UpdateSnap();
        Session.SetStatus(Session.Preferences.SnapToGrid ? "Snapping to the grid." : "Snapping off.");
    }

    /// <summary>Snaps to the grid step of the front view (the views share their zoom closely enough).</summary>
    public void UpdateSnap() => Session.Context.Snap = Session.Preferences.SnapToGrid ? Views.Views[1].Camera.GridStep() : 0;

    private void SetCenter(ActionCenter mode)
    {
        Session.Preferences.ActionCenter = mode;
        Session.Context.CenterMode = mode;
        Session.SetStatus($"Action centre: {mode.ToString().ToLowerInvariant()}.");
    }

    private void ApplyPreferences()
    {
        Views.UseRenderer(Session.Preferences.Renderer == "OpenGL");
        Tabs.ShowTab("Objects");
        Session.Document.Notify();
    }

    // ───────────────────────────── the keyboard ─────────────────────────────

    /// <summary>A key pressed anywhere in the workspace (not in a text box) goes through the key map.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        LayerBar.LastModifiers = e.KeyModifiers;
        if (e.Source is TextBox) return false;
        // The number keys pick layers: alone the foreground, with Alt the background.
        if (e.Key is >= Key.D1 and <= Key.D9 && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            int layer = e.Key - Key.D1;
            Session.CommitTool();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) Session.Document.SetLayers(Session.Document.ForegroundLayers.Where(l => l != layer), [layer]);
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Session.Document.SetLayers(Session.Document.ForegroundLayers.Append(layer), Session.Document.BackgroundLayers.Where(l => l != layer));
            else Session.Document.SetLayers([layer], Session.Document.BackgroundLayers.Where(l => l != layer));
            return true;
        }
        string gesture = Gesture(e);
        var command = Session.Keymap.CommandFor(gesture);
        if (command == null) return false;
        Execute(command);
        return true;
    }

    private static string Gesture(KeyEventArgs e)
    {
        var s = "";
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) s += "Ctrl+";
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) s += "Shift+";
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) s += "Alt+";
        return s + (e.Key == Key.Return ? "Enter" : e.Key == Key.Back ? "Delete" : e.Key.ToString());
    }

    private string KeyboardReference()
    {
        var lines = Session.Keymap.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value,-16} {kv.Key}");
        return "Mouse: click to select (Shift adds, Ctrl removes); drag a box, or right-drag a lasso. With a tool, drag in a view.\n" +
               "Wheel zooms; middle-drag or Alt-drag pans; drag (or Alt-drag) in the perspective view turns it. Double-click maximizes a view.\n" +
               "1–9 put a layer in the foreground (Shift adds; Alt puts it in the background).\n\n" + string.Join("\n", lines) +
               "\n\nChange any key in Edit › Preferences.";
    }

    private static string MacroReference() =>
        "Macros are Lua (5.2) scripts. Layers, points and polygons count from 1.\n\n" +
        string.Join("\n", Macros.Reference.Select(r => $"{r.Signature}\n    {r.Does}")) +
        "\n\nExample:\n  tool(\"Box\", { Size = {2, 1, 1}, SegmentsX = 4 })\n  selmode(\"polygons\"); selectall(); tool(\"Flip\")";

    // ───────────────────────────── files ─────────────────────────────

    private async Task<bool> ConfirmDiscard()
    {
        if (!Session.Document.Modified) return true;
        return await Dialogs.Ask(Owner, "Unsaved changes", "The object has changes that haven't been saved. Throw them away?", "Throw away", "Keep");
    }

    private async Task NewDocument()
    {
        if (!await ConfirmDiscard()) return;
        Session.Replace(new Document());
    }

    private async Task OpenDocument()
    {
        if (!await ConfirmDiscard() || TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Open an object", Dialogs.Objects);
        if (path != null) await Open(path);
    }

    /// <summary>Opens an object file, reporting a failure rather than throwing.</summary>
    public async Task Open(string path)
    {
        try
        {
            var doc = Load(path);
            Session.Replace(doc);
            Session.Preferences.AddRecent(path);
            Session.Preferences.Save();
            RefreshRecent();
            Session.SetStatus($"Opened {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.Message(Owner, "Can't open that", $"{Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>A file through the built-in formats, or a plugin's format for its extension.</summary>
    private Document Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var plugin = Session.Registry.Formats.FirstOrDefault(f => f.CanLoad && f.Extensions.Contains(ext));
        return plugin != null && !ObjectFiles.LoadExtensions.Contains(ext) ? plugin.Load(path) : ObjectFiles.Load(path);
    }

    private async Task SaveDocument(bool askName)
    {
        Session.CommitTool();
        var doc = Session.Document;
        string? path = doc.FilePath;
        if (askName || path == null || !path.EndsWith(".lwo", StringComparison.OrdinalIgnoreCase))
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            path = await Dialogs.Save(top, "Save the object", Path.GetFileNameWithoutExtension(path ?? "untitled") + ".lwo", Dialogs.Lwo);
            if (path == null) return;
        }
        try
        {
            ObjectFiles.Save(doc, path);
            doc.FilePath = path;
            doc.Modified = false;
            Session.Preferences.AddRecent(path);
            Session.Preferences.Save();
            RefreshRecent();
            Session.SetStatus($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.Message(Owner, "Can't save", e.Message);
        }
    }

    private async Task Export(string ext)
    {
        Session.CommitTool();
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Save(top, "Export", Path.GetFileNameWithoutExtension(Session.Document.FilePath ?? "untitled") + ext, ext == ".obj" ? Dialogs.ObjFile : Dialogs.Geo);
        if (path == null) return;
        try
        {
            ObjectFiles.Save(Session.Document, path);
            Session.SetStatus($"Exported {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.Message(Owner, "Can't export", e.Message);
        }
    }

    /// <summary>Saves through a plugin's format (any a plugin added that can save).</summary>
    private async Task ExportPlugin()
    {
        Session.CommitTool();
        var formats = Session.Registry.Formats.Where(f => f.CanSave).ToList();
        if (formats.Count == 0)
        {
            await Dialogs.Message(Owner, "No plugin formats", "No plugin that saves a file format is loaded. Load one from Macros › Load Plugin, or put it in " + Preferences.PluginsFolder + ".");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var types = formats.Select(f => new Avalonia.Platform.Storage.FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*" + e).ToList() }).ToArray();
        var path = await Dialogs.Save(top, "Export", Path.GetFileNameWithoutExtension(Session.Document.FilePath ?? "untitled") + formats[0].Extensions[0], types);
        if (path == null) return;
        var format = formats.FirstOrDefault(f => f.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) ?? formats[0];
        try
        {
            format.Save(Session.Document, path);
            Session.SetStatus($"Exported {Path.GetFileName(path)} ({format.Name}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await Dialogs.Message(Owner, "Can't export", e.Message);
        }
    }

    private void RefreshRecent()
    {
        var items = Session.Preferences.RecentFiles.Select(p =>
        {
            var item = new MenuItem { Header = Path.GetFileName(p) };
            ToolTip.SetTip(item, p);
            item.Click += async (_, _) => { if (await ConfirmDiscard()) await Open(p); };
            return item;
        }).ToList();
        _recent.ItemsSource = items.Count > 0 ? items : [new MenuItem { Header = "(none yet)", IsEnabled = false }];
    }

    private async Task RunMacro()
    {
        Session.CommitTool();
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Run a macro", Dialogs.Lua);
        if (path == null) return;
        await RunMacroFile(path);
    }

    public async Task RunMacroFile(string path)
    {
        var cmd = Session.Commands;
        cmd.Output.Clear();
        try
        {
            Macros.Run(await File.ReadAllTextAsync(path), cmd);
            Session.SetStatus($"Ran {Path.GetFileName(path)}." + (cmd.Output.Count > 0 ? " " + cmd.Output[^1] : ""));
            if (cmd.Output.Count > 1) await Dialogs.Message(Owner, Path.GetFileName(path), string.Join("\n", cmd.Output));
        }
        catch (Exception e) when (e is MacroException or IOException)
        {
            await Dialogs.Message(Owner, "The macro stopped", e.Message);
        }
    }

    private async Task LoadPlugin()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Load a plugin", Dialogs.Dll);
        if (path == null) return;
        try
        {
            string name = Session.Registry.LoadPlugin(path);
            Tabs.ShowTab("Objects");
            Session.SetStatus($"Loaded the {name} plugin. Copy it to {Preferences.PluginsFolder} to load it every time.");
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException or TypeLoadException or InvalidOperationException or MissingMethodException)
        {
            await Dialogs.Message(Owner, "Can't load that plugin", e.Message);
        }
    }

    // ───────────────────────────── the menus ─────────────────────────────

    private Menu BuildMenu()
    {
        MenuItem Item(string header, string command)
        {
            string key = Session.Keymap.Bindings.TryGetValue(command, out var k) ? k : "";
            var item = new MenuItem { Header = header, InputGesture = key != "" && TryGesture(key) is { } g ? g : null };
            item.Click += (_, _) => Execute(command);
            return item;
        }
        MenuItem Group(string header, params object[] items)
        {
            var g = new MenuItem { Header = header };
            foreach (var i in items) g.Items.Add(i is string ? new Separator() : i);
            return g;
        }
        return new Menu
        {
            Background = Look.PanelDark,
            Foreground = Look.Text,
            ItemsSource = new[]
            {
                Group("_File", Item("_New", "New"), Item("_Open…", "Open"), _recent, "-", Item("_Save", "Save"), Item("Save _As…", "Save As"),
                    Item("Export _OBJ…", "Export OBJ"), Item("Export _GEO…", "Export GEO"), Item("Export with a _Plugin…", "Export Plugin Format")),
                Group("_Edit", Item("_Undo", "Undo"), Item("_Redo", "Redo"), "-", Item("Cu_t", "Cut"), Item("_Copy", "Copy"), Item("_Paste", "Paste"), "-",
                    Item("Select _All", "Select All"), Item("_Deselect", "Deselect"), Item("_Invert", "Invert Selection"), Item("Select C_onnected", "Select Connected"),
                    Item("_Grow", "Grow Selection"), Item("S_hrink", "Shrink Selection"), "-", Item("Surfaces…", "Surface Editor"), Item("Pr_eferences…", "Preferences")),
                Group("_View", Item("Fit _All", "Fit All"), Item("Fit _Selected", "Fit Selected"), Item("_Maximize", "Maximize View"), "-",
                    Item("_Wireframe", "Style Wireframe"), Item("S_haded", "Style Shaded"), Item("Shaded + Wire", "Style ShadedWireframe"), "-",
                    Item("Meta_NURBS", "MetaNURBS"), Item("_Backface Tint", "Backface Tint"), Item("S_nap to Grid", "Snap"), "-", Item("_Layout", "Show Layout")),
                Group("_Macros", Item("_Run Macro…", "Run Macro"), Item("Macro _Reference", "Macro Reference"), Item("Load _Plugin…", "Load Plugin")),
                Group("_Help", Item("_Keyboard", "Keyboard"), Item("_About", "About")),
            },
        };
    }

    private static KeyGesture? TryGesture(string key)
    {
        try { return KeyGesture.Parse(key); }
        catch (ArgumentException) { return null; }
    }
}
