using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>A parameter of a steps file: its default and what it means.</summary>
public sealed class StepParameter
{
    [JsonPropertyName("default")]
    [Description("The value used when run_steps isn't given one (a number, text, true/false or a list).")]
    public JsonElement? Default { get; set; }

    [JsonPropertyName("description")]
    [Description("What it is, e.g. \"the vase's height in metres\".")]
    public string? Description { get; set; }
}

/// <summary>
/// Recording what a session changes (get_recording) and keeping it as a steps file with named parameters
/// (save_steps) to run again with other values (run_steps): procedural models made by an agent.
/// </summary>
public sealed partial class ModelerTools
{
    /// <summary>How many steps the recording keeps (the oldest go first).</summary>
    public const int MaxRecorded = 5000;

    /// <summary>The steps files' extension.</summary>
    public const string StepsExtension = ".avasteps.json";

    private readonly List<(string Tool, Dictionary<string, JsonElement>? Args)> _recording = [];
    private readonly Dictionary<string, int> _recordingMarks = new(StringComparer.Ordinal);
    private int _recordingDropped;

    /// <summary>The tools whose calls are never recorded: about the session or the history, not the model.</summary>
    private static readonly HashSet<string> NotRecorded = new(StringComparer.Ordinal)
    {
        "batch", "run_steps", "save_steps", "checkpoint", "restore_checkpoint", "undo", "redo", "undo_scene", "redo_scene",
        "start_job", "cancel_job", "lock", "unlock", "save", "save_scene", "export_scene", "save_selection",
    };

