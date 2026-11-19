using Avalonia.Threading;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Tools;

namespace AVAModeler;

/// <summary>
/// The app's live MCP door: while AI agents are allowed (Macros › Allow AI Agents, or Preferences), a
/// <see cref="LiveListener"/> takes connections from <c>AVAModeler --mcp --attach</c> bridges (and, while agents on
/// other computers are allowed, a <see cref="NetworkListener"/> from MCP clients on the network), and every call runs on
/// the UI thread against the open document, through the session's command API, and against Layout's scene, so the views
/// redraw as the agent works and Ctrl+Z undoes its modeling steps. Calls wait while the person has a tool armed or a
/// dialog box open. Scene calls don't switch the window to Layout: the person may be modeling, and Layout shows the
/// agent's scene whenever they turn to it (F2).
/// </summary>
internal sealed class LiveAgents(ModelerSession session, LayoutSession layout) : ILiveModeler, IUiThread, IAsyncDisposable
{
    private LiveListener? _listener;

    public bool Running => _listener != null;

    /// <summary>How long an agent's call waits for the person to finish with a tool or a dialog box.</summary>
    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    public Commands Commands => session.Commands;

    public LayoutSession Layout => layout;

    public string? Busy =>
        session.ActiveTool is { } tool ? $"the person is using the {tool.Name} tool (Enter keeps its work, Esc drops it)"
        : Dialogs.ModalsOpen > 0 ? "a dialog box is open in the app"
        : null;

    /// <summary>
    /// Both workspaces' status bars say what the agent did, whichever is on screen, and Layout shows its item list and
    /// the chosen item's settings afresh (an agent may have renamed or changed them).
    /// </summary>
    public void Report(string text)
    {
        session.SetStatus(text);
        layout.SetStatus(text);
        layout.NotifyChangedElsewhere();
    }

    public Task<T> InvokeAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    /// <summary>
    /// The live host the AI Chat panel's in-process MCP server runs on: the same one outside agents get (UI thread,
    /// waiting while the person is busy, each call one undo step), whether or not agents are allowed in from outside.
    /// Its status-bar lines say Claude is working.
    /// </summary>
    public LiveHost ChatHost(string root) => new(this, new ChatReports(this), root) { BusyWait = BusyWait };

    private sealed class ChatReports(LiveAgents agents) : ILiveModeler
    {
        public Commands Commands => agents.Commands;
        public LayoutSession Layout => agents.Layout;
        public string? Busy => agents.Busy;
        public void Report(string text) => agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? "Claude is working… " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVA Modeler has it), or null.</summary>
    public string? Start(string pipeName)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, root => new LiveHost(this, this, root) { BusyWait = BusyWait });
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() =>
            session.SetStatus(n > 0 ? $"AI agents connected: {n}. Their changes show here, and Ctrl+Z undoes them." : "The AI agent disconnected."));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        // The pipe closes now, so the door can be opened again straight away; the sessions wind down after.
        listener.Stop();
        await listener.DisposeAsync();
    }

    private NetworkListener? _network;

    /// <summary>Whether the network door is open.</summary>
    public bool NetworkRunning => _network != null;

    /// <summary>The settings and working folder the network door was opened with (to tell when they've changed).</summary>
    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    /// <summary>Where the network door listens, while it's open.</summary>
    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    /// <summary>The network door's port, while it's open (the one the system chose, for port 0).</summary>
    public int NetworkPort => _network?.Port ?? 0;

    /// <summary>
    /// Opens the network door: agents on other computers reach the open document through the same live host as the
    /// pipe's (UI thread, waiting while the person is busy, each call one undo step), working in <paramref name="root"/>.
    /// Returns why it couldn't open, or null.
    /// </summary>
    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var listener = new NetworkListener(settings, ServerProfile.Modeler.Name,
            o => ModelerServer.Configure(o, new LiveHost(this, this, root) { BusyWait = BusyWait }, ServerProfile.Modeler), ModelerServer.EndSessionAsync);
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() =>
            session.SetStatus(n > 0 ? $"AI agents on the network: {n}. Their changes show here, and Ctrl+Z undoes them." : "No AI agent on the network is working just now."));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}

/// <summary>
/// The network door's token and addresses for the Preferences window: the token in its owner-only file beside the
/// preferences (only in memory for a window made for tests).
/// </summary>
internal sealed class NetworkDoor(LiveAgents agents, string? folder) : INetworkDoor
{
    private string? _memory;

    private NetworkToken? Store => folder == null ? null : NetworkToken.In(folder);

    public string Token => Store?.Get() ?? (_memory ??= NetworkListener.NewToken());

    public string RenewToken()
    {
        var token = Store?.Renew() ?? (_memory = NetworkListener.NewToken());
        Renewed?.Invoke();
        return token;
    }

    public IReadOnlyList<string> Urls => agents.NetworkUrls;

    /// <summary>Raised when the token changes, so an open door reopens with the new one.</summary>
    public event Action? Renewed;
}
