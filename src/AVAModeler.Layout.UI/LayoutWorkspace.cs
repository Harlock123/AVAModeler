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
/// the timeline (with play, keys and the dope sheet's key marks) and graph editor below.
/// </summary>
public sealed class LayoutWorkspace : DockPanel
{
    public LayoutSession Session { get; }
    public SceneView View { get; }
    public GraphEditor Graph { get; }
    private readonly Func<Document> _modelerDocument;
    private readonly ListBox _items = new() { Height = 220 };
    private readonly StackPanel _properties = new() { Spacing = 3 };
    private readonly Slider _timeline = new() { Minimum = 1, Maximum = 60, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _frameText = new() { Foreground = Look.Text, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Look.ButtonOn, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly KeyMarks _marks;
    private readonly DispatcherTimer _player = new();
    private readonly MenuItem _undo = new(), _redo = new();
    private bool _refreshing;

    /// <summary>Raised to switch to the Modeler workspace.</summary>
    public event Action? ShowModeler;

    public LayoutWorkspace(LayoutSession session, Func<Document> modelerDocument)
    {
        Session = session;
        _modelerDocument = modelerDocument;
        Background = Look.Panel;
        View = new SceneView(session);
        Graph = new GraphEditor(session) { Height = 200 };
        _marks = new KeyMarks(session) { Height = 14 };

        var header = new StackPanel { Background = Look.PanelDark, Children = { BuildMenu(), Toolbar() } };
        SetDock(header, Dock.Top);
        Children.Add(header);

        var left = new StackPanel { Width = 240, Margin = new Thickness(6), Spacing = 6 };
        left.Children.Add(new TextBlock { Text = "Items", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        left.Children.Add(_items);
        left.Children.Add(_properties);
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
        var bottom = new StackPanel { Background = Look.PanelDark, Children = { timeline, Graph, _status } };
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
            if (!_properties.IsKeyboardFocusWithin) ShowProperties();
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
        ShowProperties();
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

    /// <summary>
    /// A key pressed anywhere in Layout (not in a text box, which keeps its own Ctrl+Z): F9 renders, Ctrl+Z undoes the
    /// scene, Ctrl+Shift+Z and Ctrl+Y redo. Returns whether it was used.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.F9) { Render(); return true; }
        if (e.Source is TextBox) return false;
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Alt)) return false;
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) { Undo(); return true; }
        if (e.Key == Key.Z && shift || e.Key == Key.Y && !shift) { Redo(); return true; }
        return false;
    }

    /// <summary>Makes a change from Layout's controls one undo step.</summary>
    private void Change(string label, Action change) => Session.Edit(label, change);

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
    private SceneItem? _shownItem;
    private double _shownFrame = double.NaN;

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
        if (Session.Selected != _shownItem || Session.Frame != _shownFrame) ShowProperties();
        _marks.InvalidateVisual();
        View.InvalidateVisual();
    }

