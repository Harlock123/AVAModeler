using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Tools;

namespace AVAModeler.Modeler.UI;

/// <summary>Small windows: a message, a yes/no question, and the file pickers.</summary>
public static class Dialogs
{
    private static Window Frame(string title, Control content, double width = 460) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Panel,
        Content = new Border { Padding = new Thickness(16), Child = content },
    };

    private static int _modals;

    /// <summary>How many modal dialog boxes are open (an AI agent's calls wait while there are any).</summary>
    public static int ModalsOpen => _modals;

    /// <summary>Shows a window as a modal dialog box over its owner, counted in <see cref="ModalsOpen"/>.</summary>
    public static async Task ShowModal(Window window, Window owner)
    {
        _modals++;
        try { await window.ShowDialog(owner); }
        finally { _modals--; }
    }

    public static async Task Message(Window? owner, string title, string text)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Width = 80 };
        var panel = new StackPanel { Spacing = 12, Children = { new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, MaxHeight = 520 }, ok } };
        var w = Frame(title, new ScrollViewer { Content = panel, MaxHeight = 600 });
        ok.Click += (_, _) => w.Close();
        if (owner != null) await ShowModal(w, owner); else w.Show();
    }

    public static async Task<bool> Ask(Window? owner, string title, string text, string yes = "Yes", string no = "No")
    {
        bool answer = false;
        var y = new Button { Content = yes, Width = 110 };
        var n = new Button { Content = no, Width = 110 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { y, n } };
        var w = Frame(title, new StackPanel { Spacing = 12, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text }, buttons } });
        y.Click += (_, _) => { answer = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        if (owner == null) return false;
        await ShowModal(w, owner);
        return answer;
    }

    public static readonly FilePickerFileType Objects = new("Objects") { Patterns = ["*.lwo", "*.lw", "*.geo", "*.obj", "*.glb", "*.gltf", "*.stl", "*.3mf"] };
    public static readonly FilePickerFileType Lwo = new("LightWave object (LWOB)") { Patterns = ["*.lwo"] };
    public static readonly FilePickerFileType ObjFile = new("Wavefront OBJ") { Patterns = ["*.obj"] };
    public static readonly FilePickerFileType Geo = new("VideoScape GEO") { Patterns = ["*.geo"] };
    public static readonly FilePickerFileType Glb = new("glTF 2.0 binary (.glb)") { Patterns = ["*.glb"] };
    public static readonly FilePickerFileType GltfFile = new("glTF 2.0 (.gltf, data inside)") { Patterns = ["*.gltf"] };
    public static readonly FilePickerFileType StlFile = new("STL (3D printing)") { Patterns = ["*.stl"] };
    public static readonly FilePickerFileType ThreeMfFile = new("3MF (3D printing)") { Patterns = ["*.3mf"] };

    /// <summary>The save dialog's choices for exporting as a format, by extension.</summary>
    public static FilePickerFileType[] ExportTypes(string ext) => ext switch
    {
        ".obj" => [ObjFile],
        ".geo" => [Geo],
        ".glb" or ".gltf" => ext == ".glb" ? [Glb, GltfFile] : [GltfFile, Glb],
        ".stl" => [StlFile],
        ".3mf" => [ThreeMfFile],
        _ => [Lwo],
    };
    public static readonly FilePickerFileType Lua = new("Lua macro") { Patterns = ["*.lua"] };
    public static readonly FilePickerFileType BackdropSetup = new("Backdrop setup (JSON)") { Patterns = ["*.json"] };
    public static readonly FilePickerFileType Dll = new("Plugin") { Patterns = ["*.dll"] };
    public static readonly FilePickerFileType Images = new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] };

    public static async Task<string?> Open(TopLevel top, string title, params FilePickerFileType[] types)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = types, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> Save(TopLevel top, string title, string suggested, params FilePickerFileType[] types)
    {
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested, FileTypeChoices = types });
        return file?.TryGetLocalPath();
    }
}

/// <summary>
/// The surface editor: every surface in the object, and how the chosen one looks (colour, diffuse, specular,
/// glossiness, luminosity, transparency, reflection, smoothing). As in LightWave 5, a "T" button beside a setting
/// opens its texture panel (<see cref="TextureEditor"/>), and lights up when the setting has textures. Changes show in
/// the shaded view as they're made, and the whole visit is one undo step. Nodes… opens the surface's node graph
/// (<see cref="NodeEditor"/>).
/// </summary>
public sealed class SurfaceEditor : Window
{
    private readonly ModelerSession _session;
    private readonly ListBox _list = new() { Width = 170 };
    private readonly StackPanel _fields = new() { Spacing = 4, Width = 300 };
    private readonly UndoStack.Snapshot _before;
    private bool _changed, _loading;

