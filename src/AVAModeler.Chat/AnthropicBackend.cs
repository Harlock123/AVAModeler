using Anthropic;
using Anthropic.Models.Messages;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>
/// Claude through the Messages API on the official SDK: the conversation as content blocks, streamed requests with the
/// MCP server's tools, pictures back as image blocks inside each <c>tool_result</c>, failures as <c>is_error</c>, and
/// prompt-cache breakpoints on the system prompt, the last tool and the conversation.
/// </summary>
internal sealed class AnthropicBackend : IChatBackend
{
    private readonly List<(bool User, List<ContentBlockParam> Blocks)> _turns = [];

    /// <summary>The Claude API client, or null while there's no key.</summary>
    public IAnthropicClient? Client { get; set; }

    public string Model { get; set; } = ChatModels.Default;

    public long MaxTokens { get; set; } = 32000;

    /// <summary>The conversation as it will be sent: each turn (the person's or Claude's) and its blocks.</summary>
    public IReadOnlyList<(bool User, List<ContentBlockParam> Blocks)> Turns => _turns;

    public bool Ready => Client != null;

    public bool EndsWithPerson => _turns.Count > 0 && _turns[^1].User;

    public void AddUserText(string text) => AddToUserTurn(new TextBlockParam { Text = text });

    public void Clear() => _turns.Clear();

    public void AddAssistant(string text, IReadOnlyList<ToolCall> calls)
    {
        var blocks = new List<ContentBlockParam>();
        if (text.Length > 0) blocks.Add(new TextBlockParam { Text = text });
        foreach (var c in calls) blocks.Add(new ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = c.Input });
        if (blocks.Count > 0) _turns.Add((false, blocks));
    }

    /// <summary>Adds blocks to the conversation's last user turn, or starts one (turns must alternate).</summary>
    private void AddToUserTurn(params ContentBlockParam[] blocks)
    {
        if (_turns.Count > 0 && _turns[^1].User) _turns[^1].Blocks.AddRange(blocks);
        else _turns.Add((true, [.. blocks]));
    }

    public async Task<BackendReply> ReplyAsync(IReadOnlyList<McpTool> tools, Action<string> text, CancellationToken cancel)
    {
        // Text shows as it streams; the whole reply is put together from the same events afterwards, by the SDK.
        var events = new List<RawMessageStreamEvent>();
        await foreach (var ev in Client!.Messages.CreateStreaming(Request(tools), cancel).WithCancellation(cancel))
        {
            events.Add(ev);
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t)) text(t.Text);
        }
        var message = await events.ToAsyncEnumerable().Aggregate();

        // An empty reply (a refusal can be one) isn't kept: the API takes no empty turns.
        var assistant = AssistantBlocks(message);
        if (assistant.Count > 0) _turns.Add((false, assistant));
        var calls = message.Content.Select(b => b.TryPickToolUse(out var u) ? u : null).OfType<ToolUseBlock>()
            .Select(u => new ToolCall(u.ID, u.Name, u.Input)).ToList();
        var end = message.StopReason?.Value() switch
        {
            StopReason.MaxTokens => ReplyEnd.MaxTokens,
            StopReason.Refusal => ReplyEnd.Refusal,
            _ => ReplyEnd.Done,
        };
        return new BackendReply(calls, end);
    }

    public void AddResults(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results) =>
        AddToUserTurn([.. results.Select(r => (ContentBlockParam)Result(r.Call.Id, r.Outcome))]);

    /// <summary>
    /// The request: the system prompt and the tools marked for prompt caching (the tool schemas are most of every
    /// request), and automatic caching of the conversation so far.
    /// </summary>
    internal MessageCreateParams Request(IReadOnlyList<McpTool> tools) => new()
    {
        Model = Model,
        MaxTokens = MaxTokens,
        System = new List<TextBlockParam> { new() { Text = ChatPrompt.System, CacheControl = new CacheControlEphemeral() } },
        Tools = ToolsFor(tools),
        Messages = _turns.Select(t => t.User ? (MessageParam)new UserMessageParam { Content = t.Blocks.ToList() } : new AssistantMessageParam { Content = t.Blocks.ToList() }).ToList(),
        CacheControl = new CacheControlEphemeral(),
    };

    /// <summary>The MCP tools as Messages API tools, schemas unchanged; the last one carries the cache breakpoint.</summary>
    internal static List<ToolUnion> ToolsFor(IReadOnlyList<McpTool> definitions)
    {
        var list = new List<ToolUnion>();
        for (int i = 0; i < definitions.Count; i++)
        {
            var d = definitions[i];
            var schema = d.InputSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var tool = new Tool { Name = d.Name, Description = d.Description ?? "", InputSchema = InputSchema.FromRawUnchecked(schema) };
            // Set only on the last: a property set to null would still be sent (as null).
            if (i == definitions.Count - 1) tool = tool with { CacheControl = new CacheControlEphemeral() };
            list.Add(tool);
        }
        return list;
    }

    /// <summary>Claude's reply as it goes back in the next request: text, tool calls and thinking, unchanged.</summary>
    private static List<ContentBlockParam> AssistantBlocks(Message message)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var b in message.Content)
        {
            if (b.TryPickText(out var text)) { if (text.Text.Length > 0) blocks.Add(new TextBlockParam { Text = text.Text }); }
            else if (b.TryPickThinking(out var thinking)) blocks.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (b.TryPickRedactedThinking(out var redacted)) blocks.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            else if (b.TryPickToolUse(out var use)) blocks.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
        }
        return blocks;
    }

    /// <summary>A tool's outcome as a tool_result: its pictures as image blocks, then its text; failures marked is_error.</summary>
    internal static ToolResultBlockParam Result(string id, ToolOutcome outcome)
    {
        var content = new List<Block>();
        foreach (var image in outcome.Images)
            content.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(image.Data), MediaType = MediaTypeOf(image.MediaType) } });
        if (outcome.Text.Length > 0 || content.Count == 0) content.Add(new TextBlockParam { Text = outcome.Text.Length > 0 ? outcome.Text : "(no output)" });
        var result = new ToolResultBlockParam { ToolUseID = id, Content = content };
        if (outcome.IsError) result = result with { IsError = true };
        return result;
    }

    private static MediaType MediaTypeOf(string mime) => mime switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImagePng,
    };
}
