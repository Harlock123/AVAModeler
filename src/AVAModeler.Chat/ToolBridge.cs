using System.IO.Pipelines;
using System.Text.Json;
using AVAModeler.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>A picture a tool sent back (render_view, render, render_scene and the like).</summary>
public sealed record ToolImage(byte[] Data, string MediaType);

/// <summary>What a tool call came to: its text, any pictures, and whether it failed.</summary>
public sealed record ToolOutcome(bool IsError, string Text, IReadOnlyList<ToolImage> Images)
{
    public static ToolOutcome Error(string text) => new(true, text, []);
}

/// <summary>The tools the chat's Claude may call: exactly the MCP server's, and a way to call them.</summary>
public interface IToolBridge
{
    /// <summary>The tools' MCP definitions: names, descriptions and JSON input schemas.</summary>
    Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancel = default);

    /// <summary>Calls a tool. A tool's own failure comes back as an error outcome, not an exception.</summary>
    Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancel = default);

    /// <summary>Calls a tool, hearing its progress (0 to 1, and a line) as it goes.</summary>
    Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, IProgress<(double Done, string? Message)>? progress, CancellationToken cancel = default) =>
        CallAsync(name, input, cancel);

    /// <summary>
    /// Asks the person something the server wants confirmed (MCP elicitation: an agent about to remove most of a
    /// layer); true to go ahead. Null: nothing can ask, so the server doesn't.
    /// </summary>
    Func<string, CancellationToken, Task<bool>>? Ask { get => null; set { } }

    /// <summary>
    /// Where the server keeps the chat's checkpoints asked to persist (the saved chat's folder), or null for none. Set
    /// before taking or restoring one.
    /// </summary>
    string? CheckpointFolder { get => null; set { } }

    /// <summary>The object (first) or the scene (second) was changed by the person or another agent, not by the chat.</summary>
    event Action<bool, bool>? ChangedElsewhere { add { } remove { } }
}

/// <summary>A tool call's progress, as the chat hears it, from MCP's progress notifications.</summary>
internal sealed class ProgressAdapter(IProgress<(double, string?)> to) : IProgress<ProgressNotificationValue>
{
    public void Report(ProgressNotificationValue value) =>
        to.Report((value.Total is > 0 ? value.Progress / value.Total.Value : value.Progress, value.Message));
}

/// <summary>
/// The tool bridge as an in-process MCP client talking to the app's own MCP server (<see cref="ModelerServer"/>) over a
/// pair of in-memory pipes. The server is built over an <see cref="IModelerHost"/> (the app's <see cref="LiveHost"/>),
/// so the chat and outside agents share one code path: the same argument checks, the same marshalling to the UI
/// thread, the same waiting while the person is busy, and each call one undo step.
/// </summary>
public sealed class McpToolBridge : IToolBridge, IAsyncDisposable
{
    private readonly Pipe _up = new(), _down = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _server = Task.CompletedTask;
    private McpClient _client = null!;
    private IReadOnlyList<McpTool>? _tools;
    private McpServerOptions _options = null!;
    private ModelerTools? _session;

    private McpToolBridge() { }

    /// <summary>Starts the server over <paramref name="host"/> and connects to it.</summary>
    public static Task<McpToolBridge> StartAsync(IModelerHost host, CancellationToken cancel = default) => StartAsync(host, ServerProfile.Modeler, cancel);

    /// <summary>Starts a profile's server (its tools only: a standalone Layout's scene tools, say) over <paramref name="host"/> and connects to it.</summary>
    public static async Task<McpToolBridge> StartAsync(IModelerHost host, ServerProfile profile, CancellationToken cancel = default)
    {
        var bridge = new McpToolBridge();
        var transport = new StreamServerTransport(bridge._up.Reader.AsStream(), bridge._down.Writer.AsStream(), "chat");
        bridge._options = ModelerServer.Options(host, profile);
        bridge._session = ModelerServer.SessionOf(bridge._options);
        if (bridge._session != null) bridge._session.ChangedElsewhere += (obj, scene) => bridge._changedElsewhere?.Invoke(obj, scene);
        var server = McpServer.Create(transport, bridge._options);
        bridge._server = Task.Run(async () =>
        {
            await using (server) await server.RunAsync(bridge._stop.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = profile.Name + " Chat", Version = ModelerServer.InfoFor(profile).Version },
            // The server's questions for the person (elicitation) are asked in the chat, with buttons.
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (request, cancel) =>
                    bridge.Ask is { } ask && await ask(request?.Message ?? "Go ahead?", cancel).ConfigureAwait(false)
                        ? new ElicitResult { Action = "accept" }
                        : new ElicitResult { Action = "decline" },
            },
        };
        bridge._client = await McpClient.CreateAsync(new StreamClientTransport(bridge._up.Writer.AsStream(), bridge._down.Reader.AsStream()), options, null, cancel).ConfigureAwait(false);
        return bridge;
    }

