using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>
/// One chat with a model: the conversation kept for the session, and the agent loop. A message goes to the provider's
/// API (streamed, so text shows as it's written) with the MCP server's tools; each tool call runs through the
/// <see cref="IToolBridge"/> and its result goes back, until the model stops, the step cap is reached, or the person
/// presses Stop. The provider is Claude through Anthropic's Messages API, or any model behind an OpenAI-compatible
/// Chat Completions API (a local server such as Ollama, or a gateway); each keeps the conversation in its own API's shape.
/// </summary>
/// <remarks>
/// It runs its continuations on the caller's context (no <c>ConfigureAwait(false)</c>), so in the app every entry and
/// event changes on the UI thread. The HTTP traffic and the tools' work happen elsewhere and don't block it.
/// </remarks>
public sealed class ChatSession(IToolBridge tools)
{
    private readonly List<ChatEntry> _entries = [];
    private readonly AnthropicBackend _anthropic = new();
    private readonly OpenAICompatibleBackend _server = new();
    private ChatProvider _provider;
    private CancellationTokenSource? _running;
    private int _generation;
    private IReadOnlyList<McpTool>? _toolDefinitions;

    /// <summary>
    /// Where the model runs. Changing it starts a new chat: a conversation can't move between providers (their messages
    /// and tool calls are kept in different shapes).
    /// </summary>
    public ChatProvider Provider
    {
        get => _provider;
        set
        {
            if (value == _provider) return;
            bool any = _entries.Count > 0 || Working;
            _provider = value;
            if (any) NewChat();
        }
    }

    private IChatBackend Backend => _provider == ChatProvider.Anthropic ? _anthropic : _server;

    /// <summary>The Claude API client, or null while there's no key.</summary>
    public IAnthropicClient? Client { get => _anthropic.Client; set => _anthropic.Client = value; }

    /// <summary>The Claude model the next request uses (one of <see cref="ChatModels.All"/>).</summary>
    public string Model { get => _anthropic.Model; set => _anthropic.Model = value; }

    /// <summary>The OpenAI-compatible server's client (its base URL and any key), or null while none is set.</summary>
    public OpenAICompatibleClient? Server { get => _server.Client; set => _server.Client = value; }

    /// <summary>The model on the OpenAI-compatible server, by the server's name for it.</summary>
    public string ServerModel { get => _server.Model; set => _server.Model = value ?? ""; }

    /// <summary>Offer the OpenAI-compatible model only <see cref="ChatTools.Core"/> (on by default: small models do better).</summary>
    public bool CoreToolsOnly { get => _server.CoreToolsOnly; set => _server.CoreToolsOnly = value; }

    /// <summary>Send the OpenAI-compatible model the tools' pictures (a vision model); otherwise it's told one was made.</summary>
    public bool ModelSeesPictures { get => _server.ModelSeesPictures; set => _server.ModelSeesPictures = value; }

    /// <summary>The model as the chat's lines name it: "Claude", or "The model".</summary>
    public string Who => _provider == ChatProvider.Anthropic ? "Claude" : "The model";

    /// <summary>The most tool calls one request may make before the chat stops and says so.</summary>
    public int MaxToolCalls { get; set; } = 60;

    /// <summary>
    /// How many times one request carries on by itself after <see cref="MaxToolCalls"/>, as if the person had said
    /// "continue" (0: it stops and asks). Each time, the count starts again and a line in the chat says so; Stop ends it
    /// at any point. This bounds what one request can cost.
    /// </summary>
    public int AutoContinues { get; set; }

    public long MaxTokens { get => _anthropic.MaxTokens; set => _anthropic.MaxTokens = value; }

    /// <summary>What to say when there's no key (the panel says how to set one).</summary>
    public string NoKeyText { get; set; } =
        $"There's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting AVA Modeler, or enter a key with the Key… button above. Your own API account is billed for what the chat uses.";

    /// <summary>What to say when the OpenAI-compatible server or its model isn't set.</summary>
    public string NoServerText { get; set; } =
        "No model is set for the OpenAI-compatible server yet. Press Setup… above, enter the server's base URL (Ollama's is http://localhost:11434/v1) and a model name; Refresh lists the server's models.";

    public IReadOnlyList<ChatEntry> Entries => _entries;

    /// <summary>A new entry was added at the end.</summary>
    public event Action<ChatEntry>? EntryAdded;

    /// <summary>New Chat cleared everything.</summary>
    public event Action? Cleared;

    /// <summary><see cref="Working"/> changed.</summary>
    public event Action? WorkingChanged;

    /// <summary>A request is under way (Send has become Stop).</summary>
    public bool Working => _running != null;

    /// <summary>The Claude conversation as it will be sent (for tests): each turn (the person's or Claude's) and its blocks.</summary>
    internal IReadOnlyList<(bool User, List<ContentBlockParam> Blocks)> Turns => _anthropic.Turns;

    /// <summary>The OpenAI-compatible conversation as it will be sent, after the system message (for tests).</summary>
    internal IReadOnlyList<JsonObject> ServerMessages => _server.Messages;

