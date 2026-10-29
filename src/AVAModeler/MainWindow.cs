using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AVAModeler.Chat;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Geometry;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Tools;

namespace AVAModeler;

/// <summary>
/// The window: the Modeler and Layout workspaces, one at a time (F1 for the Modeler, F2 for Layout, or the buttons in
/// each). It loads the plugins, keeps an auto-save of a modified object, and after a crash offers it back.
/// </summary>
public sealed class MainWindow : Window
{
    public ModelerSession Session { get; }
    public ModelerWorkspace Modeler { get; }
    private LayoutWorkspace? _layout;
    private readonly LayoutSession _layoutSession = new();

    /// <summary>Layout's state, there from the start (agents can work on the scene before Layout is first shown).</summary>
    internal LayoutSession LayoutSession => _layoutSession;
    private readonly DispatcherTimer _autosave = new();

    /// <summary>AI agents' live access (MCP), open while the preference allows it.</summary>
    internal LiveAgents Agents { get; }

    /// <summary>The AI Chat panel (F4): Claude working in the open window, through the app's own MCP server in-process.</summary>
    internal ChatPanel Chat { get; }

    private readonly LazyToolBridge _chatTools;
    private bool _chatOpen;

    /// <summary>Whether the chat panel is showing (beside whichever workspace is on screen).</summary>
    internal bool ChatOpen => _chatOpen;

    /// <summary>The chat's working folder, where files Claude opens and saves are (the Documents folder).</summary>
    internal string ChatRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs
        ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The pipe agents reach the app on (tests give it one of their own).</summary>
    internal string AgentPipe { get; set; } = LiveProtocol.PipeName;

    /// <summary>Marks a running session; left behind if the app doesn't close cleanly.</summary>
    private static string LockPath => Path.Combine(Preferences.Folder, "running.lock");
    private static string AutosavePath => Path.Combine(Preferences.AutosaveFolder, "autosave.lwo");

    public MainWindow(string[] args) : this(args, Preferences.Load(), loadPlugins: true) { }

    /// <summary>For tests: preferences of its own, and no plugins from the user's folder.</summary>
    public MainWindow(string[] args, Preferences prefs, bool loadPlugins)
    {
        ModelerSession.Subdivide = (mesh, levels) => Subdivision.CatmullClark(mesh, levels);
        var registry = ToolRegistry.Create();
        var problems = loadPlugins ? registry.LoadPlugins(Preferences.PluginsFolder) : [];
        Session = new ModelerSession(prefs, registry);
        Modeler = new ModelerWorkspace(Session);
        Modeler.ShowLayout += ShowLayout;
        Agents = new LiveAgents(Session, _layoutSession);
        Modeler.AgentsChanged += SyncAgents;
        Modeler.ToggleChat += ToggleChat;
        // Tests never see the person's own key: no environment variable, no key file.
        var keys = loadPlugins ? ApiKeyStore.ForApp(Preferences.Folder) : new ApiKeyStore(null, null);
        _chatTools = new LazyToolBridge(async cancel => await McpToolBridge.StartAsync(Agents.ChatHost(ChatRoot), cancel));
        Chat = new ChatPanel(_chatTools, keys, text => { Session.SetStatus(text); _layoutSession.SetStatus(text); });
        Chat.CloseRequested += ToggleChat;
        Width = 1400;
        Height = 900;
        MinWidth = 800;
        MinHeight = 560;
        Content = Modeler;
        UpdateTitle();
        Session.Changed += UpdateTitle;
        if (problems.Count > 0) Session.SetStatus("Some plugins didn't load: " + string.Join("; ", problems));

        if (loadPlugins)
        {
            _autosave.Interval = TimeSpan.FromMinutes(Math.Max(1, prefs.AutosaveMinutes));
            _autosave.Tick += (_, _) => Autosave();
            if (prefs.AutosaveMinutes > 0) _autosave.Start();
            Opened += async (_, _) => await Started(args);
            Closing += Closing_;
            SyncAgents();
        }
        Closed += (_, _) =>
        {
            Chat.Stop();
            _ = _chatTools.DisposeAsync().AsTask();
            _ = Agents.StopAsync();
        };
    }

    /// <summary>Opens or closes the door to AI agents to match the preference.</summary>
    internal void SyncAgents()
    {
        if (Session.Preferences.AllowAgents && !Agents.Running)
        {
            Session.SetStatus(Agents.Start(AgentPipe) is { } problem
                ? $"AI agents can't connect: {problem} (Is another AVA Modeler already taking them?)"
                : "AI agents may connect (MCP): configure them with \"AVAModeler --mcp --attach\".");
        }
        else if (!Session.Preferences.AllowAgents && Agents.Running)
        {
            _ = Agents.StopAsync();
            Session.SetStatus("AI agents can no longer connect.");
        }
    }

