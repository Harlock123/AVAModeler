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

    private McpToolBridge() { }

    /// <summary>Starts the server over <paramref name="host"/> and connects to it.</summary>
    public static async Task<McpToolBridge> StartAsync(IModelerHost host, CancellationToken cancel = default)
    {
        var bridge = new McpToolBridge();
        var transport = new StreamServerTransport(bridge._up.Reader.AsStream(), bridge._down.Writer.AsStream(), "chat");
        var server = McpServer.Create(transport, ModelerServer.Options(host));
        bridge._server = Task.Run(async () =>
        {
            await using (server) await server.RunAsync(bridge._stop.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        var options = new McpClientOptions { ClientInfo = new Implementation { Name = "AVAModeler Chat", Version = ModelerServer.Info.Version } };
        bridge._client = await McpClient.CreateAsync(new StreamClientTransport(bridge._up.Writer.AsStream(), bridge._down.Reader.AsStream()), options, null, cancel).ConfigureAwait(false);
        return bridge;
    }

    /// <summary>The instructions the server gives every agent (the chat's system prompt starts with them).</summary>
    public string? ServerInstructions => _client.ServerInstructions;

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancel = default) =>
        _tools ??= (await _client.ListToolsAsync(cancellationToken: cancel).ConfigureAwait(false)).Select(t => t.ProtocolTool).ToList();

    public async Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancel = default)
    {
        var args = input.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Clone());
        try
        {
            var result = await _client.CallToolAsync(name, args, cancellationToken: cancel).ConfigureAwait(false);
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
        await (await Bridge(cancel).WaitAsync(cancel).ConfigureAwait(false)).ListToolsAsync(cancel).ConfigureAwait(false);

    public async Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancel = default) =>
        await (await Bridge(cancel).WaitAsync(cancel).ConfigureAwait(false)).CallAsync(name, input, cancel).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_bridge is { IsCompletedSuccessfully: true } done && done.Result is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
    }
}