    private string NotReadyText => _provider == ChatProvider.Anthropic ? NoKeyText : NoServerText;

    /// <summary>Sends what the person typed and works until the model has finished with it.</summary>
    public async Task SendAsync(string text, CancellationToken cancel = default)
    {
        text = text.Trim();
        if (text.Length == 0 || Working) return;
        foreach (var n in _entries.OfType<NoticeEntry>()) n.RetryGone();
        Add(new UserEntry(text));
        if (!Backend.Ready)
        {
            Add(new NoticeEntry(NotReadyText, NoticeKind.Info));
            return;
        }
        Backend.AddUserText(text);
        await RunAsync(cancel);
    }

    /// <summary>Sends the conversation again after an error (nothing new is added).</summary>
    public async Task RetryAsync(CancellationToken cancel = default)
    {
        if (Working) return;
        foreach (var n in _entries.OfType<NoticeEntry>()) n.RetryGone();
        if (!Backend.Ready)
        {
            Add(new NoticeEntry(NotReadyText, NoticeKind.Info));
            return;
        }
        if (!Backend.EndsWithPerson) return;
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
        _anthropic.Clear();
        _server.Clear();
        Cleared?.Invoke();
    }

    private void Add(ChatEntry entry)
    {
        _entries.Add(entry);
        EntryAdded?.Invoke(entry);
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _running = running;
        WorkingChanged?.Invoke();
        try
        {
            await LoopAsync(Backend, running.Token);
        }
        finally
        {
            _running = null;
            WorkingChanged?.Invoke();
        }
    }

    private async Task LoopAsync(IChatBackend backend, CancellationToken cancel)
    {
        int calls = 0, continues = 0, generation = _generation;
        bool Gone() => generation != _generation;
        var who = Who;
        while (true)
        {
            var reply = new AssistantEntry();
            BackendReply message;
            try
            {
                _toolDefinitions ??= await tools.ListToolsAsync(cancel);
                if (Gone()) return;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                if (Gone()) return;
                Add(new NoticeEntry($"The app's tools for the chat couldn't start: {e.Message}", NoticeKind.Error, canRetry: true));
                return;
            }
            catch (OperationCanceledException)
            {
                if (!Gone()) Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
            try
            {
                message = await backend.ReplyAsync(_toolDefinitions, text =>
                {
                    if (Gone()) return;
                    // The reply shows once it has words (a turn of tool calls only shows its tool lines).
                    if (reply.Text.Length == 0 && !_entries.Contains(reply)) Add(reply);
                    reply.Append(text);
                }, cancel);
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

            if (message.Calls.Count == 0)
            {
                if (message.End is ReplyEnd.MaxTokens)
                    Add(new NoticeEntry($"{who}'s reply reached its length limit and was cut short. Say \"continue\" to go on.", NoticeKind.Info));
                else if (message.End is ReplyEnd.Refusal)
                    Add(new NoticeEntry($"{who} declined that request.", NoticeKind.Info));
                return;
            }

            var results = new List<(ToolCall, ToolOutcome)>();
            bool capped = false, stopped = false;
            foreach (var use in message.Calls)
            {
                if (stopped || capped)
                {
                    results.Add((use, ToolOutcome.Error(stopped ? "Not run: the person pressed Stop." : $"Not run: the limit of {MaxToolCalls} tool calls for one request was reached.")));
                    continue;
                }
                if (calls >= MaxToolCalls && continues < AutoContinues)
                {
                    continues++;
                    calls = 0;
                    Add(new NoticeEntry($"{MaxToolCalls} tool calls made; carrying on by itself ({continues} of {AutoContinues} times this request). Press Stop to end it.", NoticeKind.Info));
                }
                if (calls >= MaxToolCalls)
                {
                    capped = true;
                    results.Add((use, ToolOutcome.Error($"Not run: the limit of {MaxToolCalls} tool calls for one request was reached.")));
                    continue;
                }
                calls++;
                var entry = new ToolEntry(use.Name, use.Input);
                Add(entry);
                ToolOutcome outcome;
                if (use.Problem != null) outcome = ToolOutcome.Error(use.Problem);
                else
                {
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
                }
                if (Gone()) return;
                entry.Finish(outcome);
                results.Add((use, outcome));
            }
            // Every tool call has its result, so the conversation can go on after a stop or the cap.
            backend.AddResults(results);
            if (stopped)
            {
                Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
            if (capped)
            {
                Add(new NoticeEntry(AutoContinues == 0
                    ? $"Stopped after {MaxToolCalls} tool calls, the most one request may make. Say \"continue\" to let {(who == "Claude" ? who : "the model")} carry on, or tick Auto-continue."
                    : $"Stopped after {MaxToolCalls * (AutoContinues + 1)} tool calls ({MaxToolCalls}, then {AutoContinues} automatic continues), the most one request may make. Say \"continue\" to let {(who == "Claude" ? who : "the model")} carry on.", NoticeKind.Info));
                return;
            }
            if (cancel.IsCancellationRequested)
            {
                Add(new NoticeEntry("Stopped.", NoticeKind.Info));
                return;
            }
        }
    }
}