    private void UpdateTitle()
    {
        var doc = Session.Document;
        string name = doc.FilePath != null ? Path.GetFileName(doc.FilePath) : "Untitled";
        Title = $"AVA Modeler — {name}{(doc.Modified ? " *" : "")}";
    }

    public void ShowModeler()
    {
        Content = Modeler;
        PlaceChat();
        Modeler.Views.Redraw();
    }

    /// <summary>Shows or hides the AI Chat panel (F4, Macros › AI Chat, or Layout's AI › Chat).</summary>
    internal void ToggleChat()
    {
        _chatOpen = !_chatOpen;
        PlaceChat();
        if (_chatOpen)
        {
            Chat.RefreshIntro();
            Chat.FocusInput();
        }
    }

    /// <summary>The panel goes on the right of the workspace on screen, below its menus.</summary>
    private void PlaceChat()
    {
        if (Chat.Parent is Panel old) old.Children.Remove(Chat);
        if (!_chatOpen || Content is not DockPanel workspace) return;
        DockPanel.SetDock(Chat, Dock.Right);
        // After the menus (the first child), so they keep the full width.
        workspace.Children.Insert(Math.Min(1, workspace.Children.Count), Chat);
    }

    public void ShowLayout()
    {
        Session.CommitTool();
        if (_layout == null)
        {
            _layout = new LayoutWorkspace(_layoutSession, () => Session.Document);
            _layout.ShowModeler += ShowModeler;
            _layout.ToggleChat += ToggleChat;
            _layoutSession.ShowModelerObject(Session.Document);
        }
        else _layoutSession.ShowModelerObject(Session.Document);
        Content = _layout;
        PlaceChat();
        _layoutSession.Scene.Notify();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.F1) { ShowModeler(); e.Handled = true; return; }
        if (e.Key == Key.F2) { ShowLayout(); e.Handled = true; return; }
        if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None) { ToggleChat(); e.Handled = true; return; }
        if (Content == Modeler) e.Handled = Modeler.HandleKey(e);
        else if (Content == _layout) e.Handled = _layout!.HandleKey(e);
    }

    // ───────────────────────────── starting, auto-save and recovery ─────────────────────────────

    private async Task Started(string[] args)
    {
        bool crashed = File.Exists(LockPath) && File.Exists(AutosavePath);
        try
        {
            Directory.CreateDirectory(Preferences.Folder);
            await File.WriteAllTextAsync(LockPath, Environment.ProcessId.ToString());
        }
        catch (IOException) { }
        if (crashed && await Dialogs.Ask(this, "Recover your work?",
                $"AVA Modeler didn't close properly last time. An auto-save from {File.GetLastWriteTime(AutosavePath):g} is waiting. Open it?", "Open it", "No thanks"))
        {
            await Modeler.Open(AutosavePath);
            Session.Document.FilePath = null;
            Session.Document.Modified = true;
            UpdateTitle();
        }
        else if (args.FirstOrDefault(a => File.Exists(a)) is { } path) await Modeler.Open(path);
    }

    /// <summary>Saves a modified object to the auto-save file (never over the user's own file).</summary>
    private void Autosave()
    {
        var doc = Session.Document;
        if (!doc.Modified || doc.Layers.All(l => l.Mesh.IsEmpty)) return;
        var path = doc.FilePath;
        try
        {
            Directory.CreateDirectory(Preferences.AutosaveFolder);
            ObjectFiles.Save(doc, AutosavePath);
            Session.SetStatus($"Auto-saved at {DateTime.Now:t}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally
        {
            // The user's own file and its unsaved state are untouched by the auto-save.
            doc.FilePath = path;
            doc.Modified = true;
        }
    }

    private bool _closingConfirmed;

    private async void Closing_(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !Session.Document.Modified) { CleanExit(); return; }
        e.Cancel = true;
        if (await Dialogs.Ask(this, "Quit?", "The object has changes that haven't been saved. Quit anyway?", "Quit", "Keep working"))
        {
            _closingConfirmed = true;
            Close();
        }
    }

    /// <summary>A clean exit: no lock or auto-save left behind, and the preferences kept.</summary>
    private void CleanExit()
    {
        _autosave.Stop();
        Session.Preferences.Save();
        try
        {
            if (File.Exists(LockPath)) File.Delete(LockPath);
            if (File.Exists(AutosavePath)) File.Delete(AutosavePath);
        }
        catch (IOException) { }
    }
}
