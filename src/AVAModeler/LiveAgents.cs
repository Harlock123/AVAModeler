using Avalonia.Threading;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Tools;

namespace AVAModeler;

/// <summary>
/// The app's live MCP door: while AI agents are allowed (Macros › Allow AI Agents, or Preferences), a
/// <see cref="LiveListener"/> takes connections from <c>AVAModeler --mcp --attach</c> bridges, and every call runs on
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
