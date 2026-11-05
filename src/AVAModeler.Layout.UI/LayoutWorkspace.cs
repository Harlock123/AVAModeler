using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// The Layout workspace: the scene's items and the chosen one's settings on the left, the view in the middle, and
/// the timeline (with play, keys and the chosen item's key marks) and the graph editor or the dope sheet below.
/// </summary>
public sealed class LayoutWorkspace : DockPanel
{
    public LayoutSession Session { get; }
    public SceneView View { get; }
    public GraphEditor Graph { get; }
    /// <summary>The dope sheet (D, View › Dope Sheet), shown in the graph editor's place.</summary>
    public DopeSheetPanel DopeSheet { get; }
    /// <summary>The chosen item's settings, on the left under the item list.</summary>
    public ItemPropertiesPanel Properties { get; }
    private readonly Func<Document> _modelerDocument;
    private readonly ListBox _items = new() { Height = 220 };
    private readonly Slider _timeline = new() { Minimum = 1, Maximum = 60, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _frameText = new() { Foreground = Look.Text, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Look.ButtonOn, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly KeyMarks _marks;
    private readonly DispatcherTimer _player = new();
    private readonly MenuItem _undo = new(), _redo = new();
    private bool _refreshing;

    /// <summary>Raised to switch to the Modeler workspace.</summary>
    public event Action? ShowModeler;

    /// <summary>Raised to show or hide the AI Chat panel (AI › Chat, F4).</summary>
    public event Action? ToggleChat;

    public LayoutWorkspace(LayoutSession session, Func<Document> modelerDocument)
    {
        Session = session;
        _modelerDocument = modelerDocument;
        Background = Look.Panel;
        View = new SceneView(session);
        Graph = new GraphEditor(session) { Height = 200 };
        DopeSheet = new DopeSheetPanel(session) { Height = 200, IsVisible = false };
        _marks = new KeyMarks(session) { Height = 14 };
        Properties = new ItemPropertiesPanel(session);
        Properties.ItemsChanged += RefreshItems;

        var header = new StackPanel { Background = Look.PanelDark, Children = { BuildMenu(), Toolbar() } };
        SetDock(header, Dock.Top);
        Children.Add(header);

        var left = new StackPanel { Width = 240, Margin = new Thickness(6), Spacing = 6 };
        left.Children.Add(new TextBlock { Text = "Items", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        left.Children.Add(_items);
        left.Children.Add(Properties);
        var leftScroll = new ScrollViewer { Content = left };
        SetDock(leftScroll, Dock.Left);
        Children.Add(leftScroll);

        var timeline = new DockPanel { Margin = new Thickness(6, 2) };
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        transport.Children.Add(Look.MakeButton("|◀", () => Session.SetFrame(Session.Scene.FirstFrame), "First frame"));
        transport.Children.Add(Look.MakeButton("◀", () => Session.SetFrame(Session.Frame - 1), "Back a frame"));
        transport.Children.Add(Look.MakeButton("Play", TogglePlay, "Play / stop"));
        transport.Children.Add(Look.MakeButton("▶", () => Session.SetFrame(Session.Frame + 1), "On a frame"));
        transport.Children.Add(Look.MakeButton("▶|", () => Session.SetFrame(Session.Scene.LastFrame), "Last frame"));
        transport.Children.Add(Look.MakeButton("Create Key", Session.CreateKey, "Key every channel of the chosen item at this frame"));
        transport.Children.Add(Look.MakeButton("Delete Key", Session.DeleteKey, "Remove the chosen item's keys at this frame"));
        var auto = new CheckBox { Content = "Auto key", IsChecked = Session.AutoKey, Foreground = Look.Text, Margin = new Thickness(6, 0) };
        auto.IsCheckedChanged += (_, _) => Session.AutoKey = auto.IsChecked == true;
        transport.Children.Add(auto);
        SetDock(transport, Dock.Right);
        SetDock(_frameText, Dock.Left);
        timeline.Children.Add(transport);
        timeline.Children.Add(_frameText);
        timeline.Children.Add(new StackPanel { Children = { _timeline, _marks } });
        var bottom = new StackPanel { Background = Look.PanelDark, Children = { timeline, Graph, DopeSheet, _status } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        Children.Add(View);

        _items.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (_items.SelectedIndex >= 0 && _items.SelectedIndex < Session.Scene.Items.Count) Session.Select(Session.Scene.Items[_items.SelectedIndex]);
        };
        _timeline.ValueChanged += (_, _) => { if (!_refreshing) Session.SetFrame(Math.Round(_timeline.Value)); };
        _player.Tick += (_, _) =>
        {
            var s = Session.Scene;
            Session.SetFrame(Session.Frame + 1 > s.LastFrame ? s.FirstFrame : Session.Frame + 1);
        };
        session.Changed += Refresh;
        session.History.Changed += UpdateUndoItems;
        session.SceneReplaced += () => { RefreshItems(); View.Fit(); };
        session.ChangedElsewhere += () =>
        {
            RefreshItems();
            // Not while the person is typing in a setting: that would throw their typing away.
            if (!Properties.IsKeyboardFocusWithin) Properties.Show();
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(View.Fit, DispatcherPriority.Background);
        RefreshItems();
        Refresh();
        UpdateUndoItems();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>Edit › Undo, which names the step it would take back ("Undo Move Camera").</summary>
    public MenuItem UndoItem => _undo;
    /// <summary>Edit › Redo, which names the step it would do again.</summary>
    public MenuItem RedoItem => _redo;

    // ───────────────────────────── undo and the keyboard ─────────────────────────────

    /// <summary>Undoes the last step to the scene (Ctrl+Z, Edit › Undo): the person's or an agent's.</summary>
    public void Undo() => AfterStep(Session.Undo());

    /// <summary>Redoes the last step undone (Ctrl+Shift+Z or Ctrl+Y, Edit › Redo).</summary>
    public void Redo() => AfterStep(Session.Redo());

    private void AfterStep(string? label)
    {
        if (label == null) return;
        // Names, parents and settings may all have gone back: show them afresh.
        RefreshItems();
        Properties.Show();
    }

    private void UpdateUndoItems()
    {
        var h = Session.History;
        // A step's name is shown as it is: underscores aren't access keys.
        _undo.Header = h.UndoLabel is { } u ? "_Undo " + u.Replace("_", "__") : "_Undo";
        _redo.Header = h.RedoLabel is { } r ? "_Redo " + r.Replace("_", "__") : "_Redo";
        _undo.IsEnabled = h.CanUndo;
        _redo.IsEnabled = h.CanRedo;
    }

    /// <summary>Whether the dope sheet is showing (in the graph editor's place).</summary>
    public bool DopeSheetShown
    {
        get => DopeSheet.IsVisible;
        set
        {
            DopeSheet.IsVisible = value;
            Graph.IsVisible = !value;
            if (value)
            {
                DopeSheet.Sheet.Prune();
                DopeSheet.View.InvalidateMeasure();
                DopeSheet.View.InvalidateVisual();
            }
        }
    }

    /// <summary>Shows the dope sheet, or the graph editor again (D, View › Dope Sheet).</summary>
    public void ToggleDopeSheet() => DopeSheetShown = !DopeSheetShown;

    /// <summary>
    /// A key pressed anywhere in Layout (not in a text box, which keeps its own Ctrl+Z): F9 renders, D shows the dope
    /// sheet (whose own keys work while it has the focus), Ctrl+Z undoes the scene, Ctrl+Shift+Z and Ctrl+Y redo.
    /// Returns whether it was used.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.F9) { Render(); return true; }
        if (e.Key == Key.F10) { RenderAnimation(); return true; }
        if (e.Source is TextBox) return false;
        if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.None) { ToggleDopeSheet(); return true; }
        if (DopeSheetShown && DopeSheet.IsKeyboardFocusWithin && DopeSheet.HandleKey(e)) return true;
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Alt)) return false;
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) { Undo(); return true; }
        if (e.Key == Key.Z && shift || e.Key == Key.Y && !shift) { Redo(); return true; }
        return false;
    }

    /// <summary>The item list as shown (kind and name, indented under parents).</summary>
    public IReadOnlyList<string> ListedItems => _items.ItemsSource as IReadOnlyList<string> ?? [];

    private Control Toolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        bar.Children.Add(Look.MakeButton("Modeler", () => ShowModeler?.Invoke(), "Back to modeling"));
        bar.Children.Add(new TextBlock { Text = "View", Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        var mode = new ComboBox { ItemsSource = Enum.GetNames<LayoutViewMode>(), SelectedItem = View.Mode.ToString(), Width = 130 };
        mode.SelectionChanged += (_, _) => { if (Enum.TryParse<LayoutViewMode>(mode.SelectedItem as string, out var m)) View.Mode = m; };
        View.ModeChanged += () => mode.SelectedItem = View.Mode.ToString();
        bar.Children.Add(mode);
        var shaded = new CheckBox { Content = "Shaded", IsChecked = View.Shaded, Foreground = Look.Text, Margin = new Thickness(8, 0) };
        shaded.IsCheckedChanged += (_, _) => { View.Shaded = shaded.IsChecked == true; View.InvalidateVisual(); };
        bar.Children.Add(shaded);
        bar.Children.Add(Look.MakeButton("Fit", View.Fit, "Frame the scene"));
        bar.Children.Add(Look.MakeButton("Render Frame", Render, "Ray-trace this frame (F9)"));
        return bar;
    }

    private void TogglePlay()
    {
        if (_player.IsEnabled) { _player.Stop(); return; }
        _player.Interval = TimeSpan.FromSeconds(1 / Math.Max(1, Session.Scene.FramesPerSecond));
        _player.Start();
    }

    public void Render()
    {
        _player.Stop();
        var w = new RenderWindow(Session);
        if (Owner != null) w.Show(Owner); else w.Show();
    }

    private AnimationWindow? _animation;

    /// <summary>Render › Render Animation (F10): the animation window, or the one already open brought forward.</summary>
    public AnimationWindow RenderAnimation()
    {
        _player.Stop();
        if (_animation is { IsVisible: true })
        {
            _animation.Activate();
            return _animation;
        }
        var w = _animation = new AnimationWindow(Session);
        w.Closed += (_, _) => { if (_animation == w) _animation = null; };
        if (Owner != null) w.Show(Owner); else w.Show();
        return w;
    }

    // ───────────────────────────── the item list and properties ─────────────────────────────

    private void RefreshItems()
    {
        _refreshing = true;
        _items.ItemsSource = Session.Scene.Items.Select(i => $"{Indent(i)}{i.Kind}: {i.Name}").ToList();
        _items.SelectedIndex = Session.Selected == null ? -1 : Session.Scene.Items.ToList().IndexOf(Session.Selected);
        _refreshing = false;
    }

    private static string Indent(SceneItem i) => new(' ', i.Ancestors().Count() * 2);

    private int _itemsVersion = -1;

    private void Refresh()
    {
        _refreshing = true;
        var s = Session.Scene;
        _timeline.Minimum = s.FirstFrame;
        _timeline.Maximum = Math.Max(s.FirstFrame + 1, s.LastFrame);
        _timeline.Value = Session.Frame;
        _frameText.Text = $"Frame {Session.Frame:0}";
        _status.Text = Session.Status;
        _refreshing = false;
        if (s.Items.Count != _itemsVersion) { _itemsVersion = s.Items.Count; RefreshItems(); }
        else
        {
            _refreshing = true;
            _items.SelectedIndex = Session.Selected == null ? -1 : s.Items.ToList().IndexOf(Session.Selected);
            _refreshing = false;
        }
        // Rebuild the property fields only when the item or frame changes (not on every keystroke's notify).
        if (Session.Selected != Properties.ShownItem || Session.Frame != Properties.ShownFrame) Properties.Show();
        _marks.InvalidateVisual();
        View.InvalidateVisual();
    }

    // ───────────────────────────── menus ─────────────────────────────

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action action)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => action();
            return i;
        }
        MenuItem Group(string header, params MenuItem[] items) => new() { Header = header, ItemsSource = items };
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        _undo.InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control);
        _redo.InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        return new Menu
        {
            Background = Look.PanelDark,
            Foreground = Look.Text,
            ItemsSource = new[]
            {
                Group("_Scene", Item("_New Scene", NewScene), Item("_Load Scene…", () => _ = LoadScene()), Item("_Save Scene…", () => _ = SaveScene(".avascene")),
                    Item("Save as _LWS…", () => _ = SaveScene(".lws")), Item("Export _glTF…", () => _ = ExportGltf()), Item("Scene _Settings…", () => _ = SceneSettings()),
                    Item("_Quit", () => Owner?.Close())),
                Group("_Edit", _undo, _redo),
                Group("_View", Item("_Graph Editor", () => DopeSheetShown = false), Item("_Dope Sheet  (D)", ToggleDopeSheet)),
                Group("_Add", Item("The _Modeler's Object", () => Session.ShowModelerObject(_modelerDocument())), Item("_Object from a File…", () => _ = LoadObject()),
                    Item("_Null", () => Session.Add(new NullItem("Null"))),
                    Item("_Distant Light", () => Session.Add(new LightItem("Distant Light"))),
                    Item("_Point Light", () => Session.Add(new LightItem("Point Light", LightType.Point))),
                    Item("_Spot Light", () => Session.Add(new LightItem("Spot Light", LightType.Spot))),
                    Item("_Area Light", () => Session.Add(new LightItem("Area Light", LightType.Area))),
                    Item("_Camera", () => Session.Add(new CameraItem("Camera")))),
                Group("_Render", Item("_Render Frame", Render), Item("Render _Animation…", () => RenderAnimation())),
                Group("_AI", Item("_Chat with Claude…  (F4)", () => ToggleChat?.Invoke())),
                Group("_Help", Item("_Layout", () => _ = Dialogs.Message(Owner, "Layout", Help))),
            },
        };
    }

    public const string Help = """
        Layout animates and renders. Add the object you're modeling (Add › The Modeler's Object): it stays live, so edits in
        the Modeler show here at once. Add lights, cameras, nulls and bones; parent items to one another in their settings.

        Choose an item by clicking it in the view or the list. Drag it in the view to move it, right-drag to turn it; with Auto
        key on, each change is a key at the current frame. Create Key keys every channel at once.

        Ctrl+Z (Edit › Undo) takes back the last change to the scene, an AI agent's included; Ctrl+Shift+Z or Ctrl+Y redoes
        it. A drag, a value typed and entered, or a key made is one step. The object's geometry has the Modeler's own undo.

        A bone's settings have inverse kinematics: choose a Goal (a null, say) for the bone at the end of a chain and, with
        Full-time IK, the chain turns at every frame so its tip reaches the goal. Limits and locks keep each bone's heading,
        pitch and bank in bounds; an Anchor stops chains below it. Key IK here keys the pose at this frame; Bake IK keys every
        frame and turns full-time IK off.

        Any item can follow a path: in its settings, choose the items the path runs through (nulls you've placed, in the item
        list's order) and tick Follow a path. Its position along the path (0 to 1) is a channel you key like any other, or
        Even speed keys it once round from the first frame to the last; Align to path turns it to face along the path, and
        Banking leans it into turns. Bake path turns the motion into ordinary keys.

        The dope sheet (D, or View › Dope Sheet) lists every item (and with Channels, each keyed channel) against the frames,
        each key a diamond. Click a key, Shift-click to add more, or drag a box round them; drag them sideways to move them in
        time, snapped to frames. Delete removes them, Ctrl+C and Ctrl+V copy them and paste them at the current frame, the
        arrow keys nudge them a frame, and Scale × stretches their timing. Each is one undo step.

        The graph editor shows the chosen item's channels as curves; click a key to set its value, tension, continuity and
        bias (TCB, as classic LightWave), and what happens before the first key and after the last.

        Render › Render Frame (F9) ray-traces the frame through the current camera. Scene › Scene Settings has ambient occlusion
        and an environment picture (seen behind everything and in reflections, and optionally lighting the scene); a camera's
        settings have depth of field (Focus on brings an item into focus) and motion blur; area lights, and point and spot
        lights with a radius, cast soft shadows. All are off until switched on, and each takes render time. Render › Render Animation (F10) renders
        a run of frames into numbered PNGs, and joins them into an MP4 or a looping GIF if ffmpeg is installed. Scenes save as .avascene (everything) or as
        LightWave .lws (for other programs); .lws scenes from LightWave 4 and 5 (and later) load.
        """;

    private void NewScene()
    {
        Session.NewScene(_modelerDocument());
    }

    private async Task LoadScene()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Load a scene", new FilePickerFileType("Scenes") { Patterns = ["*.lws", "*" + SceneJson.Extension] });
        if (path == null) return;
        try
        {
            Session.LoadScene(path);
            if (Session.Warnings.Count > 0) await Dialogs.Message(Owner, "Loaded with warnings", string.Join("\n", Session.Warnings));
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.Message(Owner, "Can't load that scene", e.Message);
        }
    }

    private async Task SaveScene(string ext)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var type = ext == ".lws" ? new FilePickerFileType("LightWave scene") { Patterns = ["*.lws"] } : new FilePickerFileType("AVA scene") { Patterns = ["*" + SceneJson.Extension] };
        var path = await Dialogs.Save(top, "Save the scene", "scene" + ext, type);
        if (path == null) return;
        try
        {
            Session.SaveScene(path);
            if (Session.Warnings.Count > 0) await Dialogs.Message(Owner, "Saved with warnings", string.Join("\n", Session.Warnings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Owner, "Can't save", e.Message);
        }
    }

    /// <summary>Exports the scene as glTF 2.0 (.glb or .gltf, as the name chosen ends), with its hierarchy, cameras, lights and motion.</summary>
    private async Task ExportGltf()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Save(top, "Export the scene as glTF", Path.GetFileNameWithoutExtension(Session.ScenePath ?? "scene") + ".glb", Dialogs.Glb, Dialogs.GltfFile);
        if (path == null) return;
        try
        {
            var warnings = SceneGltf.Save(Session.Scene, path);
            Session.SetStatus($"Exported {Path.GetFileName(path)}.");
            if (warnings.Count > 0) await Dialogs.Message(Owner, "Exported with warnings", string.Join("\n", warnings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await Dialogs.Message(Owner, "Can't export", e.Message);
        }
    }

    private async Task LoadObject()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Load an object", Dialogs.Objects);
        if (path == null) return;
        try { Session.LoadObject(path); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Owner, "Can't load that object", e.Message);
        }
    }

    /// <summary>Scene › Scene Settings…: the frames, ambient light, backdrop and the scene's render effects, in a dialog.</summary>
    private async Task SceneSettings()
    {
        var panel = new SceneSettingsPanel(Session);
        var w = new Window
        {
            Title = "Scene settings", Width = 380, SizeToContent = SizeToContent.Height, Background = Look.Panel, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        panel.Applied += w.Close;
        if (Owner != null) await Dialogs.ShowModal(w, Owner);
    }

    /// <summary>The dope sheet's strip: a mark at every frame the chosen item has a key.</summary>
    private sealed class KeyMarks(LayoutSession session) : Control
    {
        public override void Render(DrawingContext dc)
        {
            var s = session.Scene;
            double first = s.FirstFrame, last = Math.Max(s.LastFrame, s.FirstFrame + 1);
            dc.FillRectangle(new SolidColorBrush(Color.FromRgb(50, 50, 54)), new Rect(Bounds.Size));
            if (session.Selected is not { } item) return;
            // The slider's thumb travels inside the track, so the marks are inset to line up with it.
            double inset = 10, width = Math.Max(1, Bounds.Width - 2 * inset);
            foreach (double f in item.KeyFrames())
            {
                if (f < first || f > last) continue;
                double x = inset + (f - first) / (last - first) * width;
                dc.FillRectangle(f == session.Frame ? Brushes.White : Brushes.Gold, new Rect(x - 2, 2, 4, Bounds.Height - 4));
            }
        }
    }
}
