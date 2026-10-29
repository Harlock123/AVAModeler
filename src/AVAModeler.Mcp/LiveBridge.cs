using System.IO.Pipes;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// <c>AVAModeler --mcp --attach</c>: an MCP server on stdio that passes each tool call on to the running app through
/// its pipe (<see cref="LiveListener"/>). It lists the same tools as the app (they're defined in this same program),
/// connects on the first call rather than at start, and reconnects after the app restarts, so an agent can be set up
/// before the app is open. With no app to reach, a call fails with a message saying how to turn live mode on.
/// </summary>
public sealed class LiveBridge(string pipeName, string root) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>How long to wait for the app to answer.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public const string NotRunning =
        "No AVA Modeler is taking AI agents just now. Start AVA Modeler and turn on Macros › Allow AI Agents (MCP) " +
        "(also in Edit › Preferences), then call again. To work on a model of your own without the app, run the server as " +
        "\"AVAModeler --mcp\" (without --attach).";

    /// <summary>The server options: the tools' definitions and the recipes (prompts), answered here, and calls passed on.</summary>
    public McpServerOptions Options() => new()
    {
        ServerInfo = ModelerServer.Info,
        ServerInstructions = ModelerServer.Instructions + "\n\nThis is the live app: the person using AVA Modeler sees every change in its views, and their own edits share the same document and undo history.",
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability(), Prompts = new PromptsCapability() },
        PromptCollection = [.. ModelerServer.Prompts()],
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ModelerServer.Definitions() }),
            CallToolHandler = (request, cancel) => CallAsync(request.Params ?? new CallToolRequestParams { Name = "" }, cancel),
        },
    };

    /// <summary>Passes a tool call to the app, connecting first if need be.</summary>
    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams call, CancellationToken cancel)
    {
        McpClient client;
        try
        {
            client = await ClientAsync(cancel).ConfigureAwait(false);
        }
        catch (LiveConnectionException e)
        {
            return Error(e.Message);
        }
        try
        {
            return await client.CallToolAsync(call, cancel).ConfigureAwait(false);
        }
        catch (McpProtocolException e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or ClientTransportClosedException or McpException)
        {
            await DropAsync().ConfigureAwait(false);
            return Error($"The connection to AVA Modeler was lost ({e.Message}). If the app is still open with AI agents allowed, call again to reconnect.");
        }
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private async Task<McpClient> ClientAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_client is { } c && !c.Completion.IsCompleted) return c;
            if (_client != null) await DropUnlockedAsync().ConfigureAwait(false);
            _client = await ConnectAsync(pipeName, Root, ConnectTimeout, cancel).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DropUnlockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DropUnlockedAsync()
    {
        var c = _client;
        _client = null;
        if (c == null) return;
        try { await c.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>
    /// Opens a pipe to the live app, does the handshake for <paramref name="root"/>, and starts an MCP client session
    /// over it. Throws <see cref="LiveConnectionException"/> with an explanation if there's no app or it refuses.
    /// </summary>
    public static async Task<McpClient> ConnectAsync(string pipeName, string root, TimeSpan timeout, CancellationToken cancel = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                wait.CancelAfter(timeout);
                try
                {
                    await pipe.ConnectAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new LiveConnectionException(NotRunning);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    throw new LiveConnectionException($"{NotRunning} ({e.Message})");
                }
                await LiveProtocol.WriteLineAsync(pipe, LiveProtocol.Hello(Path.GetFullPath(root)), wait.Token).ConfigureAwait(false);
                string answer;
                try
                {
                    answer = await LiveProtocol.ReadLineAsync(pipe, wait.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException && !cancel.IsCancellationRequested)
                {
                    throw new LiveConnectionException($"AVA Modeler didn't answer the handshake ({e.Message}).");
                }
                var o = JsonNode.Parse(answer) as JsonObject;
                if ((string?)o?["error"] is { } refusal) throw new LiveConnectionException($"AVA Modeler refused the connection: {refusal}");
                if ((bool?)o?["ok"] != true) throw new LiveConnectionException("AVA Modeler gave an answer the bridge doesn't understand.");
            }
            var transport = new StreamClientTransport(pipe, pipe);
            return await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "AVAModeler-bridge", Version = ModelerServer.Info.Version } }, null, cancel).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>The bridge couldn't reach a live app; the message says why and what to do.</summary>
public sealed class LiveConnectionException(string message) : Exception(message);
