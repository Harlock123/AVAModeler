using System.Text.Json;

namespace AVAModeler.Chat;

/// <summary>One line of the chat as the panel shows it. <see cref="Changed"/> fires as it fills in.</summary>
public abstract class ChatEntry
{
    public event Action? Changed;

    protected void Raise() => Changed?.Invoke();
}

/// <summary>What the person typed.</summary>
public sealed class UserEntry(string text) : ChatEntry
{
    public string Text { get; } = text;
}

/// <summary>Claude's text, which grows as it streams in.</summary>
public sealed class AssistantEntry : ChatEntry
{
    public string Text { get; private set; } = "";

    /// <summary>Still being written.</summary>
    public bool Streaming { get; private set; } = true;

    internal void Append(string text)
    {
        Text += text;
        Raise();
    }

    internal void Finish(string? note = null)
    {
        Streaming = false;
        if (note != null) Text += (Text.Length > 0 ? " " : "") + note;
        Raise();
    }
}

/// <summary>A tool call: its name and input, then its result (first line for the compact view) and any pictures.</summary>
public sealed class ToolEntry : ChatEntry
{
    internal ToolEntry(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        Name = name;
        Input = JsonSerializer.Serialize(input, Indented);
        Summary = Summarise(name, input);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Name { get; }

    /// <summary>The input as indented JSON.</summary>
    public string Input { get; }

    /// <summary>A few words of the input: run_tool's tool name, or the first values.</summary>
    public string Summary { get; }

    public bool Running { get; private set; } = true;
    public bool IsError { get; private set; }
    public string Result { get; private set; } = "";
    public IReadOnlyList<ToolImage> Images { get; private set; } = [];

    /// <summary>The result's first line, for the compact view (a JSON answer put in a few words).</summary>
    public string FirstLine => Running ? "…" : Brief(Result) is { Length: > 0 } line ? line : Images.Count > 0 ? "a picture" : "done";

    /// <summary>
    /// A result in a few words: the MCP tools mostly answer in JSON, so "done" and the counts after it, or the first few
    /// plain values; otherwise the first line of the text.
    /// </summary>
    private static string Brief(string result)
    {
        var first = result.Split('\n', 2)[0].Trim();
        if (!first.StartsWith('{')) return first;
        try
        {
            using var doc = JsonDocument.Parse(result);
            var o = doc.RootElement;
            string? Count(string name) => o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("after", out var after) ? $"{after.GetRawText()} {name}" : null;
            if (o.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.String)
            {
                var counts = new[] { Count("points"), Count("polygons") }.OfType<string>().ToList();
                return done.GetString() + (counts.Count > 0 ? ": " + string.Join(", ", counts) : "");
            }
            var plain = o.EnumerateObject()
                .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .Take(3).Select(p => $"{p.Name} {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}").ToList();
            return plain.Count > 0 ? string.Join(", ", plain) : first;
        }
        catch (JsonException)
        {
            return first;
        }
    }

    /// <summary>The compact line: "▸ run_tool Ball → 482 points…".</summary>
    public string Line
    {
        get
        {
            var first = FirstLine;
            if (first.Length > 90) first = first[..90] + "…";
            return $"{Name}{(Summary.Length > 0 ? " " + Summary : "")} {(IsError ? "✗" : "→")} {first}";
        }
    }

    /// <summary>How far a long call has got (0 to 1), or null while it hasn't said.</summary>
    public double? Progress { get; private set; }

    /// <summary>The call's last progress line ("frame 12 of 30"), or null.</summary>
    public string? ProgressText { get; private set; }

    internal void Report(double done, string? text)
    {
        if (!Running) return;
        Progress = Math.Clamp(done, 0, 1);
        if (text != null) ProgressText = text;
        Raise();
    }

    internal void Finish(ToolOutcome outcome)
    {
        Running = false;
        IsError = outcome.IsError;
        Result = outcome.Text;
        Images = outcome.Images;
        Raise();
    }

    private static string Summarise(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.Count == 0) return "";
        if (name == "run_tool" && input.TryGetValue("name", out var tool) && tool.ValueKind == JsonValueKind.String) return tool.GetString()!;
        var parts = input.Take(3).Select(kv => kv.Value.ValueKind switch
        {
            JsonValueKind.String => kv.Value.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => $"{kv.Key}={kv.Value.GetRawText()}",
            _ => kv.Key + "=…",
        });
        var s = string.Join(" ", parts);
        return s.Length > 50 ? s[..50] + "…" : s;
    }
}

/// <summary>What kind of note the chat shows.</summary>
public enum NoticeKind { Info, Error }

/// <summary>A note from the app itself: a missing key, an error (with Retry), the step cap, a stop.</summary>
public sealed class NoticeEntry(string text, NoticeKind kind, bool canRetry = false) : ChatEntry
{
    public string Text { get; } = text;
    public NoticeKind Kind { get; } = kind;

    /// <summary>Retry would send the conversation again (it's still offered until something else is sent).</summary>
    public bool CanRetry { get; private set; } = canRetry;

    internal void RetryGone()
    {
        if (!CanRetry) return;
        CanRetry = false;
        Raise();
    }
}

/// <summary>Where an <see cref="ReplyUndoEntry"/> stands.</summary>
public enum ReplyUndoState
{
    /// <summary>The button can be pressed.</summary>
    Available,
    /// <summary>The restore is under way.</summary>
    Undoing,
    /// <summary>The reply was undone.</summary>
    Undone,
    /// <summary>An earlier reply was undone, which took this one back with it.</summary>
    Superseded,
    /// <summary>The restore failed (see <see cref="ReplyUndoEntry.Message"/>); it can be tried again.</summary>
    Failed,
}

/// <summary>
/// After a reply that changed the object or the scene: Undo this reply, which puts both back as they were before it
/// (the checkpoint the chat took just before the reply's first change).
/// </summary>
public sealed class ReplyUndoEntry(string checkpoint) : ChatEntry
{
    /// <summary>The checkpoint's name on the MCP server.</summary>
    public string Checkpoint { get; } = checkpoint;

    public ReplyUndoState State { get; private set; } = ReplyUndoState.Available;

    /// <summary>Why the restore failed, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>What the reply changed, in a line ("+6 polygons, 8 points moved; scene: Key added"), for the timeline.</summary>
    public string Summary { get; internal set; } = "";

    /// <summary>The reply's number in the chat (1 for the first that changed something).</summary>
    public int Number { get; internal set; }

    /// <summary>A small picture of the object as it was before the reply, once the timeline has asked for it.</summary>
    public ToolImage? Before { get; internal set; }

    /// <summary>The button can be pressed (it's there, and not under way or done).</summary>
    public bool CanUndo => State is ReplyUndoState.Available or ReplyUndoState.Failed;

    internal void Set(ReplyUndoState state, string? message = null)
    {
        State = state;
        Message = message;
        Raise();
    }
}

/// <summary>
/// A question the app asks the person for an agent (MCP elicitation): an agent's call would remove most of a layer.
/// The panel shows it with Go Ahead and Don't; the call waits for the answer.
/// </summary>
public sealed class ConfirmEntry(string question) : ChatEntry
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Question { get; } = question;

    /// <summary>Null until answered: true to go ahead.</summary>
    public bool? Answer { get; private set; }

    internal Task<bool> Answered => _answer.Task;

    /// <summary>The person's answer (the panel's buttons call it); a second answer is ignored.</summary>
    public void Choose(bool goAhead)
    {
        if (Answer != null) return;
        Answer = goAhead;
        _answer.TrySetResult(goAhead);
        Raise();
    }
}
