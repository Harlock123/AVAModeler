using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using AVAModeler.Mcp;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// A stand-in for the Anthropic Messages API: an <see cref="HttpMessageHandler"/> that answers each request with the
/// next scripted reply (as a server-sent event stream, or an error) and keeps every request body to look at. Nothing
/// leaves the process, and the key it's given is a made-up one.
/// </summary>
internal sealed class FakeClaude : HttpMessageHandler
{
    public const string Key = "sk-ant-test-made-up-0000000000-WXYZ";

    private readonly Queue<Func<JsonObject, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<JsonObject> Requests { get; } = [];
    public List<HttpRequestMessage> Headers { get; } = [];

    /// <summary>A client on this fake, with no retries (so error tests are quick).</summary>
    public AnthropicClient Client() => new() { ApiKey = Key, HttpClient = new HttpClient(this), MaxRetries = 0 };

    public FakeClaude Reply(params Part[] parts)
    {
        _script.Enqueue((_, _) => Task.FromResult(Stream(parts)));
        return this;
    }

    /// <summary>A reply worked out from the request (e.g. to echo what it was sent).</summary>
    public FakeClaude Reply(Func<JsonObject, Part[]> parts)
    {
        _script.Enqueue((body, _) => Task.FromResult(Stream(parts(body))));
        return this;
    }

    public FakeClaude Error(int status, string type, string message)
    {
        _script.Enqueue((_, _) =>
        {
            var body = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        });
        return this;
    }

    /// <summary>A reply that never comes: the request hangs until it's cancelled.</summary>
    public FakeClaude Hang()
    {
        _script.Enqueue(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public int Left => _script.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.GetLeftPart(UriPartial.Path));
        var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancel))!;
        Requests.Add(body);
        Headers.Add(request);
        Assert.True(_script.Count > 0, "The chat sent a request the test didn't script.");
        return await _script.Dequeue()(body, cancel);
    }

    // ───────────────────────────── the reply as server-sent events ─────────────────────────────

    public abstract record Part;
    public sealed record Text(string Value) : Part;
    public sealed record Use(string Id, string Name, object Input) : Part;
    public sealed record Thinking(string Signature) : Part;

    public static Part Say(string text) => new Text(text);
    public static Part Call(string id, string name, object input) => new Use(id, name, input);

    private static int _messages;

    private static HttpResponseMessage Stream(Part[] parts)
    {
        var sse = new StringBuilder();
        void Event(string type, JsonObject data)
        {
            data["type"] = type;
            sse.Append("event: ").Append(type).Append('\n').Append("data: ").Append(data.ToJsonString()).Append("\n\n");
        }
        Event("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_{Interlocked.Increment(ref _messages)}", ["type"] = "message", ["role"] = "assistant", ["model"] = ChatModels.Default,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 1 },
            },
        });
        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case Text t:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
                    // In two pieces, as a real stream comes.
                    int half = t.Value.Length / 2;
                    foreach (var piece in new[] { t.Value[..half], t.Value[half..] })
                        Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = piece } });
                    break;
                case Use u:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = new JsonObject() } });
                    Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = JsonSerializer.Serialize(u.Input) } });
                    break;
                case Thinking th:
                    Event("content_block_start", new JsonObject { ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" } });
                    Event("content_block_delta", new JsonObject { ["index"] = i, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = th.Signature } });
                    break;
            }
            Event("content_block_stop", new JsonObject { ["index"] = i });
        }
        bool tools = parts.OfType<Use>().Any();
        Event("message_delta", new JsonObject
        {
            ["delta"] = new JsonObject { ["stop_reason"] = tools ? "tool_use" : "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = 20 },
        });
        Event("message_stop", new JsonObject());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}

/// <summary>A chat on a headless modeler (a document and scene of its own) and the fake API.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public required HeadlessHost Host { get; init; }
    public required McpToolBridge Bridge { get; init; }
    public required ChatSession Chat { get; init; }
    public required FakeClaude Api { get; init; }

    public static async Task<Rig> Start(FakeClaude? api = null)
    {
        api ??= new FakeClaude();
        var host = new HeadlessHost(Path.GetTempPath());
        var bridge = await McpToolBridge.StartAsync(host, TestContext.Current.CancellationToken);
        return new Rig { Host = host, Bridge = bridge, Api = api, Chat = new ChatSession(bridge) { Client = api.Client() } };
    }

    public Task Send(string text) => Chat.SendAsync(text, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Bridge.DisposeAsync();
}

internal static class Json
{
    /// <summary>The blocks of a request's last message.</summary>
    public static JsonArray LastBlocks(JsonObject request) => (JsonArray)request["messages"]!.AsArray()[^1]!["content"]!;

    public static IEnumerable<JsonObject> Results(JsonObject request) =>
        request["messages"]!.AsArray().SelectMany(m => m!["content"] is JsonArray a ? a.OfType<JsonObject>() : []).Where(b => (string?)b["type"] == "tool_result");
}
