using System.Text.Json;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>Where the chat's model runs.</summary>
public enum ChatProvider
{
    /// <summary>Claude, through Anthropic's Messages API (the official SDK).</summary>
    Anthropic,

    /// <summary>
    /// Any server that speaks the OpenAI Chat Completions API: Ollama's <c>/v1</c>, LM Studio, vLLM, llama.cpp's server,
    /// or a company's own gateway.
    /// </summary>
    OpenAICompatible,
}

/// <summary>A tool call the model asked for, as the agent loop runs it.</summary>
/// <param name="Problem">Set when the call can't be run as sent (its arguments weren't JSON): it goes back as an error.</param>
internal sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input, string? Problem = null);

/// <summary>Why a reply ended, when it asked for no tools.</summary>
internal enum ReplyEnd { Done, MaxTokens, Refusal }

/// <summary>A finished reply: the tool calls it asks for (none when it's done), why it ended, and the tokens it used.</summary>
internal sealed record BackendReply(IReadOnlyList<ToolCall> Calls, ReplyEnd End, TokenUsage Usage = default);

/// <summary>
/// One provider's side of the chat: its conversation in its own API's shape, and one streamed request at a time. The
/// agent loop (<see cref="ChatSession"/>) is shared: it runs the tool calls, counts them, stops and carries on the same
/// way whichever provider answers.
/// </summary>
internal interface IChatBackend
{
    /// <summary>Whether a request can be made (a key, or a server and model, is set).</summary>
    bool Ready { get; }

    /// <summary>The conversation ends on the person's side (their words or tool results), so it can be sent again.</summary>
    bool EndsWithPerson { get; }

    void AddUserText(string text);

    /// <summary>
    /// Puts back a reply the model made before (a saved chat carried on): its words and the tool calls it asked for, in
    /// this provider's shape.
    /// </summary>
    void AddAssistant(string text, IReadOnlyList<ToolCall> calls);

    /// <summary>
    /// Sends the conversation and streams the reply: its words go to <paramref name="text"/> as they come, and the whole
    /// reply is kept in the conversation (unless it's empty). A failure or cancel keeps nothing. <paramref name="usage"/>
    /// is told the request's tokens so far as the API reports them (each time the whole count, not what's new).
    /// </summary>
    Task<BackendReply> ReplyAsync(IReadOnlyList<McpTool> tools, Action<string> text, CancellationToken cancel, Action<TokenUsage>? usage = null);

    /// <summary>The outcomes of the last reply's tool calls, one for each, in order.</summary>
    void AddResults(IReadOnlyList<(ToolCall Call, ToolOutcome Outcome)> results);

    void Clear();
}
