using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Mcp;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// A stand-in for an OpenAI-compatible model server (Ollama's <c>/v1</c>, LM Studio, vLLM, a gateway): an
/// <see cref="HttpMessageHandler"/> that answers each chat request with the next scripted reply, streamed as server-sent
/// events in small pieces (tool names and arguments split across chunks, several calls interleaved), and keeps every
/// request to look at. Nothing leaves the process; no real server is ever called.
/// </summary>
internal sealed class FakeModelServer(string baseUrl = FakeModelServer.Local) : HttpMessageHandler
{
    public const string Local = "http://localhost:11434/v1";

    /// <summary>A made-up gateway key.</summary>
    public const string Key = "sk-gateway-made-up-0000000000-QRST";

    private readonly Queue<Func<JsonObject, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public string BaseUrl { get; } = baseUrl;

    public List<JsonObject> Requests { get; } = [];

    /// <summary>Each request's Authorization header, as sent (null when there was none).</summary>
    public List<string?> Authorization { get; } = [];

    /// <summary>The model list GET /models answers with (null: the server has no such endpoint, 404).</summary>
    public string? ModelsJson { get; set; }

    public int ModelRequests { get; private set; }

    public OpenAICompatibleClient Client(string? key = null) => new(new HttpClient(this), BaseUrl, key);

    public FakeModelServer Reply(params Part[] parts) => Reply("", parts);

    /// <summary>A usage object for the replies scripted after it's set, sent in a chunk of its own at the end (null: none, as some servers).</summary>
    public JsonObject? Usage { get; set; }

    /// <summary>A reply ending with the given finish reason ("" for the usual: tool_calls or stop).</summary>
    public FakeModelServer Reply(string finish, params Part[] parts)
    {
        var usage = Usage?.DeepClone();
        _script.Enqueue((_, _) => Task.FromResult(Stream(parts, finish, usage as JsonObject)));
        return this;
    }