    /// <summary>The instructions the server gives every agent (the chat's system prompt starts with them).</summary>
    public string? ServerInstructions => _client.ServerInstructions;

    public Func<string, CancellationToken, Task<bool>>? Ask { get; set; }

    public string? CheckpointFolder
    {
        get => _session?.CheckpointFolder;
        set { if (_session != null) _session.CheckpointFolder = value; }
    }

    private Action<bool, bool>? _changedElsewhere;

    public event Action<bool, bool>? ChangedElsewhere
    {
        add => _changedElsewhere += value;
        remove => _changedElsewhere -= value;
    }

    public Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancel = default) =>
        CallAsync(name, input, null, cancel);

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancel = default) =>
        _tools ??= (await _client.ListToolsAsync(cancellationToken: cancel).ConfigureAwait(false)).Select(t => t.ProtocolTool).ToList();

    public async Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, IProgress<(double Done, string? Message)>? progress, CancellationToken cancel = default)
    {
        var args = input.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Clone());
        try
        {
            var result = await _client.CallToolAsync(name, args, progress == null ? null : new ProgressAdapter(progress), cancellationToken: cancel).ConfigureAwait(false);
            return Outcome(result);
        }
        catch (McpException e)
        {
            return ToolOutcome.Error(e.Message);
        }
    }

    /// <summary>An MCP result as the chat keeps it: text joined, pictures decoded.</summary>
    public static ToolOutcome Outcome(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        var images = result.Content.OfType<ImageContentBlock>().Select(i => new ToolImage(i.DecodedData.ToArray(), i.MimeType)).ToList();
        return new ToolOutcome(result.IsError == true, text, images);
    }

    public async ValueTask DisposeAsync()
    {
        // The chat's session is over: its locks go and it leaves the app's session list.
        try { await ModelerServer.EndSessionAsync(_options).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or InvalidOperationException) { }
        await _client.DisposeAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
        _stop.Dispose();
    }
}

/// <summary>A bridge started on first use (the app starts its in-process MCP server only when the chat is used).</summary>
public sealed class LazyToolBridge(Func<CancellationToken, Task<IToolBridge>> start) : IToolBridge, IAsyncDisposable
{
    private Task<IToolBridge>? _bridge;

    private Task<IToolBridge> Bridge(CancellationToken cancel)
    {
        // Started once, without the first caller's cancellation; tried again if it failed.
        if (_bridge is { IsFaulted: true } or { IsCanceled: true }) _bridge = null;
        return _bridge ??= start(CancellationToken.None);
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancel = default) =>
        await (await Started(cancel).ConfigureAwait(false)).ListToolsAsync(cancel).ConfigureAwait(false);

    public async Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancel = default) =>
        await (await Started(cancel).ConfigureAwait(false)).CallAsync(name, input, cancel).ConfigureAwait(false);

    public async Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, IProgress<(double Done, string? Message)>? progress, CancellationToken cancel = default) =>
        await (await Started(cancel).ConfigureAwait(false)).CallAsync(name, input, progress, cancel).ConfigureAwait(false);

    private Func<string, CancellationToken, Task<bool>>? _ask;
    private string? _checkpointFolder;
    private Action<bool, bool>? _changed;

    public Func<string, CancellationToken, Task<bool>>? Ask
    {
        get => _ask;
        set { _ask = value; if (_bridge is { IsCompletedSuccessfully: true } b) b.Result.Ask = value; }
    }

    public string? CheckpointFolder
    {
        get => _checkpointFolder;
        set { _checkpointFolder = value; if (_bridge is { IsCompletedSuccessfully: true } b) b.Result.CheckpointFolder = value; }
    }

    public event Action<bool, bool>? ChangedElsewhere
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// <summary>The started bridge, given what was set before it started (the person's questions, the folder, change notices).</summary>
    private async Task<IToolBridge> Started(CancellationToken cancel)
    {
        bool fresh = _bridge is not { IsCompletedSuccessfully: true };
        var bridge = await Bridge(cancel).WaitAsync(cancel).ConfigureAwait(false);
        if (fresh)
        {
            bridge.Ask = _ask;
            bridge.CheckpointFolder = _checkpointFolder;
            bridge.ChangedElsewhere -= Relay;
            bridge.ChangedElsewhere += Relay;
        }
        return bridge;
    }

    private void Relay(bool obj, bool scene) => _changed?.Invoke(obj, scene);

    public async ValueTask DisposeAsync()
    {
        if (_bridge is { IsCompletedSuccessfully: true } done && done.Result is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
    }
}
