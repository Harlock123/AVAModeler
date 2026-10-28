using System.Collections.Concurrent;
using AVAModeler.Core;
using AVAModeler.Tools;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>A stand-in for the app's UI thread: one thread of its own running the work it's given, in order.</summary>
internal sealed class TestUiThread : IUiThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(() => { foreach (var job in _queue.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "test UI" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A stand-in for the running app: a document, whether the person is busy, and the status bar.</summary>
internal sealed class TestModeler(TestUiThread ui) : ILiveModeler
{
    private readonly Commands _commands = new(new Document());
    public volatile string? BusyWith;
    public readonly ConcurrentQueue<string> Reports = new();
    public int WrongThread;

    public Commands Commands
    {
        get
        {
            if (Environment.CurrentManagedThreadId != ui.ThreadId) Interlocked.Increment(ref WrongThread);
            return _commands;
        }
    }

    public Document Document => _commands.Document;

    public string? Busy => BusyWith;

    public void Report(string text) => Reports.Enqueue(text);
}

/// <summary>
/// The live mode in one process: a <see cref="LiveListener"/> on a pipe serving a document on a test UI thread, and
/// an agent reaching it through the <see cref="LiveBridge"/>, as <c>AVAModeler --mcp --attach</c> would.
/// </summary>
public sealed class LiveTests : IDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TestModeler _app;
    private readonly TempFolder _folder = new();
    private readonly string _pipe = "avamodeler-test-" + Guid.NewGuid().ToString("N")[..12];

    public LiveTests() => _app = new TestModeler(_ui);

    private LiveListener Listen(TimeSpan? busyWait = null)
    {
        var listener = new LiveListener(_pipe, root => new LiveHost(_ui, _app, root) { BusyWait = busyWait ?? TimeSpan.FromSeconds(5), BusyPoll = TimeSpan.FromMilliseconds(20) });
        listener.Start();
        return listener;
    }

    private Task<InProcess> Agent(TimeSpan? timeout = null) =>
        InProcess.Start(new LiveBridge(_pipe, _folder.Path) { ConnectTimeout = timeout ?? TimeSpan.FromSeconds(5) }.Options());

    [Fact]
    public async Task Calls_are_run_on_the_ui_thread_against_the_open_document()
    {
        await using var listener = Listen();
        int connections = 0;
        listener.ConnectionsChanged += n => Volatile.Write(ref connections, n);
        await using var agent = await Agent();
        var tools = await agent.Client.ListToolsAsync(cancellationToken: Cancel);
        Assert.Equal(ModelerServer.Definitions().Select(t => t.Name), tools.Select(t => t.Name));

        var r = await agent.Client.Json("run_tool", new { name = "Box", @params = new { Size = new[] { 2.0, 1, 1 } } });
        Assert.Equal(8, (int)r["points"]!["after"]!);
        Assert.Equal(8, await _ui.InvokeAsync(() => _app.Document.Current.Mesh.Points.Count));
        Assert.Equal(1, Volatile.Read(ref connections));
        Assert.Equal(0, _app.WrongThread);
        Assert.Contains("AI agent: ran Box.", _app.Reports);

        // The person's Ctrl+Z undoes the agent's step, and the agent sees it.
        await _ui.InvokeAsync(() => _app.Document.UndoLast());
        var doc = await agent.Client.Json("get_document");
        Assert.Equal(0, (int)doc["undo"]!["depth"]!);
        Assert.Equal(1, await _ui.InvokeAsync(() => _app.Document.Undo.RedoLabel == "Box" ? 1 : 0));

        // Files resolve against the agent's working folder, sent in the handshake.
        await agent.Client.Json("run_tool", new { name = "Ball" });
        var saved = await agent.Client.Json("save", new { path = "ball.lwo" });
        Assert.Equal(_folder.File("ball.lwo"), (string?)saved["saved"]);
        // Pictures are drawn from a copy, away from the UI thread.
        var view = await agent.Client.Call("render_view", new { width = 64, height = 48 });
        Assert.True(Pngs.Inspect(Pngs.Of(view)).Colours > 4);
        // Errors come back through the bridge as tool errors.
        Assert.Contains("no tool called", await agent.Client.Fails("run_tool", new { name = "Nope" }));
        Assert.Contains("run_tool has no argument \"nam\"", await agent.Client.Fails("run_tool", new { nam = "Box" }));
        Assert.Equal(0, _app.WrongThread);
    }

    [Fact]
    public async Task A_call_waits_while_the_person_is_busy_then_says_why()
    {
        await using var listener = Listen(TimeSpan.FromMilliseconds(600));
        await using var agent = await Agent();
        _app.BusyWith = "the person is using the Move tool";
        var message = await agent.Client.Fails("run_tool", new { name = "Box" });
        Assert.Contains("AVA Modeler is busy: the person is using the Move tool", message);
        Assert.Empty(await _ui.InvokeAsync(() => _app.Document.Current.Mesh.Points));

        // If the person finishes while it waits, the call goes ahead.
        var call = agent.Client.Json("run_tool", new { name = "Box" });
        await Task.Delay(150, Cancel);
        Assert.False(call.IsCompleted);
        _app.BusyWith = null;
        Assert.Equal(8, (int)(await call)["points"]!["after"]!);
    }

    [Fact]
    public async Task With_no_app_running_the_bridge_says_how_to_start_one_and_connects_once_there_is()
    {
        await using var agent = await Agent(TimeSpan.FromMilliseconds(300));
        // The tools are listed even before the app is there.
        Assert.Equal(30, (await agent.Client.ListToolsAsync(cancellationToken: Cancel)).Count);
        var message = await agent.Client.Fails("get_document");
        Assert.Contains("No AVA Modeler is taking AI agents", message);
        Assert.Contains("Allow AI Agents", message);

        var listener = Listen();
        var doc = await agent.Client.Json("get_document");
        Assert.Equal(0, (int)doc["current_layer"]!);
        await listener.DisposeAsync();
        // The app went away: the next call says so, and once it's back the bridge reconnects.
        var lost = await agent.Client.Fails("get_document");
        Assert.True(lost.Contains("lost") || lost.Contains("No AVA Modeler"), lost);
        await using var again = Listen();
        Assert.Equal(0, (int)(await agent.Client.Json("get_document"))["current_layer"]!);
    }

    [Fact]
    public async Task A_second_app_cant_take_the_pipe_from_the_first()
    {
        await using var first = Listen();
        await using var second = new LiveListener(_pipe, root => new LiveHost(_ui, _app, root));
        var e = Assert.Throws<IOException>(second.Start);
        Assert.Contains("Another AVA Modeler", e.Message);
        // The first still answers (the probe's knock didn't upset it).
        await using var agent = await Agent();
        Assert.Equal(0, (int)(await agent.Client.Json("get_document"))["current_layer"]!);
    }

    [Fact]
    public async Task Stopping_frees_the_pipe_at_once()
    {
        var first = Listen();
        first.Stop();
        // Turning live mode off and straight back on works: the pipe is free before the old sessions wind down.
        await using var second = Listen();
        await first.DisposeAsync();
        await using var agent = await Agent();
        Assert.Equal(0, (int)(await agent.Client.Json("get_document"))["current_layer"]!);
    }

    [Fact]
    public async Task The_handshake_refuses_a_working_folder_that_isnt_there()
    {
        await using var listener = Listen();
        var e = await Assert.ThrowsAsync<LiveConnectionException>(() =>
            LiveBridge.ConnectAsync(_pipe, Path.Combine(_folder.Path, "nowhere"), TimeSpan.FromSeconds(5), Cancel));
        Assert.Contains("doesn't exist", e.Message);
        Assert.Contains("protocol only", LiveProtocol.Check("{}", out _));
        Assert.Contains("version", LiveProtocol.Check("{\"protocol\":\"avamodeler-mcp\",\"version\":99,\"root\":\"/\"}", out _));
        Assert.Null(LiveProtocol.Check(LiveProtocol.Hello(_folder.Path).ToJsonString(), out var root));
        Assert.Equal(_folder.Path, root);
    }

    public void Dispose()
    {
        _ui.Dispose();
        _folder.Dispose();
    }
}