    /// <summary>Records a successful call that changes something (a batch's steps, one by one).</summary>
    private void Record(CallToolRequestParams request, Tool tool)
    {
        if (request.Name == "checkpoint")
        {
            var name = request.Arguments?.TryGetValue("name", out var n) == true && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : null;
            if (_checkpoints.Count > 0) _recordingMarks[name ?? _checkpoints[^1].Name] = _recording.Count + _recordingDropped;
            return;
        }
        if (request.Name == "batch")
        {
            var args = request.Arguments;
            if (args == null || args.TryGetValue("dry_run", out var d) && d.ValueKind == JsonValueKind.True) return;
            if (!args.TryGetValue("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return;
            foreach (var step in steps.EnumerateArray())
            {
                var name = step.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                var stepArgs = step.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : null;
                if (Server?.ServerOptions.ToolCollection?.TryGetPrimitive(name, out var st) == true && st.ProtocolTool.Annotations?.ReadOnlyHint != true)
                    Add(name, stepArgs);
            }
            return;
        }
        if (tool.Annotations?.ReadOnlyHint == true || NotRecorded.Contains(request.Name)) return;
        Add(request.Name, request.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()));
    }

    private void Add(string tool, Dictionary<string, JsonElement>? args)
    {
        _recording.Add((tool, args));
        if (_recording.Count > MaxRecorded)
        {
            _recording.RemoveAt(0);
            _recordingDropped++;
        }
    }

    [McpServerTool(Name = "get_recording", ReadOnly = true, Idempotent = true)]
    [Description("The calls this session has made that changed something, in order, as steps batch and save_steps take ([{\"tool\": ..., \"args\": {...}}]): every call that wasn't read-only (a batch's steps one by one; not undo, checkpoints, saves or locks). since gives only those after a checkpoint. Edit them (put \"${name}\" where a parameter should go) and keep them with save_steps to make a procedural model you can run again with other values. The 5,000 newest are kept.")]
    public Task<string> GetRecording(
        [Description("Only the steps since this checkpoint was taken.")] string? since = null,
        CancellationToken cancel = default) =>
        Do("read the recording", c =>
        {
            int from = 0;
            if (since != null)
            {
                if (!_recordingMarks.TryGetValue(since.Trim(), out int mark))
                    throw new ArgumentException($"No checkpoint called \"{since}\" was taken while recording. Checkpoints: {string.Join(", ", _recordingMarks.Keys.Select(k => $"\"{k}\""))}.");
                from = Math.Max(0, mark - _recordingDropped);
            }
            var steps = new JsonArray();
            foreach (var (tool, args) in _recording.Skip(from)) steps.Add(StepJson(tool, args));
            var json = new JsonObject { ["count"] = steps.Count, ["steps"] = steps };
            if (since != null && _recordingMarks[since.Trim()] < _recordingDropped) json["note"] = "Some of the steps since then were dropped: the recording keeps the 5,000 newest.";
            return json;
        }, cancel);

    [McpServerTool(Name = "save_steps", Destructive = true)]
    [Description("Keeps steps as a steps file (.avasteps.json) in the working folder, to run again with run_steps: a procedural model or a routine. The steps are given, or the recording's (since a checkpoint, or all of it). parameters names the values that can change, each with its default and description; in the steps, a value \"${name}\" is replaced by the parameter's value, \"${height * 1.2}\" by a sum of parameters and numbers (+ - * / and brackets), and ${name} inside longer text by the value as text. Writes no file that exists unless overwrite is true. Returns the file and what it holds.")]
    public Task<string> SaveSteps(
        [Description("The file, relative to the working folder; .avasteps.json is added if it isn't there.")] string path,
        [Description("The steps ([{\"tool\": ..., \"args\": {...}}]). Default: the recording.")] BatchStep[]? steps = null,
        [Description("With no steps given: only the recording's steps since this checkpoint.")] string? since = null,
        [Description("The parameters by name: {\"height\": {\"default\": 0.3, \"description\": \"the vase's height in metres\"}}.")] Dictionary<string, StepParameter>? parameters = null,
        [Description("What the steps make or do, for whoever runs them.")] string? description = null,
        [Description("Replace the file if it exists.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Do("saved steps", c =>
        {
            var files = Files(c);
            var full = files.Resolve(StepsPath(path));
            JsonArray list;
            if (steps != null) list = new JsonArray([.. steps.Select(s => (JsonNode)StepJson(s.Tool, s.Args))]);
            else
            {
                var recorded = (JsonObject)JsonNode.Parse(GetRecordingNow(since))!;
                list = (JsonArray)recorded["steps"]!.DeepClone();
            }
            if (list.Count == 0) throw new InvalidOperationException("There are no steps to save: give steps, or change something first so the recording has some.");
            var parameterJson = new JsonObject();
            foreach (var (name, p) in parameters ?? [])
            {
                if (!IsName(name)) throw new ArgumentException($"\"{name}\" can't be a parameter's name: use letters, digits and _ (starting with a letter).");
                var pj = new JsonObject();
                if (p.Default is { } d) pj["default"] = JsonNode.Parse(d.GetRawText());
                if (p.Description != null) pj["description"] = p.Description;
                parameterJson[name] = pj;
            }
            // The steps must name only the parameters there are.
            foreach (var used in Placeholders(list))
                if (!parameterJson.ContainsKey(used.Split(Separators, StringSplitOptions.RemoveEmptyEntries)[0]) && Names(used).FirstOrDefault(n => !parameterJson.ContainsKey(n)) is { } unknown)
                    throw new ArgumentException($"The steps use ${{{used}}}, but \"{unknown}\" isn't one of the parameters. Add it to parameters.");
            var file = new JsonObject
            {
                ["format"] = "avamodeler-steps",
                ["version"] = 1,
                ["description"] = description,
                ["parameters"] = parameterJson,
                ["steps"] = list,
            };
            files.CheckWritable([full], overwrite);
            File.WriteAllText(full, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            files.Wrote(full);
            return new JsonObject
            {
                ["file"] = Path.GetRelativePath(files.Root, full).Replace('\\', '/'),
                ["steps"] = list.Count,
                ["parameters"] = new JsonArray([.. parameterJson.Select(kv => (JsonNode)kv.Key)]),
            };
        }, cancel);

    [McpServerTool(Name = "run_steps", Destructive = true)]
    [Description("Runs a steps file (save_steps) or steps given here, with values for their parameters, as one batch: one undo step, all or nothing (see batch). Each \"${...}\" in the steps becomes its value: a parameter's (values, else its default), or a sum of them. With dry_run, says what they would change and changes nothing. Returns the batch's answer and the values used.")]
    public async Task<string> RunSteps(
        McpServer server,
        [Description("A steps file in the working folder (.avasteps.json).")] string? path = null,
        [Description("Steps to run instead of a file.")] BatchStep[]? steps = null,
        [Description("The parameters' values by name, e.g. {\"height\": 0.36}. Those left out take their defaults.")] Dictionary<string, JsonElement>? values = null,
        [Description("Say what they would change, then take it all back.")] bool dry_run = false,
        [Description("The undo step's name (default: the file's name).")] string? label = null,
        CancellationToken cancel = default)
    {
        if ((path == null) == (steps == null)) throw new McpException("Give a steps file (path) or steps, one of them.");
        JsonArray list;
        var parameters = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        string title = label ?? "Run steps";
        if (path != null)
        {
            var text = await Do("read steps", c =>
            {
                var full = Files(c).Resolve(StepsPath(path));
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no steps file {Path.GetFileName(full)} in the working folder.");
                return File.ReadAllText(full);
            }, cancel).ConfigureAwait(false);
            JsonObject file;
            try { file = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not an object"); }
            catch (JsonException e) { throw new McpException($"{path} isn't a steps file: {e.Message}"); }
            if ((string?)file["format"] != "avamodeler-steps") throw new McpException($"{path} isn't a steps file (its format isn't avamodeler-steps).");
            list = file["steps"] as JsonArray ?? throw new McpException($"{path} has no steps.");
            foreach (var (name, p) in file["parameters"] as JsonObject ?? [])
                parameters[name] = p?["default"]?.DeepClone();
            title = label ?? Path.GetFileName(path).Replace(StepsExtension, "", StringComparison.OrdinalIgnoreCase);
        }
        else list = new JsonArray([.. steps!.Select(s => (JsonNode)StepJson(s.Tool, s.Args))]);
        foreach (var (name, value) in values ?? [])
            parameters[name] = JsonNode.Parse(value.GetRawText());

        BatchStep[] filled;
        try
        {
            filled = [.. list.Select(step => new BatchStep
            {
                Tool = (string?)step?["tool"] ?? "",
                Args = step?["args"] is JsonObject a
                    ? ((JsonObject)Substitute(a, parameters)!).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value))
                    : null,
            })];
        }
        catch (FormatException e) { throw new McpException(e.Message); }

        var answer = (JsonObject)JsonNode.Parse(await Batch(server, filled, title, dry_run, cancel).ConfigureAwait(false))!;
        if (!dry_run) foreach (var s in filled) if (server.ServerOptions.ToolCollection?.TryGetPrimitive(s.Tool, out var t) == true && t.ProtocolTool.Annotations?.ReadOnlyHint != true) Add(s.Tool, s.Args);
        answer["values"] = new JsonObject([.. parameters.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))]);
        return answer.ToJsonString();
    }

    private string GetRecordingNow(string? since)
    {
        int from = 0;
        if (since != null)
        {
            if (!_recordingMarks.TryGetValue(since.Trim(), out int mark))
                throw new ArgumentException($"No checkpoint called \"{since}\" was taken while recording.");
            from = Math.Max(0, mark - _recordingDropped);
        }
        return new JsonObject { ["steps"] = new JsonArray([.. _recording.Skip(from).Select(r => (JsonNode)StepJson(r.Tool, r.Args))]) }.ToJsonString();
    }

    private static string StepsPath(string path) =>
        path.EndsWith(StepsExtension, StringComparison.OrdinalIgnoreCase) ? path : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + StepsExtension;

    private static JsonObject StepJson(string tool, IReadOnlyDictionary<string, JsonElement>? args)
    {
        var json = new JsonObject { ["tool"] = tool };
        if (args is { Count: > 0 }) json["args"] = new JsonObject([.. args.Select(kv => KeyValuePair.Create(kv.Key, JsonNode.Parse(kv.Value.GetRawText())))]);
        return json;
    }

    // ───────────────────────────── ${…} placeholders ─────────────────────────────

    private static readonly char[] Separators = [' ', '+', '-', '*', '/', '(', ')'];

    private static bool IsName(string s) => s.Length > 0 && char.IsAsciiLetter(s[0]) && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static IEnumerable<string> Names(string expression) =>
        expression.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(t => !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static IEnumerable<string> Placeholders(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o: foreach (var (_, v) in o) foreach (var p in Placeholders(v)) yield return p; break;
            case JsonArray a: foreach (var v in a) foreach (var p in Placeholders(v)) yield return p; break;
            case JsonValue v when v.TryGetValue(out string? s):
                int at = 0;
                while ((at = s.IndexOf("${", at, StringComparison.Ordinal)) >= 0)
                {
                    int end = s.IndexOf('}', at);
                    if (end < 0) break;
                    yield return s[(at + 2)..end].Trim();
                    at = end + 1;
                }
                break;
        }
    }

    /// <summary>A copy of <paramref name="node"/> with every "${…}" replaced (see save_steps).</summary>
    internal static JsonNode? Substitute(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> values)
    {
        switch (node)
        {
            case JsonObject o:
                return new JsonObject([.. o.Select(kv => KeyValuePair.Create(kv.Key, Substitute(kv.Value, values)))]);
            case JsonArray a:
                return new JsonArray([.. a.Select(v => Substitute(v, values))]);
            case JsonValue v when v.TryGetValue(out string? s) && s.Contains("${", StringComparison.Ordinal):
                var whole = s.Trim();
                if (whole.StartsWith("${", StringComparison.Ordinal) && whole.EndsWith('}') && whole.IndexOf("${", 2, StringComparison.Ordinal) < 0)
                {
                    var expression = whole[2..^1].Trim();
                    if (IsName(expression)) return Value(expression, values)?.DeepClone();
                    return JsonValue.Create(Evaluate(expression, values));
                }
                var sb = new StringBuilder();
                int at = 0;
                while (true)
                {
                    int start = s.IndexOf("${", at, StringComparison.Ordinal);
                    if (start < 0) { sb.Append(s, at, s.Length - at); break; }
                    int end = s.IndexOf('}', start);
                    if (end < 0) throw new FormatException($"\"{s}\" has a ${{ with no }}.");
                    sb.Append(s, at, start - at);
                    var expression = s[(start + 2)..end].Trim();
                    sb.Append(IsName(expression)
                        ? Value(expression, values) switch { JsonValue jv when jv.TryGetValue(out string? text) => text, var other => other?.ToJsonString() ?? "" }
                        : Evaluate(expression, values).ToString(CultureInfo.InvariantCulture));
                    at = end + 1;
                }
                return JsonValue.Create(sb.ToString());
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? Value(string name, IReadOnlyDictionary<string, JsonNode?> values) =>
        values.TryGetValue(name, out var v) ? v : throw new FormatException($"The steps use ${{{name}}}, but there's no value for {name} (and no default).");

    /// <summary>A sum of numbers and number parameters: + - * / and brackets.</summary>
    private static double Evaluate(string expression, IReadOnlyDictionary<string, JsonNode?> values)
    {
        int at = 0;
        double Number()
        {
            Skip();
            if (at < expression.Length && expression[at] == '(')
            {
                at++;
                double inner = Sum();
                Skip();
                if (at >= expression.Length || expression[at] != ')') throw Bad("a ) is missing");
                at++;
                return inner;
            }
            if (at < expression.Length && expression[at] == '-') { at++; return -Number(); }
            int start = at;
            while (at < expression.Length && (char.IsAsciiLetterOrDigit(expression[at]) || expression[at] is '.' or '_')) at++;
            var token = expression[start..at];
            if (token.Length == 0) throw Bad("a number or name is missing");
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            var v = Value(token, values);
            if (v is JsonValue jv && jv.TryGetValue(out double d)) return d;
            throw Bad($"{token} isn't a number");
        }
        double Product()
        {
            double x = Number();
            while (true)
            {
                Skip();
                if (at < expression.Length && expression[at] is '*' or '/')
                {
                    char op = expression[at++];
                    double y = Number();
                    x = op == '*' ? x * y : x / y;
                }
                else return x;
            }
        }
        double Sum()
        {
            double x = Product();
            while (true)
            {
                Skip();
                if (at < expression.Length && expression[at] is '+' or '-')
                {
                    char op = expression[at++];
                    double y = Product();
                    x = op == '+' ? x + y : x - y;
                }
                else return x;
            }
        }
        void Skip() { while (at < expression.Length && expression[at] == ' ') at++; }
        FormatException Bad(string why) => new($"${{{expression}}} can't be worked out: {why}.");
        double result = Sum();
        Skip();
        if (at != expression.Length) throw Bad($"\"{expression[at..]}\" is left over");
        if (!double.IsFinite(result)) throw Bad("it isn't a finite number");
        return result;
    }
}