    private void ShowProperties()
    {
        _shownItem = Session.Selected;
        _shownFrame = Session.Frame;
        _properties.Children.Clear();
        if (Session.Selected is not { } item) return;
        _properties.Children.Add(new TextBlock { Text = $"{item.Kind}: {item.Name}", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        var name = new TextBox { Text = item.Name };
        void Rename()
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == item.Name) return;
            Change($"Rename {item.Name}", () => { item.Name = name.Text; Session.Scene.Notify(); });
            RefreshItems();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(); };
        _properties.Children.Add(name);
        Vector3Row("Position", item.PositionAt(Session.Frame), v => Session.SetVector(item, 0, v));
        Vector3Row("Rotation (H P B)", item.RotationAt(Session.Frame), v => Session.SetVector(item, 3, v));
        Vector3Row("Scale", item.ScaleAt(Session.Frame), v => Session.SetVector(item, 6, v));
        Vector3Row("Pivot", item.Pivot, v => Change($"Move {item.Name}'s Pivot", () => { item.Pivot = v; Session.Scene.Notify(); }));

        var parents = new List<SceneItem?> { null };
        parents.AddRange(Session.Scene.Items.Where(i => i != item && !i.Ancestors().Contains(item)));
        var parent = new ComboBox { ItemsSource = parents.Select(p => p == null ? "(none)" : $"{p.Kind}: {p.Name}").ToList(), SelectedIndex = parents.IndexOf(item.Parent), HorizontalAlignment = HorizontalAlignment.Stretch };
        parent.SelectionChanged += (_, _) =>
        {
            if (parent.SelectedIndex < 0) return;
            try { Change($"Parent {item.Name}", () => item.Parent = parents[parent.SelectedIndex]); RefreshItems(); }
            catch (InvalidOperationException e) { Session.SetStatus(e.Message); }
        };
        _properties.Children.Add(Label("Parent"));
        _properties.Children.Add(parent);

        switch (item)
        {
            case LightItem light:
                _properties.Children.Add(Label("Light type"));
                var type = new ComboBox { ItemsSource = Enum.GetNames<LightType>(), SelectedItem = light.Type.ToString() };
                type.SelectionChanged += (_, _) => { if (Enum.TryParse<LightType>(type.SelectedItem as string, out var t)) Change("Light Type", () => { light.Type = t; Session.Scene.Notify(); }); };
                _properties.Children.Add(type);
                // 0 to 255 like surface colours (and LightWave's light panel); kept as 0 to 1.
                Vector3Row("Colour (0–255)", light.Color * 255, v => Change("Light Colour", () => { light.Color = Vec3.Min(Vec3.Max(v / 255, Vec3.Zero), Vec3.One); Session.Scene.Notify(); }));
                NumberRow("Intensity", light.Intensity.Evaluate(Session.Frame), v => Change("Light Intensity", () => { light.Intensity.AddKey(Session.Frame, v); Session.Scene.Notify(); }));
                NumberRow("Cone angle °", light.ConeAngle, v => Change("Cone Angle", () => { light.ConeAngle = v; Session.Scene.Notify(); }));
                NumberRow("Soft edge °", light.EdgeAngle, v => Change("Soft Edge", () => { light.EdgeAngle = v; Session.Scene.Notify(); }));
                NumberRow("Range (0: none)", light.Range, v => Change("Light Range", () => { light.Range = v; Session.Scene.Notify(); }));
                var shadows = new CheckBox { Content = "Shadows", IsChecked = light.Shadows, Foreground = Look.Text };
                shadows.IsCheckedChanged += (_, _) => Change("Shadows", () => { light.Shadows = shadows.IsChecked == true; Session.Scene.Notify(); });
                _properties.Children.Add(shadows);
                break;
            case CameraItem cam:
                NumberRow("Zoom factor", cam.Zoom.Evaluate(Session.Frame), v => Change("Zoom", () => { cam.Zoom.AddKey(Session.Frame, Math.Max(0.01, v)); Session.Scene.Notify(); }));
                _properties.Children.Add(new TextBlock { Text = $"{cam.HorizontalFov(Session.Frame):0.#}° across, {cam.VerticalFov(Session.Frame):0.#}° high", Foreground = Look.Text, FontSize = 11 });
                NumberRow("Width (pixels)", cam.Width, v => Change("Camera Width", () => { cam.Width = Math.Max(1, (int)v); Session.Scene.Notify(); }));
                NumberRow("Height (pixels)", cam.Height, v => Change("Camera Height", () => { cam.Height = Math.Max(1, (int)v); Session.Scene.Notify(); }));
                _properties.Children.Add(Look.MakeButton("Use this camera", () => Change("Use Camera", () => Session.Scene.CurrentCamera = cam)));
                break;
            case Bone bone:
                NumberRow("Rest length", bone.RestLength, v => Change("Rest Length", () => { bone.RestLength = Math.Max(1e-3, v); Session.Scene.Notify(); }));
                NumberRow("Strength", bone.Strength, v => Change("Bone Strength", () => { bone.Strength = v; Session.Scene.Notify(); }));
                var active = new CheckBox { Content = "Active", IsChecked = bone.Active, Foreground = Look.Text };
                active.IsCheckedChanged += (_, _) => Change("Bone Active", () => { bone.Active = active.IsChecked == true; Session.Scene.Notify(); });
                _properties.Children.Add(active);
                _properties.Children.Add(Look.MakeButton("Rest here", () => Change("Rest Here", () => { bone.RestFromPose(Session.Frame); Session.Scene.Notify(); }), "Make the current pose the rest pose"));
                break;
            case ObjectItem obj:
                _properties.Children.Add(new TextBlock { Text = obj.IsPlaceholder ? "(couldn't load: a placeholder)" : $"{obj.BaseMesh().Points.Count} points, {obj.BaseMesh().Polygons.Count} polygons", Foreground = Look.Text, FontSize = 11 });
                _properties.Children.Add(Look.MakeButton("Add Bone", () => Session.Select(Session.Edit("Add Bone", () => Session.Scene.AddBone(obj, $"Bone {obj.Bones.Count + 1}")))));
                _properties.Children.Add(Look.MakeButton("Skelegons → Bones", () =>
                {
                    var made = Session.Edit("Skelegons → Bones", () => Skelegons.CreateBones(Session.Scene, obj));
                    Session.SetStatus(made.Count > 0 ? $"Made {made.Count} bones from skelegons." : "No skelegons (2-point polygons on a \"Skelegon…\" surface) in this object.");
                }));
                break;
        }
        _properties.Children.Add(Look.MakeButton("Remove item", () => Session.Remove(item)));
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private void NumberRow(string label, double value, Action<double> set)
    {
        var box = new TextBox { Text = F(value), FontSize = 11 };
        var applied = box.Text;
        // Only a value changed is a change (and an undo step): Enter, then leaving the box, is one.
        void Apply()
        {
            if (box.Text == applied || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
            applied = box.Text;
            set(v);
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        _properties.Children.Add(Label(label));
        _properties.Children.Add(box);
    }

    private void Vector3Row(string label, Vec3 value, Action<Vec3> set)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var boxes = new TextBox[3];
        for (int i = 0; i < 3; i++)
        {
            boxes[i] = new TextBox { Text = F(value[i]), FontSize = 11, Margin = new Thickness(1, 0) };
            Grid.SetColumn(boxes[i], i);
            grid.Children.Add(boxes[i]);
        }
        string Texts() => string.Join(" ", boxes.Select(b => b.Text));
        var applied = Texts();
        void Apply()
        {
            if (Texts() == applied) return;
            var v = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return;
            applied = Texts();
            set(new Vec3(v[0], v[1], v[2]));
        }
        foreach (var b in boxes)
        {
            b.LostFocus += (_, _) => Apply();
            b.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        }
        _properties.Children.Add(Label(label));
        _properties.Children.Add(grid);
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
                    Item("Save as _LWS…", () => _ = SaveScene(".lws")), Item("Scene _Settings…", () => _ = SceneSettings()),
                    Item("_Quit", () => Owner?.Close())),
                Group("_Edit", _undo, _redo),
                Group("_Add", Item("The _Modeler's Object", () => Session.ShowModelerObject(_modelerDocument())), Item("_Object from a File…", () => _ = LoadObject()),
                    Item("_Null", () => Session.Add(new NullItem("Null"))),
                    Item("_Distant Light", () => Session.Add(new LightItem("Distant Light"))),
                    Item("_Point Light", () => Session.Add(new LightItem("Point Light", LightType.Point))),
                    Item("_Spot Light", () => Session.Add(new LightItem("Spot Light", LightType.Spot))),
                    Item("_Camera", () => Session.Add(new CameraItem("Camera")))),
                Group("_Render", Item("_Render Frame", Render)),
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

        The graph editor shows the chosen item's channels as curves; click a key to set its value, tension, continuity and
        bias (TCB, as classic LightWave), and what happens before the first key and after the last.

        Render › Render Frame ray-traces the frame through the current camera. Scenes save as .avascene (everything) or as
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

    private async Task SceneSettings()
    {
        var s = Session.Scene;
        var first = new TextBox { Text = s.FirstFrame.ToString(CultureInfo.InvariantCulture) };
        var last = new TextBox { Text = s.LastFrame.ToString(CultureInfo.InvariantCulture) };
        var fps = new TextBox { Text = F(s.FramesPerSecond) };
        var ambient = new TextBox { Text = F(s.AmbientIntensity) };
        var gradient = new CheckBox { Content = "Gradient backdrop", IsChecked = s.GradientBackground, Foreground = Look.Text };
        var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var w = new Window
        {
            Title = "Scene settings", Width = 320, SizeToContent = SizeToContent.Height, Background = Look.Panel, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 4,
                Children = { Label("First frame"), first, Label("Last frame"), last, Label("Frames per second"), fps, Label("Ambient intensity"), ambient, gradient, ok },
            },
        };
        ok.Click += (_, _) => Change("Scene Settings", () =>
        {
            if (int.TryParse(first.Text, out int f)) s.FirstFrame = f;
            if (int.TryParse(last.Text, out int l)) s.LastFrame = Math.Max(l, s.FirstFrame);
            if (double.TryParse(fps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0) s.FramesPerSecond = r;
            if (double.TryParse(ambient.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) s.AmbientIntensity = Math.Max(0, a);
            s.GradientBackground = gradient.IsChecked == true;
            s.Notify();
            w.Close();
        });
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
