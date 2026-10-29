using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp.Tests;

/// <summary>A folder of its own for a test, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avamodeler-mcp-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}

/// <summary>One end of an in-process byte stream pair: what one side writes, the other reads.</summary>
internal sealed class Wire : IDisposable
{
    private readonly AnonymousPipeServerStream _write = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _read;

    public Wire() => _read = new AnonymousPipeClientStream(PipeDirection.In, _write.ClientSafePipeHandle);

    public Stream Writer => _write;
    public Stream Reader => _read;

    public void Dispose()
    {
        _write.Dispose();
        _read.Dispose();
    }
}

/// <summary>An MCP client talking to a server in the same process, over a pair of in-memory pipes.</summary>
internal sealed class InProcess : IAsyncDisposable
{
    private readonly Wire _up = new(), _down = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _server = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;

    public static Task<InProcess> Start(IModelerHost host) => Start(ModelerServer.Options(host));

    public static async Task<InProcess> Start(McpServerOptions options)
    {
        var s = new InProcess();
        var transport = new StreamServerTransport(s._up.Reader, s._down.Writer, "test");
        var server = McpServer.Create(transport, options);
        s._server = Task.Run(async () =>
        {
            await using (server) await server.RunAsync(s._stop.Token);
        });
        s.Client = await McpClient.CreateAsync(new StreamClientTransport(s._up.Writer, s._down.Reader), null, null, TestContext.Current.CancellationToken);
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
        _up.Dispose();
        _down.Dispose();
    }
}

internal static class Calls
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Calls a tool and returns its JSON answer, failing the test on an error result.</summary>
    public static async Task<JsonObject> Json(this McpClient client, string tool, object? args = null)
    {
        var result = await client.Call(tool, args);
        Assert.True(result.IsError != true, $"{tool} failed: {Text(result)}");
        return (JsonObject)JsonNode.Parse(Text(result))!;
    }

    public static async Task<CallToolResult> Call(this McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args, JsonSerializerOptions.Default).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return await client.CallToolAsync(tool, dict, cancellationToken: Cancel);
    }

    /// <summary>Calls a tool that should fail, and returns its message.</summary>
    public static async Task<string> Fails(this McpClient client, string tool, object? args = null)
    {
        var result = await client.Call(tool, args);
        Assert.True(result.IsError == true, $"{tool} should have failed but said: {Text(result)}");
        return Text(result);
    }

    public static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>A handler's JSON answer.</summary>
    public static async Task<JsonObject> Parse(Task<string> call) => (JsonObject)JsonNode.Parse(await call)!;

    /// <summary>The message of a handler call that should fail as an MCP tool error.</summary>
    public static async Task<string> Error<T>(Task<T> call) => (await Assert.ThrowsAsync<McpException>(async () => await call)).Message;
}

internal static class Pngs
{
    /// <summary>Decodes a PNG and counts how many different colours it has (a blank picture has one or two).</summary>
    public static (int Width, int Height, int Colours) Inspect(byte[] png)
    {
        Assert.True(png.Length > 8 && png[0] == 0x89 && png[1] == 'P', "not a PNG");
        var image = TextureImages.Decode(png) ?? throw new InvalidOperationException("The PNG didn't decode.");
        var colours = new HashSet<int>();
        for (int i = 0; i < image.Rgba.Length; i += 4) colours.Add(image.Rgba[i] << 16 | image.Rgba[i + 1] << 8 | image.Rgba[i + 2]);
        return (image.Width, image.Height, colours.Count);
    }

    public static byte[] Of(CallToolResult r) => Assert.Single(r.Content.OfType<ImageContentBlock>()).DecodedData.ToArray();
}

/// <summary>A headless host with a fresh document, and the handlers bound to it.</summary>
internal sealed class Bench : IDisposable
{
    public TempFolder Folder { get; } = new();
    public HeadlessHost Host { get; }
    public ModelerTools Tools { get; }
    public Commands Commands => Host.Commands;
    public Document Doc => Host.Commands.Document;
    public AVAModeler.Layout.LayoutSession Layout => Host.Layout;
    public AVAModeler.Scene.Scene Scene => Host.Layout.Scene;

    public Bench(ToolRegistry? registry = null)
    {
        Host = new HeadlessHost(Folder.Path, registry);
        Tools = new ModelerTools(Host);
    }

    public static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.SerializeToElement(o, JsonSerializerOptions.Default).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    public void Dispose() => Folder.Dispose();
}
