using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>
/// A model behind an OpenAI-compatible Chat Completions API (a local server such as Ollama, or a gateway): the
/// conversation as chat messages, the MCP tools as function definitions (<c>tool_choice: "auto"</c>), the reply streamed
/// with its <c>delta.content</c> and <c>delta.tool_calls</c> fragments put together by index, and each result sent back as
/// a <c>role: "tool"</c> message. Pictures go in a user message after the tool messages, since many local servers take
/// no images in a tool message; with <see cref="ModelSeesPictures"/> off they become a line of text instead.
/// </summary>
internal sealed class OpenAICompatibleBackend : IChatBackend
{
    private readonly List<JsonObject> _messages = [];
    private int _ids;

    public OpenAICompatibleClient? Client { get; set; }

    /// <summary>The model's name as the server knows it (<c>qwen3</c>, <c>llama3.1:8b</c>…).</summary>
    public string Model { get; set; } = "";

    /// <summary>Send only <see cref="ChatTools.Core"/> (small models choose better among fewer tools).</summary>
    public bool CoreToolsOnly { get; set; } = true;

    /// <summary>The system prompt (<see cref="ChatPrompt.System"/> unless the app gives its own).</summary>
    public string SystemPrompt { get; set; } = ChatPrompt.System;

    /// <summary>The core tool set's names (<see cref="ChatTools.Core"/> unless the app gives its own).</summary>
    public IReadOnlyList<string> CoreTools { get; set; } = ChatTools.Core;

    /// <summary>The note for a model given only the core tools (<see cref="ChatPrompt.CoreToolsNote"/> unless the app gives its own).</summary>
    public string CoreToolsNote { get; set; } = ChatPrompt.CoreToolsNote;

    /// <summary>Send pictures from tools to the model as images (it must be a vision model, on a server that takes them).</summary>
    public bool ModelSeesPictures { get; set; }

    /// <summary>The conversation as it will be sent, after the system message (for tests).</summary>
    public IReadOnlyList<JsonObject> Messages => _messages;

    public bool Ready => Client != null && Model.Trim().Length > 0;

    public bool EndsWithPerson => _messages.Count > 0 && (string?)_messages[^1]["role"] is "user" or "tool";

    public void AddUserText(string text) => _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

    public void Clear() => _messages.Clear();

