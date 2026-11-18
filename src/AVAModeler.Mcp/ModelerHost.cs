using System.Diagnostics;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Tools;

namespace AVAModeler.Mcp;

/// <summary>
/// Where the MCP tools' work runs. The tool handlers (<see cref="ModelerTools"/>) only ever touch the document and the
/// scene through <see cref="RunAsync{T}"/> and <see cref="RunSceneAsync{T}"/>, so the same handlers serve the headless
/// server (<see cref="HeadlessHost"/>, a document and scene of its own) and the live app (<see cref="LiveHost"/>, the
/// open document and Layout's scene, on the UI thread).
/// </summary>
public interface IModelerHost
{
    /// <summary>The folder relative paths start from; open and save stay inside it.</summary>
    string Root { get; }

    /// <summary>
    /// Runs <paramref name="work"/> against the command API on the thread that owns the document, one call at a time,
    /// and returns what it returns (or throws what it throws). <paramref name="action"/> names it for the app's status
    /// bar.
    /// </summary>
    Task<T> RunAsync<T>(string action, Func<Commands, T> work, CancellationToken cancel = default);

    /// <summary>
    /// Runs <paramref name="work"/> as <see cref="RunAsync{T}"/> does, with Layout's session (its scene, frame and
    /// auto-key) beside the command API.
    /// </summary>
    /// <remarks>
    /// A call made from inside another's work (a batch running its steps) runs at once, on the same thread, and its
    /// task is already complete when it returns: it's part of the call under way, not a new one waiting its turn.
    /// </remarks>
    Task<T> RunSceneAsync<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel = default);
}

/// <summary>Runs a call made from inside another call's work at once (see <see cref="IModelerHost.RunSceneAsync{T}"/>).</summary>
internal sealed class Reentry
{
    private int _thread = -1;

    /// <summary>Whether the current thread is in the middle of a call's work.</summary>
    public bool Inside => Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId;

    /// <summary>Runs a call's work, noting that this thread is inside it (a nested call then runs inline).</summary>
    public T Run<T>(Func<T> work)
    {
        int outer = _thread;
        _thread = Environment.CurrentManagedThreadId;
        try { return work(); }
        finally { _thread = outer; }
    }

    /// <summary>A nested call's work, run now, as a finished task.</summary>
    public static Task<T> Inline<T>(Func<T> work)
    {
        try { return Task.FromResult(work()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }
}

/// <summary>
/// The headless host: a document and a Layout scene of its own with no window, as <c>AVAModeler --mcp</c> serves it.
/// Calls run on whichever thread the request came in on, one at a time.
/// </summary>
public sealed class HeadlessHost(string root, ToolRegistry? registry = null) : IModelerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Reentry _reentry = new();

    public string Root { get; } = Path.GetFullPath(root);
    public Commands Commands { get; } = new(new Document(), registry);

    /// <summary>Layout's state: the scene (Layout's default, a camera and a light), its frame and auto-key.</summary>
    public LayoutSession Layout { get; } = new();

    public Task<T> RunAsync<T>(string action, Func<Commands, T> work, CancellationToken cancel = default) =>
        RunSceneAsync(action, (c, _) => work(c), cancel);

    public Task<T> RunSceneAsync<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel = default) =>
        _reentry.Inside ? Reentry.Inline(() => work(Commands, Layout)) : Queued(work, cancel);

    private async Task<T> Queued<T>(Func<Commands, LayoutSession, T> work, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return _reentry.Run(() => work(Commands, Layout));
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Runs work on a UI thread (the app's Avalonia dispatcher, or a test's stand-in).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<T> work);
}

/// <summary>The running app as the live host sees it. Every member is used on the UI thread only.</summary>
public interface ILiveModeler
{
    /// <summary>The command API on the document now open (it changes when a document is opened or made).</summary>
    Commands Commands { get; }

    /// <summary>Layout's state in the app (its scene is the one Layout shows, whether or not Layout is on screen).</summary>
    LayoutSession Layout { get; }

    /// <summary>
    /// Why the app can't take a call just now ("the Move tool is in use", "a dialog box is open"), or null when it can.
    /// The person's work in progress isn't interrupted: the call waits for it.
    /// </summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did (the status bar).</summary>
    void Report(string text);
}

/// <summary>
/// The live host: an agent's calls run against the document open in the app, on its UI thread, so the views redraw
/// and Ctrl+Z undoes them like the person's own edits. While the person is in the middle of something (a tool armed,
/// a dialog box open) a call waits for up to <see cref="BusyWait"/>, then fails saying why.
/// </summary>
public sealed class LiveHost(IUiThread ui, ILiveModeler modeler, string root) : IModelerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Reentry _reentry = new();

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>How long a call waits for the person to finish what they're doing.</summary>
    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a waiting call looks again.</summary>
    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public Task<T> RunAsync<T>(string action, Func<Commands, T> work, CancellationToken cancel = default) =>
        RunSceneAsync(action, (c, _) => work(c), cancel);

    public Task<T> RunSceneAsync<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel = default) =>
        _reentry.Inside ? Reentry.Inline(() => work(modeler.Commands, modeler.Layout)) : Queued(action, work, cancel);

    private async Task<T> Queued<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel)
    {
        // The call's scope (its error's kind, progress, the person's yes) goes with it to the UI thread, whose
        // dispatcher needn't carry it.
        var scope = ModelerTools.Scope.Value;
        var inner = work;
        work = (c, l) =>
        {
            var outer = ModelerTools.Scope.Value;
            ModelerTools.Scope.Value = scope;
            try { return inner(c, l); }
            finally { ModelerTools.Scope.Value = outer; }
        };
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(() =>
                {
                    if (modeler.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = _reentry.Run(() => work(modeler.Commands, modeler.Layout));
                        modeler.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        modeler.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new ModelerBusyException($"AVA Modeler is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The live app stayed busy with the person's own work for too long to take a call.</summary>
public sealed class ModelerBusyException(string message) : Exception(message);
