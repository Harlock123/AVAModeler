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
public sealed class LiveBridge(string pipeName, string root, ServerProfile? profile = null) : IAsyncDisposable
{
    private readonly ServerProfile _profile = profile ?? ServerProfile.Modeler;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>How long to wait for the app to answer.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public const string NotRunning =
        "No AVA Modeler is taking AI agents just now. Start AVA Modeler and turn on Macros › Allow AI Agents (MCP) " +
        "(also in Edit › Preferences), then call again. To work on a model of your own without the app, run the server as " +
        "\"AVAModeler --mcp\" (without --attach).";

    /// <summary>
    /// The server options: the tools' definitions and the recipes (prompts), answered here, and calls passed on; so are
    /// the app's resources (read and subscribed to), a call's progress, and the app's questions for the person
    /// (elicitation), which go to the agent's client.
    /// </summary>
    public McpServerOptions Options() => new()
    {
        ServerInfo = ModelerServer.InfoFor(_profile),
        ServerInstructions = _profile.Instructions + "\n\n" + _profile.LiveNote,
        Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(), Prompts = new PromptsCapability(), Resources = new ResourcesCapability { Subscribe = true },
        },
        PromptCollection = [.. ModelerServer.Prompts(_profile)],
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ModelerServer.Definitions(_profile) }),
            CallToolHandler = (request, cancel) => CallAsync(request.Params ?? new CallToolRequestParams { Name = "" }, cancel, request.Server),
            ListResourcesHandler = (request, cancel) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = [.. ModelerTools.ResourceList.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = "application/json" })],
            }),
            ReadResourceHandler = async (request, cancel) =>
                await (await LiveClientAsync(request.Server, cancel).ConfigureAwait(false))
                    .ReadResourceAsync(request.Params?.Uri ?? throw new McpException("Give the resource's uri."), cancellationToken: cancel).ConfigureAwait(false),
            SubscribeToResourcesHandler = async (request, cancel) =>
            {
                var uri = request.Params?.Uri ?? throw new McpException("Give the resource's uri.");
                lock (_subscribed) _subscribed.Add(uri);
                await (await LiveClientAsync(request.Server, cancel).ConfigureAwait(false)).SubscribeToResourceAsync(uri, cancellationToken: cancel).ConfigureAwait(false);
                return new EmptyResult();
            },
            UnsubscribeFromResourcesHandler = (request, cancel) =>
            {
                lock (_subscribed) _subscribed.Remove(request.Params?.Uri ?? "");
                return ValueTask.FromResult(new EmptyResult());
            },
        },
    };

    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);

    /// <summary>The agent's side (the server it reached the bridge through), for questions and notices from the app.</summary>
    private McpServer? _outer;

    /// <summary>The app's client for a resource request, or an MCP error saying how to start live mode.</summary>
    private async Task<McpClient> LiveClientAsync(McpServer outer, CancellationToken cancel)
    {
        try { return await ClientAsync(cancel, outer).ConfigureAwait(false); }
        catch (LiveConnectionException e) { throw new McpException(e.Message.Replace(NotRunning, _profile.NotRunning, StringComparison.Ordinal)); }
    }

    /// <summary>Passes a tool call to the app, connecting first if need be (its progress comes back to the agent).</summary>
    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams call, CancellationToken cancel, McpServer? outer = null)
    {
        McpClient client;
        try
        {
            client = await ClientAsync(cancel, outer).ConfigureAwait(false);
        }
        catch (LiveConnectionException e)
        {
            return Error(e.Message.Replace(NotRunning, _profile.NotRunning, StringComparison.Ordinal));
        }
        try
        {
            if (outer != null && call.ProgressToken is { } token)
            {
                var args = call.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                return await client.CallToolAsync(call.Name, args, new Forward(outer, token), cancellationToken: cancel).ConfigureAwait(false);
            }
            return await client.CallToolAsync(call, cancel).ConfigureAwait(false);
        }
        catch (McpProtocolException e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or ClientTransportClosedException or McpException)
        {
            await DropAsync().ConfigureAwait(false);
            return Error($"The connection to {_profile.Title} was lost ({e.Message}). If the app is still open with AI agents allowed, call again to reconnect.");
        }
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    /// <summary>Sends the app's progress for a call on to the agent, under the agent's token.</summary>
    private sealed class Forward(McpServer outer, ProgressToken token) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) =>
            _ = outer.SendNotificationAsync(NotificationMethods.ProgressNotification, new ProgressNotificationParams { ProgressToken = token, Progress = value });
    }

    private async Task<McpClient> ClientAsync(CancellationToken cancel, McpServer? outer = null)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (outer != null) _outer = outer;
            if (_client is { } c && !c.Completion.IsCompleted) return c;
            if (_client != null) await DropUnlockedAsync().ConfigureAwait(false);
            _client = await ConnectAsync(pipeName, Root, ConnectTimeout, cancel, ClientOptions()).ConfigureAwait(false);
            // A client on the 2026-07-28 revision listens (subscriptions/listen) rather than subscribing: hear about
            // everything, and the SDK delivers each notice to whoever listens for it.
            bool listens = _outer?.NegotiatedProtocolVersion is { } v && string.CompareOrdinal(v, "2026-07-28") >= 0;
            List<string> uris;
            lock (_subscribed) uris = listens ? [.. ModelerTools.ResourceList.Select(r => r.Uri)] : [.. _subscribed];
            foreach (var uri in uris) await _client.SubscribeToResourceAsync(uri, cancellationToken: cancel).ConfigureAwait(false);
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
    /// The bridge's client to the app: named after the agent's own (so the app's session list says who it is), on the
    /// revision with resources/subscribe, asking the person through the agent's client when it can, and passing the
    /// app's resource notices on.
    /// </summary>
    private McpClientOptions ClientOptions()
    {
        var name = _outer?.ClientInfo?.Name is { Length: > 0 } n ? $"{n} (through the bridge)" : "AVAModeler-bridge";
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = name, Version = ModelerServer.Info.Version },
            ProtocolVersion = "2025-11-25",
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new(NotificationMethods.ResourceUpdatedNotification, async (notification, cancel) =>
                    {
                        if (_outer is { } outer && notification.Params is { } p)
                            await outer.SendNotificationAsync(NotificationMethods.ResourceUpdatedNotification, p, cancellationToken: cancel).ConfigureAwait(false);
                    }),
                ],
            },
        };
        if (_outer?.ClientCapabilities?.Elicitation != null)
        {
            options.Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() };
            options.Handlers.ElicitationHandler = async (request, cancel) =>
                _outer is { } outer && request != null
                    ? await outer.ElicitAsync(request, cancel).ConfigureAwait(false)
                    : new ElicitResult { Action = "cancel" };
        }
        return options;
    }

    /// <summary>
    /// Opens a pipe to the live app, does the handshake for <paramref name="root"/>, and starts an MCP client session
    /// over it. Throws <see cref="LiveConnectionException"/> with an explanation if there's no app or it refuses.
    /// </summary>
    public static async Task<McpClient> ConnectAsync(string pipeName, string root, TimeSpan timeout, CancellationToken cancel = default, McpClientOptions? options = null)
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
            return await McpClient.CreateAsync(transport, options ?? new McpClientOptions { ClientInfo = new Implementation { Name = "AVAModeler-bridge", Version = ModelerServer.Info.Version } }, null, cancel).ConfigureAwait(false);
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
