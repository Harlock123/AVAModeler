using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>
/// One chat with Claude: the conversation kept for the session, and the agent loop. A message goes to the Messages API
/// (streamed, so text shows as it's written) with the MCP server's tools; each <c>tool_use</c> runs through the
/// <see cref="IToolBridge"/> and its result goes back (pictures as image blocks, failures as <c>is_error</c>), until
/// Claude stops, the step cap is reached, or the person presses Stop.
/// </summary>
/// <remarks>
/// It runs its continuations on the caller's context (no <c>ConfigureAwait(false)</c>), so in the app every entry and
/// event changes on the UI thread. The HTTP traffic and the tools' work happen elsewhere and don't block it.
/// </remarks>
public sealed class ChatSession(IToolBridge tools)
{
    private readonly List<ChatEntry> _entries = [];
    private readonly List<(bool User, List<ContentBlockParam> Blocks)> _turns = [];
    private CancellationTokenSource? _running;
    private int _generation;
    private IReadOnlyList<McpTool>? _toolDefinitions;

    /// <summary>The Claude API client, or null while there's no key.</summary>
    public IAnthropicClient? Client { get; set; }

    /// <summary>The model the next request uses (one of <see cref="ChatModels.All"/>).</summary>
    public string Model { get; set; } = ChatModels.Default;

    /// <summary>The most tool calls one request may make before the chat stops and says so.</summary>
    public int MaxToolCalls { get; set; } = 60;

    public long MaxTokens { get; set; } = 32000;

    /// <summary>What to say when there's no key (the panel says how to set one).</summary>
    public string NoKeyText { get; set; } =
        $"There's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting AVA Modeler, or enter a key with the Key… button above. Your own API account is billed for what the chat uses.";

    public IReadOnlyList<ChatEntry> Entries => _entries;

    /// <summary>A new entry was added at the end.</summary>
    public event Action<ChatEntry>? EntryAdded;

    /// <summary>New Chat cleared everything.</summary>
    public event Action? Cleared;

    /// <summary><see cref="Working"/> changed.</summary>
    public event Action? WorkingChanged;

    /// <summary>A request is under way (Send has become Stop).</summary>
    public bool Working => _running != null;

    /// <summary>The conversation as it will be sent (for tests): each turn (the person's or Claude's) and its blocks.</summary>
    internal IReadOnlyList<(bool User, List<ContentBlockParam> Blocks)> Turns => _turns;

    /// <summary>Sends what the person typed and works until Claude has finished with it.</summary>
    public async Task SendAsync(string text, CancellationToken cancel = default)
    {
        text = text.Trim();
        if (text.Length == 0 || Working) return;
        foreach (var n in _entries.OfType<NoticeEntry>()) n.RetryGone();
        Add(new UserEntry(text));
        if (Client == null)
        {
            Add(new NoticeEntry(NoKeyText, NoticeKind.Info));
            return;
        }
        AddToUserTurn(new TextBlockParam { Text = text });
        await RunAsync(cancel);
    }

    /// <summary>Sends the conversation again after an error (nothing new is added).</summary>
    public async Task RetryAsync(CancellationToken cancel = default)
    {
        if (Working) return;
        foreach (var n in _entries.OfType<NoticeEntry>()) n.RetryGone();
        if (Client == null)
        {
            Add(new NoticeEntry(NoKeyText, NoticeKind.Info));
            return;
        }
        if (_turns.Count == 0 || !_turns[^1].User) return;
        await RunAsync(cancel);
    }

    /// <summary>Stops the request under way: the stream ends, a waiting tool call is given up.</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>Forgets the conversation (New Chat).</summary>
    public void NewChat()
    {
        Stop();
        // A request still winding down after the stop belongs to the old chat: it adds nothing to the new one.
        _generation++;
        _entries.Clear();
        _turns.Clear();
        Cleared?.Invoke();
    }

    private void Add(ChatEntry entry)
    {
        _entries.Add(entry);
        EntryAdded?.Invoke(entry);
    }

    /// <summary>Adds blocks to the conversation's last user turn, or starts one (turns must alternate).</summary>
    private void AddToUserTurn(params ContentBlockParam[] blocks)
    {
        if (_turns.Count > 0 && _turns[^1].User) _turns[^1].Blocks.AddRange(blocks);
        else _turns.Add((true, [.. blocks]));
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _running = running;
        WorkingChanged?.Invoke();
        try
        {
            await LoopAsync(running.Token);
        }
        finally
        {
            _running = null;
            WorkingChanged?.Invoke();
        }
    }