    public void AddAssistant(string text, IReadOnlyList<ToolCall> calls)
    {
        if (text.Length == 0 && calls.Count == 0) return;
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
        if (calls.Count > 0)
            message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonSerializer.Serialize(c.Input) },
            }).ToArray());
        _messages.Add(message);
    }

    /// <summary>The request body: the system prompt, the conversation, the tools, streamed.</summary>
    internal JsonObject Request(IReadOnlyList<McpTool> tools)
    {
        var offered = CoreToolsOnly ? tools.Where(t => CoreTools.Contains(t.Name)).ToList() : tools;
        var system = SystemPrompt;
        if (CoreToolsOnly) system += "\n\n" + CoreToolsNote;
        if (!ModelSeesPictures) system += "\n\n" + ChatPrompt.NoPicturesNote;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in _messages) messages.Add(m.DeepClone());
        return new JsonObject
        {
            ["model"] = Model.Trim(),
            ["messages"] = messages,
            ["tools"] = ToolsFor(offered),
            ["tool_choice"] = "auto",
            ["stream"] = true,
        };
    }

    /// <summary>The MCP tools as Chat Completions function tools, their JSON schemas as the parameters.</summary>
    internal static JsonArray ToolsFor(IEnumerable<McpTool> definitions)
    {
        var list = new JsonArray();
        foreach (var d in definitions)
        {
            var schema = JsonNode.Parse(d.InputSchema.GetRawText()) as JsonObject ?? [];
            // Some servers insist on an object schema with properties, even for a tool that takes nothing.
            schema["type"] ??= "object";
            schema["properties"] ??= new JsonObject();
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = d.Name, ["description"] = d.Description ?? "", ["parameters"] = schema },
            });
        }
        return list;
    }

    /// <summary>A tool call being put together from its streamed fragments.</summary>
    private sealed class Partial
    {
        public string Id = "";
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Arguments = new();
    }

    public async Task<BackendReply> ReplyAsync(IReadOnlyList<McpTool> tools, Action<string> text, CancellationToken cancel)
    {
        var words = new StringBuilder();
        var calls = new SortedDictionary<int, Partial>();
        string? finish = null;
        await foreach (var chunk in Client!.StreamAsync(Request(tools), cancel).WithCancellation(cancel))
        {
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            // A streamed chunk has a delta; a whole answer (from a server that doesn't stream) has the message.
            var delta = choice["delta"] as JsonObject ?? choice["message"] as JsonObject;
            if (delta?["content"] is JsonValue content && content.GetValueKind() == JsonValueKind.String && (string)content! is { Length: > 0 } piece)
            {
                words.Append(piece);
                text(piece);
            }
            if (delta?["tool_calls"] is JsonArray fragments)
            {
                foreach (var f in fragments.OfType<JsonObject>())
                {
                    // By index; a server that gives none starts a new call with each id and adds to the last otherwise.
                    int index = f["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i
                        : f["id"] is JsonValue idv && ((string?)idv ?? "").Length > 0 || calls.Count == 0 ? calls.Count : calls.Keys.Max();
                    if (!calls.TryGetValue(index, out var call)) calls[index] = call = new Partial();
                    if (f["id"] is JsonValue id && (string?)id is { Length: > 0 } s) call.Id = s;
                    if (f["function"] is JsonObject fn)
                    {
                        if (fn["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String) call.Name.Append((string)n!);
                        // Arguments come as pieces of a JSON string; a few servers send the whole object instead.
                        if (fn["arguments"] is JsonValue a && a.GetValueKind() == JsonValueKind.String) call.Arguments.Append((string)a!);
                        else if (fn["arguments"] is JsonObject whole) call.Arguments.Append(whole.ToJsonString());
                    }
                }
            }
            if (choice["finish_reason"] is JsonValue fr && fr.GetValueKind() == JsonValueKind.String) finish = (string)fr!;
        }

        var parsed = new List<ToolCall>();
        var assistantCalls = new JsonArray();
        foreach (var call in calls.Values.Where(c => c.Name.Length > 0))
        {
            var id = call.Id.Length > 0 ? call.Id : $"call_{++_ids}";
            var args = call.Arguments.ToString().Trim();
            if (args.Length == 0) args = "{}";
            assistantCalls.Add(new JsonObject
            {
                ["id"] = id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name.ToString(), ["arguments"] = args },
            });
            parsed.Add(Parse(id, call.Name.ToString(), args));
        }
        // An empty reply isn't kept.
        if (words.Length > 0 || assistantCalls.Count > 0)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = words.Length > 0 ? words.ToString() : null };
            if (assistantCalls.Count > 0) message["tool_calls"] = assistantCalls;
            _messages.Add(message);
        }
        var end = finish switch
        {
            "length" when parsed.Count == 0 => ReplyEnd.MaxTokens,
            "content_filter" when parsed.Count == 0 => ReplyEnd.Refusal,
            _ => ReplyEnd.Done,
        };
        return new BackendReply(parsed, end);
    }

    /// <summary>A call's arguments as the bridge takes them; arguments that aren't a JSON object make it a problem.</summary>
    private static ToolCall Parse(string id, string name, string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolCall(id, name, new Dictionary<string, JsonElement>(), $"Not run: {name}'s arguments must be a JSON object, not {args}.");
            return new ToolCall(id, name, doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
        }
        catch (JsonException)
        {
            var shown = args.Length > 200 ? args[..200] + "…" : args;
            return new ToolCall(id, name, new Dictionary<string, JsonElement>(), $"Not run: {name}'s arguments weren't valid JSON ({shown}). Send them as one JSON object.");
        }
    }

    public void AddResults(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results)
    {
        var pictures = new List<(ToolCall Call, ToolImage Image)>();
        foreach (var (call, outcome) in results)
        {
            var content = outcome.Text.Length > 0 ? outcome.Text : outcome.Images.Count > 0 ? "" : "(no output)";
            if (outcome.IsError) content = "Error: " + content;
            if (outcome.Images.Count > 0)
            {
                var n = outcome.Images.Count == 1 ? "a picture" : $"{outcome.Images.Count} pictures";
                content += (content.Length > 0 ? "\n" : "") + (ModelSeesPictures
                    ? $"({n}, in the next message)"
                    : $"({n}, shown to the person in the chat; you can't see pictures here, so check your work with get_document and check_mesh)");
                if (ModelSeesPictures) pictures.AddRange(outcome.Images.Select(i => (call, i)));
            }
            _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = content });
        }
        if (pictures.Count == 0) return;
        var names = string.Join(", ", pictures.Select(p => p.Call).Distinct().Select(c => $"{c.Name} ({c.Id})"));
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"The pictures from {names}:" } };
        foreach (var (_, image) in pictures)
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
            });
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
    }
}
