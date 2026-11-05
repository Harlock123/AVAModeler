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
    /// <summary>The conversation in neither provider's shape, as it was sent and answered: what a saved chat keeps.</summary>
    private readonly List<LogTurn> _log = [];
    private (string Model, string? Server) _opened = ("", null);
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
            if (ReadOnlyProvider is { } was)
            {
                // A saved chat shown read-only comes alive when the provider it was with is chosen again.
                if (was == value) LoadBackend();
                ReadOnlyChanged?.Invoke();
                return;
            }
            if (any) NewChat();
        }
    }

    /// <summary>This chat's id: the name of its file in the history.</summary>
    public string ChatId { get; private set; } = "";

    /// <summary>When this chat started.</summary>
    public DateTimeOffset Created { get; private set; }

    /// <summary>The clock new chats are dated by (tests set their own).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>The OpenAI-compatible server as a saved chat names it: host and port only (the panel sets it).</summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// When a saved chat with the other provider is open, that provider: the chat is shown but can't be carried on (its
    /// messages are in the other API's terms) until that provider is chosen again, or New Chat starts afresh.
    /// </summary>
    public ChatProvider? ReadOnlyProvider { get; private set; }

    /// <summary>Whether the chat on show is read-only (see <see cref="ReadOnlyProvider"/>).</summary>
    public bool ReadOnly => ReadOnlyProvider != null;

    /// <summary>Raised when <see cref="ReadOnly"/> changes.</summary>
    public event Action? ReadOnlyChanged;

    /// <summary>Whether there's anything to save: the person has said something.</summary>
    public bool HasConversation => _entries.OfType<UserEntry>().Any();

    /// <summary>A turn of the conversation: the person's words, the model's reply and calls, or the calls' outcomes.</summary>
    private sealed record LogTurn(string Role, string Text, IReadOnlyList<ToolCall> Calls, IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> Results);

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

    /// <summary>
    /// What the model is told before the conversation: <see cref="ChatPrompt.System"/> (AVA Modeler's server
    /// instructions and its in-app lines) unless an app gives its own, for both providers.
    /// </summary>
    public string SystemPrompt
    {
        get => _anthropic.SystemPrompt;
        set { _anthropic.SystemPrompt = value; _server.SystemPrompt = value; }
    }

    /// <summary>The tools an OpenAI-compatible model gets with <see cref="CoreToolsOnly"/> (<see cref="ChatTools.Core"/> unless an app gives its own), and the note that says so.</summary>
    public IReadOnlyList<string> CoreTools { get => _server.CoreTools; set => _server.CoreTools = value; }

    /// <inheritdoc cref="CoreTools"/>
    public string CoreToolsNote { get => _server.CoreToolsNote; set => _server.CoreToolsNote = value; }

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
        if (text.Length == 0 || Working || ReadOnly) return;
        if (ChatId.Length == 0) StartId();
        foreach (var n in _entries.OfType<NoticeEntry>()) n.RetryGone();
        Add(new UserEntry(text));
        if (!Backend.Ready)
        {
            Add(new NoticeEntry(NotReadyText, NoticeKind.Info));
            return;
        }
        Backend.AddUserText(text);
        _log.Add(new LogTurn("user", text, [], []));
        await RunAsync(cancel);
    }

    /// <summary>Sends the conversation again after an error (nothing new is added).</summary>
    public async Task RetryAsync(CancellationToken cancel = default)
    {
        if (Working || ReadOnly) return;
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
        _log.Clear();
        _anthropic.Clear();
        _server.Clear();
        ChatId = "";
        bool wasReadOnly = ReadOnly;
        ReadOnlyProvider = null;
        Cleared?.Invoke();
        if (wasReadOnly) ReadOnlyChanged?.Invoke();
    }

    private void StartId()
    {
        Created = Clock();
        ChatId = ChatHistory.NewId(Created);
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
            // As the backend keeps it: a reply with no words and no calls isn't kept.
            if (reply.Text.Length > 0 || message.Calls.Count > 0) _log.Add(new LogTurn("assistant", reply.Text, message.Calls, []));

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
            _log.Add(new LogTurn("results", "", [], results));
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

    // ───────────────────────────── saving and opening chats ─────────────────────────────

    /// <summary>
    /// The chat as it's saved: what the panel shows and the conversation in a shape either provider can be given again.
    /// Each picture goes through <paramref name="keep"/> (which returns the name it's kept under, or null to leave it
    /// out: too big); the same picture is asked about once. Never a key.
    /// </summary>
    public StoredChat ToStored(Func<ToolImage, string?> keep)
    {
        if (ChatId.Length == 0) StartId();
        var kept = new Dictionary<ToolImage, string?>(ReferenceEqualityComparer.Instance);
        string? Keep(ToolImage i) => kept.TryGetValue(i, out var name) ? name : kept[i] = keep(i);
        (List<string>? Names, int Left) Pictures(IReadOnlyList<ToolImage> images)
        {
            if (images.Count == 0) return (null, 0);
            var names = images.Select(Keep).ToList();
            return (names.OfType<string>().ToList(), names.Count(n => n == null));
        }
        var chat = new StoredChat
        {
            Id = ChatId, Created = Created, Updated = Clock(),
            // A chat open read-only keeps the provider, model and server it was with.
            Provider = ReadOnlyProvider ?? _provider,
            Model = ReadOnly ? _opened.Model : _provider == ChatProvider.Anthropic ? Model : ServerModel,
            Server = ReadOnly ? _opened.Server : _provider == ChatProvider.OpenAICompatible ? ServerName : null,
            Title = Title(),
        };
        foreach (var e in _entries)
        {
            switch (e)
            {
                case UserEntry u: chat.Entries.Add(new StoredEntry { Kind = "user", Text = u.Text }); break;
                case AssistantEntry a when a.Text.Length > 0: chat.Entries.Add(new StoredEntry { Kind = "assistant", Text = a.Text }); break;
                case ToolEntry t:
                {
                    var (names, left) = Pictures(t.Images);
                    chat.Entries.Add(new StoredEntry { Kind = "tool", Name = t.Name, Input = t.Input, Text = t.Result, Error = t.IsError, Images = names, ImagesLeftOut = left });
                    break;
                }
                case NoticeEntry n: chat.Entries.Add(new StoredEntry { Kind = "notice", Text = n.Text, Error = n.Kind == NoticeKind.Error }); break;
            }
        }
        foreach (var t in _log)
        {
            chat.Turns.Add(t.Role switch
            {
                "user" => new StoredTurn { Role = "user", Text = t.Text },
                "assistant" => new StoredTurn
                {
                    Role = "assistant", Text = t.Text,
                    Calls = t.Calls.Count == 0 ? null : t.Calls.Select(c => new StoredCall { Id = c.Id, Name = c.Name, Input = c.Input.ToDictionary(), Problem = c.Problem }).ToList(),
                },
                _ => new StoredTurn
                {
                    Role = "results",
                    Results = t.Results.Select(r =>
                    {
                        var (names, left) = Pictures(r.Outcome.Images);
                        return new StoredResult { Id = r.Call.Id, Name = r.Call.Name, Text = r.Outcome.Text, Error = r.Outcome.IsError, Images = names, ImagesLeftOut = left };
                    }).ToList(),
                },
            });
        }
        return chat;
    }

    /// <summary>The chat's title: the start of the person's first message.</summary>
    private string Title()
    {
        var first = _entries.OfType<UserEntry>().FirstOrDefault()?.Text.Replace('\n', ' ').Trim() ?? "";
        return first.Length > 60 ? first[..59].TrimEnd() + "…" : first;
    }

    /// <summary>
    /// Opens a saved chat in place of this one: its lines as they were (pictures read through <paramref name="image"/>;
    /// any that are gone are left out), and its conversation given to the provider's backend so it can carry on, if it
    /// was with the provider in use; otherwise it's shown read-only (<see cref="ReadOnlyProvider"/>).
    /// </summary>
    public void Open(StoredChat chat, Func<string, ToolImage?> image)
    {
        NewChat();
        ChatId = chat.Id;
        Created = chat.Created;
        _opened = (chat.Model, chat.Server);
        IReadOnlyList<ToolImage> Pictures(List<string>? names) => (names ?? []).Select(image).OfType<ToolImage>().ToList();
        foreach (var e in chat.Entries)
        {
            switch (e.Kind)
            {
                case "user": Add(new UserEntry(e.Text)); break;
                case "assistant":
                {
                    var a = new AssistantEntry();
                    a.Append(e.Text);
                    a.Finish();
                    Add(a);
                    break;
                }
                case "tool":
                {
                    Dictionary<string, System.Text.Json.JsonElement> input;
                    try { input = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(e.Input ?? "{}") ?? []; }
                    catch (System.Text.Json.JsonException) { input = []; }
                    var t = new ToolEntry(e.Name ?? "tool", input);
                    var note = e.ImagesLeftOut > 0 ? $"\n({e.ImagesLeftOut} picture(s) too big to keep)" : "";
                    t.Finish(new ToolOutcome(e.Error, e.Text + note, Pictures(e.Images)));
                    Add(t);
                    break;
                }
                case "notice": Add(new NoticeEntry(e.Text, e.Error ? NoticeKind.Error : NoticeKind.Info)); break;
            }
        }
        LogTurn? open = null;
        foreach (var t in chat.Turns)
        {
            switch (t.Role)
            {
                case "user":
                    Close();
                    _log.Add(new LogTurn("user", t.Text ?? "", [], []));
                    break;
                case "assistant":
                    Close();
                    var calls = (t.Calls ?? []).Select(c => new ToolCall(c.Id, c.Name, c.Input, c.Problem)).ToList();
                    var turn = new LogTurn("assistant", t.Text ?? "", calls, []);
                    _log.Add(turn);
                    if (calls.Count > 0) open = turn;
                    break;
                case "results" when open != null:
                    var results = new List<(ToolCall, ToolOutcome)>();
                    foreach (var call in open.Calls)
                    {
                        var r = (t.Results ?? []).FirstOrDefault(x => x.Id == call.Id);
                        results.Add((call, r == null
                            ? ToolOutcome.Error("Not run: the chat was closed before it finished.")
                            : new ToolOutcome(r.Error, r.Text + (r.ImagesLeftOut > 0 ? $"\n({r.ImagesLeftOut} picture(s) weren't kept with the saved chat.)" : ""), Pictures(r.Images))));
                    }
                    _log.Add(new LogTurn("results", "", [], results));
                    open = null;
                    break;
            }
        }
        Close();
        if (chat.Provider == _provider) LoadBackend();
        else
        {
            ReadOnlyProvider = chat.Provider;
            ReadOnlyChanged?.Invoke();
        }

        // Calls left without results (the app closed mid-request) get them, so the conversation can go on.
        void Close()
        {
            if (open == null) return;
            _log.Add(new LogTurn("results", "", [], open.Calls.Select(c => (c, ToolOutcome.Error("Not run: the chat was closed before it finished."))).ToList()));
            open = null;
        }
    }

    /// <summary>Gives the provider's backend the logged conversation, in its own shape, so the chat can carry on.</summary>
    private void LoadBackend()
    {
        var backend = Backend;
        backend.Clear();
        foreach (var t in _log)
        {
            switch (t.Role)
            {
                case "user": backend.AddUserText(t.Text); break;
                case "assistant": backend.AddAssistant(t.Text, t.Calls); break;
                default: backend.AddResults(t.Results); break;
            }
        }
        ReadOnlyProvider = null;
    }
}
