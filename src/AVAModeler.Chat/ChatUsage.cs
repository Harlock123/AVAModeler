using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAModeler.Chat;

/// <summary>
/// Tokens a request (or many, added up) used, as the API reports them: input read fresh, input written to the prompt
/// cache, input read from it, and output; and how many requests that was.
/// </summary>
public readonly record struct TokenUsage(long Input, long Output, long CacheWrite = 0, long CacheRead = 0, int Requests = 0)
{
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Requests + b.Requests);

    /// <summary>All the input, however it was billed.</summary>
    [JsonIgnore] public long AllInput => Input + CacheWrite + CacheRead;

    [JsonIgnore] public bool IsEmpty => Input == 0 && Output == 0 && CacheWrite == 0 && CacheRead == 0;

    /// <summary>A count of tokens in a few characters: 950, 12.4k, 1.20M.</summary>
    public static string Short(long n) => n switch
    {
        < 1000 => n.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (n / 1000.0).ToString(n < 100_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "k",
        _ => (n / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>"48.2k in (31.0k from cache) · 3.1k out".</summary>
    public string Describe()
    {
        var cached = CacheRead > 0 ? $" ({Short(CacheRead)} from cache)" : "";
        return $"{Short(AllInput)} in{cached} · {Short(Output)} out";
    }
}

/// <summary>A model's prices in US dollars a million tokens: input, output, and the prompt cache's writes and reads.</summary>
public sealed record ModelPrice(string Model, double Input, double Output, double CacheWrite, double CacheRead)
{
    /// <summary>What <paramref name="u"/> costs at these prices.</summary>
    public double Cost(TokenUsage u) => (u.Input * Input + u.Output * Output + u.CacheWrite * CacheWrite + u.CacheRead * CacheRead) / 1_000_000;

    /// <summary>A price that makes sense: each a number from 0 to 10000.</summary>
    public ModelPrice Checked()
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("A price needs the model's id.");
        foreach (var (v, what) in new[] { (Input, "input"), (Output, "output"), (CacheWrite, "cache write"), (CacheRead, "cache read") })
            if (!double.IsFinite(v) || v < 0 || v > 10000) throw new ArgumentException($"{Model}'s {what} price must be a number of dollars a million tokens, 0 to 10000 (it's {v}).");
        return this;
    }
}

/// <summary>
/// What the chat's models cost, to turn tokens into an estimate. The defaults are Anthropic's list prices for the
/// Claude API when this was written (October 2026): prices change, so the person can set their own in Setup… (kept in
/// preferences), and the panel always marks the dollars as about (≈) and points to the Anthropic Console for the bill.
/// A cache write is the five-minute cache's 1.25 × input. Models on an OpenAI-compatible server have no price: local
/// ones are free, and a gateway's are its own.
/// </summary>
public static class ChatPricing
{
    public const string AsOf = "October 2026";

    public static readonly IReadOnlyList<ModelPrice> Defaults =
    [
        new(ChatModels.Opus, 4, 20, 5, 0.20),
        new(ChatModels.Sonnet, 2, 10, 2.5, 0.20),
        new(ChatModels.Haiku, 1, 5, 1.25, 0.10),
    ];

    /// <summary>A model's price: the person's own if they set one, else the default, else none.</summary>
    public static ModelPrice? Find(string model, IEnumerable<ModelPrice>? own = null) =>
        own?.FirstOrDefault(p => p.Model == model) ?? Defaults.FirstOrDefault(p => p.Model == model);

    /// <summary>Dollars in a few characters: "$0.042", "$1.37", "$128".</summary>
    public static string Dollars(double d) => "$" + d.ToString(d < 0.1 ? "0.000" : d < 100 ? "0.00" : "0", CultureInfo.InvariantCulture);
}

/// <summary>A day's chat use: its tokens and its estimated cost (requests to unpriced models add tokens but no cost).</summary>
public sealed class UsageDay
{
    public TokenUsage Tokens { get; set; }
    public double Cost { get; set; }
}

/// <summary>
/// The chat's use day by day, kept in <c>chat-usage.json</c> in the settings folder (both apps' chats add to their
/// own), for the panel's today and this-month totals. Holds only counts and dollars, never a word of a chat. Reset
/// clears it. Ones made without a path (tests, test windows) keep it in memory only.
/// </summary>
public sealed class UsageLedger
{
    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<string, UsageDay> _days = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public UsageLedger(string? path = null)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;
        try { _days = JsonSerializer.Deserialize<Dictionary<string, UsageDay>>(File.ReadAllText(path), Json) ?? []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { _days = []; }
    }

    /// <summary>The file it's kept in, or null.</summary>
    public string? Path => _path;

    /// <summary>Raised after it changes.</summary>
    public event Action? Changed;

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Adds a request's use to its day, and saves.</summary>
    public void Add(DateTimeOffset when, TokenUsage tokens, double? cost)
    {
        lock (_lock)
        {
            var key = Key(DateOnly.FromDateTime(when.LocalDateTime));
            if (!_days.TryGetValue(key, out var day)) _days[key] = day = new UsageDay();
            day.Tokens += tokens;
            day.Cost += cost ?? 0;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>One day's use.</summary>
    public UsageDay Day(DateOnly day)
    {
        lock (_lock) return _days.TryGetValue(Key(day), out var d) ? new UsageDay { Tokens = d.Tokens, Cost = d.Cost } : new UsageDay();
    }

    /// <summary>A month's use, its days added up.</summary>
    public UsageDay Month(int year, int month)
    {
        lock (_lock)
        {
            var prefix = $"{year:0000}-{month:00}-";
            var sum = new UsageDay();
            foreach (var (k, d) in _days)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) { sum.Tokens += d.Tokens; sum.Cost += d.Cost; }
            return sum;
        }
    }

    /// <summary>Forgets every day.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _days.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_days, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One request's use within a turn, and how many of the tools' pictures went with it.</summary>
public sealed record RequestUsage(TokenUsage Tokens, double? Cost, int Pictures);

/// <summary>
/// A turn's use, shown after it: from the person's message until the model stopped, every request it took (each tool
/// call sends the conversation again), their tokens and estimated cost, and the pictures sent to the model.
/// </summary>
public sealed class UsageEntry : ChatEntry
{
    internal UsageEntry(IReadOnlyList<RequestUsage> requests, string? model)
    {
        Requests = requests;
        Model = model;
    }

    /// <summary>Shown again from a saved chat: its line only.</summary>
    internal UsageEntry(string line)
    {
        Requests = [];
        _saved = line;
    }

    private readonly string? _saved;

    public IReadOnlyList<RequestUsage> Requests { get; }

    public string? Model { get; }

    public TokenUsage Tokens => Requests.Aggregate(default(TokenUsage), (a, r) => a + r.Tokens);

    /// <summary>The estimated cost, or null if the model has no price.</summary>
    public double? Cost => Requests.Any(r => r.Cost == null) ? null : Requests.Sum(r => r.Cost!.Value);

    public int Pictures => Requests.Sum(r => r.Pictures);

    /// <summary>"This turn: 6 requests · 41.2k in (38.0k from cache) · 1.9k out · 2 pictures · ≈ $0.042".</summary>
    public string Line
    {
        get
        {
            if (_saved != null) return _saved;
            int n = Requests.Count;
            var parts = new List<string> { $"This turn: {n} request{(n == 1 ? "" : "s")}", Tokens.Describe() };
            if (Pictures > 0) parts.Add(Pictures == 1 ? "1 picture" : $"{Pictures} pictures");
            parts.Add(Cost is { } c ? "≈ " + ChatPricing.Dollars(c) : "no price");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Each request on a line of its own, for the tag's tooltip.</summary>
    public string Details => Requests.Count == 0 ? Line : string.Join("\n", Requests.Select((r, i) =>
        $"Request {i + 1}: {r.Tokens.Describe()}{(r.Tokens.CacheWrite > 0 ? $", {TokenUsage.Short(r.Tokens.CacheWrite)} written to the cache" : "")}"
        + (r.Pictures > 0 ? $", {r.Pictures} picture{(r.Pictures == 1 ? "" : "s")}" : "")
        + (r.Cost is { } c ? $" · ≈ {ChatPricing.Dollars(c)}" : ""))) + "\nEstimates: the Anthropic Console has the bill.";
}
