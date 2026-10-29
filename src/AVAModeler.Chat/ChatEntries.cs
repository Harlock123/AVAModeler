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