    public SurfaceEditor(ModelerSession session)
    {
        _session = session;
        _before = session.Document.Snapshot("Surfaces", []);
        Title = "Surfaces";
        Width = 520;
        Height = 560;
        Background = Look.Panel;
        _list.SelectionChanged += (_, _) => ShowSurface();
        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children = { Docked(_list, Dock.Left), new ScrollViewer { Margin = new Thickness(10, 0, 0, 0), Content = _fields } },
        };
        Refill();
        Closed += (_, _) => { if (_changed) session.Document.Commit(_before); };
    }

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    private void Refill()
    {
        _list.ItemsSource = _session.Document.Surfaces.Select(s => s.Name).ToList();
        _list.SelectedIndex = 0;
    }

    /// <summary>Shows one of the surfaces, by its place in the list.</summary>
    public void Choose(int surface) => _list.SelectedIndex = surface;

    private Surface? Current => _list.SelectedIndex >= 0 && _list.SelectedIndex < _session.Document.Surfaces.Count ? _session.Document.Surfaces[_list.SelectedIndex] : null;

    private void Changed()
    {
        if (_loading) return;
        _changed = true;
        _session.Document.Notify();
    }

    private void ShowSurface()
    {
        _fields.Children.Clear();
        if (Current is not { } s) return;
        _loading = true;
        var name = new TextBox { Text = s.Name };
        name.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == s.Name) return;
            s.Name = name.Text;
            Changed();
            int i = _list.SelectedIndex;
            _list.ItemsSource = _session.Document.Surfaces.Select(x => x.Name).ToList();
            _list.SelectedIndex = i;
        };
        _fields.Children.Add(Label("Name"));
        _fields.Children.Add(name);
        var swatch = new Border { Height = 24, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(s.Color.R, s.Color.G, s.Color.B)) };
        _fields.Children.Add(WithTexture(Label("Colour"), s, TextureChannel.Color));
        _fields.Children.Add(swatch);
        void Swatch() => swatch.Background = new SolidColorBrush(Color.FromRgb(s.Color.R, s.Color.G, s.Color.B));
        _fields.Children.Add(Slider("Red", s.Color.R, 0, 255, v => { s.Color = s.Color with { R = (byte)v }; Swatch(); }));
        _fields.Children.Add(Slider("Green", s.Color.G, 0, 255, v => { s.Color = s.Color with { G = (byte)v }; Swatch(); }));
        _fields.Children.Add(Slider("Blue", s.Color.B, 0, 255, v => { s.Color = s.Color with { B = (byte)v }; Swatch(); }));
        _fields.Children.Add(Slider("Diffuse %", s.Diffuse * 100, 0, 100, v => s.Diffuse = v / 100, s, TextureChannel.Diffuse));
        _fields.Children.Add(Slider("Specular %", s.Specular * 100, 0, 100, v => s.Specular = v / 100, s, TextureChannel.Specular));
        _fields.Children.Add(Slider("Glossiness", s.Glossiness, 4, 1024, v => s.Glossiness = v));
        _fields.Children.Add(Slider("Luminosity %", s.Luminosity * 100, 0, 100, v => s.Luminosity = v / 100, s, TextureChannel.Luminosity));
        _fields.Children.Add(Slider("Transparency %", s.Transparency * 100, 0, 100, v => s.Transparency = v / 100, s, TextureChannel.Transparency));
        _fields.Children.Add(Slider("Reflection %", s.Reflection * 100, 0, 100, v => s.Reflection = v / 100, s, TextureChannel.Reflection));
        _fields.Children.Add(WithTexture(Label("Bump"), s, TextureChannel.Bump));
        _fields.Children.Add(Slider("Smoothing angle °", s.SmoothingAngle, 0, 180, v => s.SmoothingAngle = v));
        var smooth = new CheckBox { Content = "Smoothing", IsChecked = s.Smoothing, Foreground = Look.Text };
        smooth.IsCheckedChanged += (_, _) => { s.Smoothing = smooth.IsChecked == true; Changed(); };
        var both = new CheckBox { Content = "Double sided", IsChecked = s.DoubleSided, Foreground = Look.Text };
        both.IsCheckedChanged += (_, _) => { s.DoubleSided = both.IsChecked == true; Changed(); };
        _fields.Children.Add(smooth);
        _fields.Children.Add(both);
        Button? nodes = null;
        nodes = Look.MakeButton("Nodes…", () => OpenNodes(s, nodes), "The surface's node graph: textures, gradients and sums wired into its settings (seen in renders)");
        Look.SetOn(nodes, s.Nodes?.Output?.Links.Count > 0);
        NodesButton = nodes;
        _fields.Children.Add(nodes);
        _loading = false;
    }

    /// <summary>The Nodes… button, so tests can press it.</summary>
    public Button? NodesButton { get; private set; }

    /// <summary>Opens the node editor for a surface; returns it.</summary>
    public NodeEditor OpenNodes(Surface s, Button? button = null)
    {
        var editor = new NodeEditor(s, () =>
        {
            if (button != null) Look.SetOn(button, s.Nodes?.Output?.Links.Count > 0);
            Changed();
        });
        // A graph with nothing in it but its output is no graph.
        editor.Closed += (_, _) =>
        {
            if (s.Nodes is { Nodes.Count: <= 1 } g && (g.Output?.Links.Count ?? 0) == 0) { s.Nodes = null; Changed(); }
        };
        editor.Show(this);
        return editor;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private Control Slider(string label, double value, double min, double max, Action<double> set, Surface? surface = null, TextureChannel channel = default)
    {
        var text = new TextBlock { Foreground = Look.Text, FontSize = 11, Width = 50, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        void Show() => text.Text = slider.Value.ToString("0.#", CultureInfo.InvariantCulture);
        Show();
        slider.ValueChanged += (_, _) => { set(slider.Value); Show(); Changed(); };
        var row = new DockPanel { Children = { Docked(text, Dock.Right), slider } };
        Control head = surface == null ? Label(label) : WithTexture(Label(label), surface, channel);
        return new StackPanel { Children = { head, row } };
    }

    /// <summary>The "T" buttons beside the settings, by channel, so tests can press them.</summary>
    public Dictionary<TextureChannel, Button> TextureButtons { get; } = [];

    /// <summary>A setting's label with its "T" button, lit when the setting has textures.</summary>
    private Control WithTexture(Control label, Surface s, TextureChannel channel)
    {
        Button? button = null;
        button = Look.MakeButton("T", () => OpenTexture(s, channel, button!), $"{TextureEditor.Describe(channel)} texture", 26);
        button.Padding = new Thickness(0);
        Look.SetOn(button, s.TexturesOf(channel).Any());
        TextureButtons[channel] = button;
        return new DockPanel { Children = { Docked(button, Dock.Right), label } };
    }

    /// <summary>Opens the texture panel for one of a surface's channels; returns it.</summary>
    public TextureEditor OpenTexture(Surface s, TextureChannel channel, Button? button = null)
    {
        var editor = new TextureEditor(_session, s, channel, () =>
        {
            if (button != null) Look.SetOn(button, s.TexturesOf(channel).Any());
            Changed();
        });
        editor.Show(this);
        return editor;
    }
}

/// <summary>The preferences: renderer, undo levels, auto-save, MetaNURBS level, and the key map.</summary>
public sealed class PreferencesWindow : Window
{
    public PreferencesWindow(ModelerSession session, Action applied)
    {
        var p = session.Preferences;
        Title = "Preferences";
        Width = 520;
        Height = 640;
        Background = Look.Panel;
        var renderer = new ComboBox { ItemsSource = new[] { "Software", "OpenGL" }, SelectedItem = p.Renderer };
        var undo = new TextBox { Text = p.UndoLevels.ToString(CultureInfo.InvariantCulture) };
        var autosave = new TextBox { Text = p.AutosaveMinutes.ToString(CultureInfo.InvariantCulture) };
        var level = new ComboBox { ItemsSource = new[] { "1", "2", "3", "4" }, SelectedItem = p.SubdivisionLevel.ToString(CultureInfo.InvariantCulture) };
        var style = new ComboBox { ItemsSource = Enum.GetNames<DisplayStyle>(), SelectedItem = p.PerspectiveStyle.ToString() };
        var agents = new CheckBox
        {
            Content = "Allow AI agents to drive AVA Modeler (MCP, from this user's programs only)", IsChecked = p.AllowAgents, Foreground = Look.Text,
        };
        ToolTip.SetTip(agents, "An agent configured with \"AVAModeler --mcp --attach\" can then build in the open object: you see its work in the views, and Ctrl+Z undoes it.");
        var hub = new CheckBox { Content = "Link with AVALightWave (the hub: this user's programs only)", IsChecked = p.HubEnabled, Foreground = Look.Text };
        ToolTip.SetTip(hub, "AVALightWave's Edit in Modeler opens its objects here, and saving them updates its scene. Takes effect when AVA Modeler next starts.");
        var liveSync = new CheckBox { Content = "Live sync with AVALightWave (send a linked object's changes as you model)", IsChecked = p.HubLiveSync, Foreground = Look.Text };
        ToolTip.SetTip(liveSync, "While the object is linked to one in AVALightWave's scene (Edit in Modeler, or Send to AVALightWave), its changes show there a moment after you stop, without saving.");
        var lightWave = new TextBox { Text = p.LightWavePath ?? "", PlaceholderText = "AVALightWave's program (found beside AVA Modeler or on the PATH if empty)" };
        // The AI Chat's model: Claude, or a local or self-hosted server that speaks the OpenAI Chat Completions API.
        var chatProvider = new ComboBox { ItemsSource = ChatProviders.Select(c => c.Label).ToList(), SelectedIndex = Math.Max(0, ChatProviders.FindIndex(c => c.Id == p.ChatProvider)) };
        var chatUrl = new TextBox { Text = p.ChatServerUrl, PlaceholderText = "http://localhost:11434/v1" };
        var chatModel = new TextBox { Text = p.ChatServerModel, PlaceholderText = "qwen3" };
        var chatCore = new CheckBox { Content = "Core tools only (best for small local models)", IsChecked = p.ChatCoreTools, Foreground = Look.Text };
        var chatPictures = new CheckBox { Content = "Model sees pictures (a vision model)", IsChecked = p.ChatModelSeesPictures, Foreground = Look.Text };
        var chatServer = new StackPanel { Spacing = 4, Children = { Label("Server base URL (OpenAI-compatible)"), chatUrl, Label("Model on the server"), chatModel, chatCore, chatPictures } };
        var chatHistory = new CheckBox { Content = "Keep AI Chat history on this computer (reopen the last chat on start)", IsChecked = p.ChatKeepHistory, Foreground = Look.Text };
        ToolTip.SetTip(chatHistory, "Conversations are saved in the chats folder beside the preferences, readable only by you, never with an API key. Off by default: a chat may hold a client's details.");
        void ShowServer() => chatServer.IsVisible = chatProvider.SelectedIndex == 1;
        chatProvider.SelectionChanged += (_, _) => ShowServer();
        ShowServer();
        var keys = new StackPanel { Spacing = 2 };
        var keyBoxes = new Dictionary<string, TextBox>();
        foreach (var (command, key) in session.Keymap.Bindings.OrderBy(kv => kv.Key))
        {
            var box = new TextBox { Text = key, Width = 140, FontSize = 11 };
            keyBoxes[command] = box;
            keys.Children.Add(new DockPanel
            {
                Children = { Dock(box, Avalonia.Controls.Dock.Right), new TextBlock { Text = command, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center } },
            });
        }
        var save = new Button { Content = "Save", Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            p.Renderer = renderer.SelectedItem as string ?? "Software";
            if (int.TryParse(undo.Text, out int u)) p.UndoLevels = Math.Clamp(u, 1, 10000);
            if (int.TryParse(autosave.Text, out int a)) p.AutosaveMinutes = Math.Clamp(a, 0, 600);
            if (int.TryParse(level.SelectedItem as string, out int l)) p.SubdivisionLevel = l;
            if (Enum.TryParse<DisplayStyle>(style.SelectedItem as string, out var st)) p.PerspectiveStyle = st;
            p.AllowAgents = agents.IsChecked == true;
            p.HubEnabled = hub.IsChecked == true;
            p.HubLiveSync = liveSync.IsChecked == true;
            p.LightWavePath = string.IsNullOrWhiteSpace(lightWave.Text) ? null : lightWave.Text.Trim();
            p.ChatProvider = ChatProviders[Math.Max(0, chatProvider.SelectedIndex)].Id;
            p.ChatServerUrl = string.IsNullOrWhiteSpace(chatUrl.Text) ? new Preferences().ChatServerUrl : chatUrl.Text.Trim();
            p.ChatServerModel = chatModel.Text?.Trim() ?? "";
            p.ChatCoreTools = chatCore.IsChecked == true;
            p.ChatModelSeesPictures = chatPictures.IsChecked == true;
            p.ChatKeepHistory = chatHistory.IsChecked == true;
            p.Keys = keyBoxes.Where(kv => kv.Value.Text != Keymap.Defaults.GetValueOrDefault(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? "");
            p.Save();
            session.ReloadKeys();
            session.Document.Undo.Limit = p.UndoLevels;
            applied();
            Close();
        };
        Content = new DockPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                Dock(save, Avalonia.Controls.Dock.Bottom),
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            Label("Viewports"), renderer, Label("Perspective view"), style, Label("Undo levels"), undo,
                            Label("Auto-save every (minutes, 0 for never)"), autosave, Label("MetaNURBS smoothing level"), level,
                            Label("AI agents"), agents,
                            Label("AVALightWave"), hub, liveSync, lightWave,
                            Label("AI Chat model"), chatProvider, chatServer, chatHistory,
                            Label("Keys (e.g. T, Shift+X, Ctrl+Z)"), keys,
                        },
                    },
                },
            },
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 12, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 2) };

    /// <summary>The AI Chat's providers, as <see cref="Preferences.ChatProvider"/> keeps them and as they're shown.</summary>
    public static readonly List<(string Id, string Label)> ChatProviders =
    [
        ("Anthropic", "Claude (Anthropic API)"),
        ("OpenAICompatible", "OpenAI-compatible server (local or self-hosted)"),
    ];

    private static Control Dock(Control c, Avalonia.Controls.Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }
}