    /// <summary>A reply that isn't streamed: the whole answer as one JSON object (some servers do this).</summary>
    public FakeModelServer Whole(string text)
    {
        _script.Enqueue((_, _) =>
        {
            var body = new JsonObject
            {
                ["id"] = "chatcmpl-whole", ["object"] = "chat.completion",
                ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text }, ["finish_reason"] = "stop" } },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        });
        return this;
    }

    public FakeModelServer Error(int status, string body)
    {
        _script.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        return this;
    }

    /// <summary>Nothing listening: the connection is refused, as when the server isn't running.</summary>
    public FakeModelServer Refuse()
    {
        _script.Enqueue((_, _) => throw new HttpRequestException("Connection refused (localhost:11434)", new SocketException((int)SocketError.ConnectionRefused)));
        return this;
    }

    /// <summary>A reply that never comes: the request hangs until it's cancelled.</summary>
    public FakeModelServer Hang()
    {
        _script.Enqueue(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    /// <summary>A reply that streams some words, then hangs until it's cancelled.</summary>
    public FakeModelServer StartThenHang(string words)
    {
        _script.Enqueue((_, cancel) =>
        {
            var content = new HangingContent(Encoding.UTF8.GetBytes(Event(Delta(new JsonObject { ["content"] = words }))), cancel);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        return this;
    }

    public int Left => _script.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        Authorization.Add(request.Headers.Authorization?.ToString());
        if (request.Method == HttpMethod.Get)
        {
            Assert.Equal(BaseUrl + "/models", url);
            ModelRequests++;
            return ModelsJson == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson, Encoding.UTF8, "application/json") };
        }
        Assert.Equal(BaseUrl + "/chat/completions", url);
        var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancel))!;
        Requests.Add(body);
        Assert.True(_script.Count > 0, "The chat sent a request the test didn't script.");
        return await _script.Dequeue()(body, cancel);
    }

    // ───────────────────────────── the reply as server-sent events ─────────────────────────────

    public abstract record Part;
    public sealed record Text(string Value) : Part;
    public sealed record Use(string Id, string Name, string Arguments) : Part;

    public static Part Say(string text) => new Text(text);
    public static Part Call(string id, string name, object input) => new Use(id, name, JsonSerializer.Serialize(input));

    /// <summary>A call whose arguments are sent exactly as given (e.g. not JSON at all).</summary>
    public static Part RawCall(string id, string name, string arguments) => new Use(id, name, arguments);

    private static JsonObject Delta(JsonObject delta, string? finish = null) => new()
    {
        ["id"] = "chatcmpl-fake", ["object"] = "chat.completion.chunk", ["model"] = "qwen3",
        ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish } },
    };

    private static string Event(JsonObject data) => "data: " + data.ToJsonString() + "\n\n";

    private static HttpResponseMessage Stream(Part[] parts, string finish, JsonObject? usage)
    {
        var sse = new StringBuilder();
        sse.Append(": a comment line, which a client skips\n\n");
        sse.Append(Event(Delta(new JsonObject { ["role"] = "assistant", ["content"] = "" })));
        foreach (var t in parts.OfType<Text>())
        {
            // In two pieces, as a real stream comes.
            int half = t.Value.Length / 2;
            foreach (var piece in new[] { t.Value[..half], t.Value[half..] })
                sse.Append(Event(Delta(new JsonObject { ["content"] = piece })));
        }
        // Each call starts with its id and the first part of its name; the rest of the name, then the arguments in three
        // pieces, follow, the calls' fragments interleaved, so only the index says which call a fragment belongs to.
        var uses = parts.OfType<Use>().ToList();
        static JsonObject Fragment(int index, JsonObject function, string? id = null)
        {
            var f = new JsonObject { ["index"] = index };
            if (id != null) { f["id"] = id; f["type"] = "function"; }
            f["function"] = function;
            return f;
        }
        for (int i = 0; i < uses.Count; i++)
        {
            int cut = Math.Max(1, uses[i].Name.Length / 2);
            sse.Append(Event(Delta(new JsonObject { ["tool_calls"] = new JsonArray { Fragment(i, new JsonObject { ["name"] = uses[i].Name[..cut], ["arguments"] = "" }, uses[i].Id) } })));
        }
        for (int i = 0; i < uses.Count; i++)
        {
            int cut = Math.Max(1, uses[i].Name.Length / 2);
            sse.Append(Event(Delta(new JsonObject { ["tool_calls"] = new JsonArray { Fragment(i, new JsonObject { ["name"] = uses[i].Name[cut..] }) } })));
        }
        for (int piece = 0; piece < 3; piece++)
            for (int i = 0; i < uses.Count; i++)
            {
                var a = uses[i].Arguments;
                int from = a.Length * piece / 3, to = a.Length * (piece + 1) / 3;
                sse.Append(Event(Delta(new JsonObject { ["tool_calls"] = new JsonArray { Fragment(i, new JsonObject { ["arguments"] = a[from..to] }) } })));
            }
        sse.Append(Event(Delta(new JsonObject(), finish.Length > 0 ? finish : uses.Count > 0 ? "tool_calls" : "stop")));
        if (usage != null) sse.Append(Event(new JsonObject { ["id"] = "chatcmpl-fake", ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(), ["usage"] = usage }));
        sse.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    /// <summary>A body that sends its first bytes, then waits for the request to be cancelled.</summary>
    private sealed class HangingContent(byte[] first, CancellationToken cancel) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(first, cancel);
            await stream.FlushAsync(cancel);
            await Task.Delay(Timeout.Infinite, cancel);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            var pipe = new System.IO.Pipelines.Pipe();
            await pipe.Writer.WriteAsync(first, cancel);
            await pipe.Writer.FlushAsync(cancel);
            return pipe.Reader.AsStream();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>A chat on a headless modeler and the fake OpenAI-compatible server, model "qwen3".</summary>
internal sealed class ServerRig : IAsyncDisposable
{
    public required HeadlessHost Host { get; init; }
    public required McpToolBridge Bridge { get; init; }
    public required ChatSession Chat { get; init; }
    public required FakeModelServer Server { get; init; }

    public static async Task<ServerRig> Start(FakeModelServer? server = null, bool coreTools = false, bool pictures = true)
    {
        server ??= new FakeModelServer();
        var host = new HeadlessHost(Path.GetTempPath());
        var bridge = await McpToolBridge.StartAsync(host, TestContext.Current.CancellationToken);
        var chat = new ChatSession(bridge)
        {
            Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen3",
            CoreToolsOnly = coreTools, ModelSeesPictures = pictures,
        };
        return new ServerRig { Host = host, Bridge = bridge, Server = server, Chat = chat };
    }

    public Task Send(string text) => Chat.SendAsync(text, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Bridge.DisposeAsync();
}