    private async Task LoopAsync(CancellationToken cancel)
    {
        int calls = 0, generation = _generation;
        bool Gone() => generation != _generation;
        while (true)
        {
            var reply = new AssistantEntry();
            Message message;
            try
            {
                _toolDefinitions ??= await tools.ListToolsAsync(cancel);
                if (Gone()) return;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                if (Gone()) return;
                Add(new NoticeEntry($"The app's tools for Claude couldn't start: {e.Message}", NoticeKind.Error, canRetry: true));
                return;
            }
            catch (OperationCanceledException)
            {
                if (!Gone()) Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
            try
            {
                message = await StreamAsync(Request(), reply, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // What was streamed stays on screen but isn't kept: the conversation ends on the person's turn.
                if (Gone()) return;
                if (_entries.Contains(reply)) reply.Finish("(stopped)");
                Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
            catch (Exception e)
            {
                if (Gone()) return;
                var problem = ChatErrors.Describe(e);
                reply.Finish();
                Add(new NoticeEntry(problem, NoticeKind.Error, canRetry: true));
                return;
            }
            if (Gone()) return;
            reply.Finish();

            // An empty reply (a refusal can be one) isn't kept: the API takes no empty turns.
            var assistant = AssistantBlocks(message);
            if (assistant.Count > 0) _turns.Add((false, assistant));
            var uses = message.Content.Select(b => b.TryPickToolUse(out var u) ? u : null).OfType<ToolUseBlock>().ToList();
            if (uses.Count == 0)
            {
                if (message.StopReason?.Value() is StopReason.MaxTokens)
                    Add(new NoticeEntry("Claude's reply reached its length limit and was cut short. Say \"continue\" to go on.", NoticeKind.Info));
                else if (message.StopReason?.Value() is StopReason.Refusal)
                    Add(new NoticeEntry("Claude declined that request.", NoticeKind.Info));
                return;
            }

            var results = new List<ContentBlockParam>();
            bool capped = false, stopped = false;
            foreach (var use in uses)
            {
                if (stopped || capped)
                {
                    results.Add(Result(use.ID, ToolOutcome.Error(stopped ? "Not run: the person pressed Stop." : $"Not run: the limit of {MaxToolCalls} tool calls for one request was reached.")));
                    continue;
                }
                if (calls >= MaxToolCalls)
                {
                    capped = true;
                    results.Add(Result(use.ID, ToolOutcome.Error($"Not run: the limit of {MaxToolCalls} tool calls for one request was reached.")));
                    continue;
                }
                calls++;
                var entry = new ToolEntry(use.Name, use.Input);
                Add(entry);
                ToolOutcome outcome;
                try
                {
                    outcome = await tools.CallAsync(use.Name, use.Input, cancel);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    stopped = true;
                    outcome = ToolOutcome.Error("Stopped by the person before it finished.");
                }
                catch (Exception e)
                {
                    outcome = ToolOutcome.Error($"{use.Name} failed: {e.Message}");
                }
                if (Gone()) return;
                entry.Finish(outcome);
                results.Add(Result(use.ID, outcome));
            }
            // Every tool_use has its tool_result, so the conversation can go on after a stop or the cap.
            AddToUserTurn([.. results]);
            if (stopped)
            {
                Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
            if (capped)
            {
                Add(new NoticeEntry($"Stopped after {MaxToolCalls} tool calls, the most one request may make. Say \"continue\" to let Claude carry on.", NoticeKind.Info));
                return;
            }
            if (cancel.IsCancellationRequested)
            {
                Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
        }
    }

    /// <summary>
    /// The request: the system prompt and the tools marked for prompt caching (the 52 tool schemas are most of every
    /// request), and automatic caching of the conversation so far.
    /// </summary>
    internal MessageCreateParams Request() => new()
    {
        Model = Model,
        MaxTokens = MaxTokens,
        System = new List<TextBlockParam> { new() { Text = ChatPrompt.System, CacheControl = new CacheControlEphemeral() } },
        Tools = ToolsFor(_toolDefinitions ?? []),
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

    private async Task<Message> StreamAsync(MessageCreateParams request, AssistantEntry reply, CancellationToken cancel)
    {
        // Text shows as it streams; the whole reply is put together from the same events afterwards, by the SDK.
        var events = new List<RawMessageStreamEvent>();
        await foreach (var ev in Client!.Messages.CreateStreaming(request, cancel).WithCancellation(cancel))
        {
            events.Add(ev);
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                // The reply shows once it has words (a turn of tool calls only shows its tool lines).
                if (reply.Text.Length == 0 && !_entries.Contains(reply)) Add(reply);
                reply.Append(text.Text);
            }
        }
        return await events.ToAsyncEnumerable().Aggregate();
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
