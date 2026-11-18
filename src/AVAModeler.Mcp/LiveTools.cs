using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// Working beside the person and other agents: locks (geometry and scene items kept from agents' changes), the
/// sessions on the host (list_agents), background jobs with progress (start_job, job_status, job_result,
/// cancel_job), and the resources a client can read and subscribe to.
/// </summary>
public sealed partial class ModelerTools
{
    // ───────────────────────────── locks ─────────────────────────────

    [McpServerTool(Name = "lock")]
    [Description("Keeps geometry or scene items from agents' changes, yours and other agents' alike (the person's own edits aren't stopped): the current layer's selected points and polygons (a polygon's points with it), or scene items by name. Any agent's call that would move, remove or rebuild what's locked fails, changes nothing and says which lock (its error kind is locked). Locks hold by where things are, not by index, so renumbering the mesh doesn't loosen them. Use it to protect finished parts while you, or another agent, work on the rest. You can unlock only your own; the person's and other agents' are theirs. Your locks go when your session ends. Not an undo step.")]
    public Task<string> Lock(
        [Description("A name for the lock (\"finished legs\").")] string name,
        [Description("Scene items to lock by name, instead of the selection.")] string[]? items = null,
        [Description("Why, for whoever runs into it.")] string? note = null,
        CancellationToken cancel = default) =>
        DoScene("locked", (c, l) =>
        {
            var key = (name ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("Give the lock a name.");
            var doc = c.Document;
            if (doc.AgentLocks.Any(x => x.Name == key) || l.AgentLocks.Any(x => x.Name == key))
                throw new ArgumentException($"There's a lock called \"{key}\" already: choose another name, or unlock it first.");
            if (items is { Length: > 0 })
            {
                foreach (var item in items) SceneCensus.Item(l.Scene, item);
                l.AgentLocks.Add(new SceneLock(key, SessionName, [.. items], note));
                return new JsonObject { ["locked"] = key, ["items"] = new JsonArray([.. items.Select(i => (JsonNode)i)]) };
            }
            int layer = doc.CurrentLayer;
            var points = doc.Selection.PointsIn(layer).ToList();
            var polygons = doc.Selection.PolygonsIn(layer).ToList();
            if (points.Count + polygons.Count == 0) throw new ArgumentException("Nothing is selected in the current layer: select what to lock (or give items for scene items).");
            var made = AgentLock.Of(doc, layer, points, polygons, key, SessionName, note);
            doc.AgentLocks.Add(made);
            return new JsonObject { ["locked"] = key, ["layer"] = layer + 1, ["points"] = made.Points.Count, ["polygons"] = made.Polygons.Count };
        }, cancel, changes: false);

    [McpServerTool(Name = "unlock")]
    [Description("Takes off one of your locks (lock), by name. The person's locks, and other agents', aren't yours to take off: ask the person.")]
    public Task<string> Unlock(
        [Description("The lock's name (list_locks lists them).")] string name,
        CancellationToken cancel = default) =>
        DoScene("unlocked", (c, l) =>
        {
            var key = (name ?? "").Trim();
            var geometry = c.Document.AgentLocks.FirstOrDefault(x => x.Name == key);
            var scene = l.AgentLocks.FirstOrDefault(x => x.Name == key);
            string owner = geometry?.Owner ?? scene?.Owner ?? throw new ArgumentException($"There's no lock called \"{key}\".");
            if (owner != SessionName)
                throw new LockedException(owner == AgentLock.Person
                    ? $"\"{key}\" is the person's lock: only they can take it off (Macros › Unlock for AI Agents, or the Layout item panel)."
                    : $"\"{key}\" is {owner}'s lock, not yours: it goes when that session ends.");
            if (geometry != null) c.Document.AgentLocks.Remove(geometry);
            if (scene != null) l.AgentLocks.Remove(scene);
            return new JsonObject { ["unlocked"] = key };
        }, cancel, changes: false);

    [McpServerTool(Name = "list_locks", ReadOnly = true, Idempotent = true)]
    [Description("Lists what's locked from agents: each lock's name, whose it is (\"you\", \"the person\" or another session), its note, and what it holds (a layer's points and polygons, or scene items).")]
    public Task<string> ListLocks(CancellationToken cancel = default) =>
        DoScene("listed locks", (c, l) => LocksJson(c.Document, l), cancel, changes: false);

    private JsonObject LocksJson(Document doc, LayoutSession l)
    {
        string Owner(string o) => o == SessionName ? "you" : o;
        return new JsonObject
        {
            ["locks"] = new JsonArray([
                .. doc.AgentLocks.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.Name, ["owner"] = Owner(x.Owner), ["note"] = x.Note,
                    ["layer"] = x.Layer + 1, ["points"] = x.Points.Count, ["polygons"] = x.Polygons.Count,
                }),
                .. l.AgentLocks.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.Name, ["owner"] = Owner(x.Owner), ["note"] = x.Note,
                    ["items"] = new JsonArray([.. x.Items.Select(i => (JsonNode)i)]),
                }),
            ]),
        };
    }

    /// <summary>
    /// Runs a call's work so that, when anything is locked from agents (or the person should be asked first), it is all
    /// or nothing: one group (one undo step), checked against the locks (and the size of what it removes) before it
    /// stands. A call made inside another (a batch's step) is checked too, and fails its batch.
    /// </summary>
    private T Guarded<T>(Commands c, LayoutSession? l, string action, Func<T> work)
    {
        var doc = c.Document;
        bool sceneLocks = l is { AgentLocks.Count: > 0 };
        bool locks = doc.AgentLocks.Count > 0 || sceneLocks;
        bool ask = _depth == 1 && ShouldAsk();
        if (!locks && !ask) return work();
        var before = ask ? doc.Layers.Select(x => x.Mesh.Polygons.Count).ToArray() : [];
        var sceneBefore = sceneLocks ? SceneText(l!.Scene) : null;
        return doc.Group(Capitalised(action), () =>
        {
            var result = work();
            foreach (var x in doc.AgentLocks)
                if (x.Problem(doc) is { } problem)
                    throw new LockedException($"This would change what's locked from agents: \"{x.Name}\" ({(x.Owner == SessionName ? "your lock" : x.Owner == AgentLock.Person ? "the person's lock" : $"{x.Owner}'s lock")}{(x.Note != null ? $": {x.Note}" : "")}): {problem}. Nothing was changed. Work on something else, or {(x.Owner == SessionName ? "unlock it first" : "ask the person")}.");
            if (sceneLocks && SceneText(l!.Scene) is { } after && sceneBefore != null && after != sceneBefore)
                foreach (var x in l.AgentLocks)
                    if (LockedItemsChanged(sceneBefore, after, x.Items) is { Count: > 0 } changed)
                        throw new LockedException($"This would change {string.Join(", ", changed)}, locked from agents as \"{x.Name}\" ({(x.Owner == SessionName ? "your lock" : x.Owner)}). Nothing was changed.");
            if (ask && BigRemoval(doc, before) is { } question)
                throw new NeedsConfirmationException($"{question} ({action}). Go ahead?");
            return result;
        });
    }

    private static string Capitalised(string action) => action.Length == 0 ? action : char.ToUpperInvariant(action[0]) + action[1..];

    /// <summary>The locked items whose settings or keys a change altered (or that it removed).</summary>
    private static List<string> LockedItemsChanged(string before, string after, IReadOnlyList<string> items)
    {
        static Dictionary<string, string> ByName(string text) =>
            (JsonNode.Parse(text)?["items"] as JsonArray ?? [])
                .GroupBy(i => (string?)i?["name"] ?? "").ToDictionary(g => g.Key, g => g.First()!.ToJsonString());
        var a = ByName(before);
        var b = ByName(after);
        return [.. items.Where(n => !b.TryGetValue(n, out var now) || !a.TryGetValue(n, out var was) || now != was)];
    }

    // ───────────────────────────── asking the person ─────────────────────────────

    /// <summary>Whether to ask before a big removal: in the live app, when the client can ask the person.</summary>
    private bool ShouldAsk() =>
        AskBeforeBigRemovals && host is LiveHost && (Scope.Value?.Server ?? Server)?.ClientCapabilities?.Elicitation != null && Scope.Value is not { Confirmed: true };

    /// <summary>Ask the person (through the client) before a call removes most of a layer, in the live app (default on).</summary>
    public bool AskBeforeBigRemovals { get; set; } = true;

    /// <summary>What a change removed that's worth asking about: most of a layer's polygons, or all of them.</summary>
    private static string? BigRemoval(Document doc, int[] before)
    {
        for (int i = 0; i < before.Length && i < doc.Layers.Count; i++)
        {
            int was = before[i], now = doc.Layers[i].Mesh.Polygons.Count;
            if (was >= 20 && now * 2 <= was || was >= 4 && now == 0)
                return $"This would remove {was - now:N0} of the {was:N0} polygons in layer {i + 1}";
        }
        return null;
    }

    /// <summary>
    /// Runs a call; if it asks for confirmation, asks the person (MCP elicitation, which the in-app chat answers with
    /// buttons) and runs it again if they agree.
    /// </summary>
    private async Task<T> Confirming<T>(Func<Task<T>> run, CancellationToken cancel)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (NeedsConfirmationException question)
        {
            var server = Scope.Value?.Server ?? Server ?? throw new DeclinedException("The change needs the person's go-ahead, and there's no one to ask.");
            ElicitResult answer;
            try
            {
                answer = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = question.Message,
                    RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = { } },
                }, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is McpException or InvalidOperationException or NotSupportedException)
            {
                throw new DeclinedException($"{question.Message} The person couldn't be asked ({e.Message}), so nothing was changed.");
            }
            if (answer.Action != "accept")
                throw new DeclinedException($"The person said no: {question.Message.Replace(" Go ahead?", "", StringComparison.Ordinal)}. Nothing was changed. Ask them what they want, or do less at a time.");
            var scope = Scope.Value ??= new CallScope();
            scope.Confirmed = true;
            return await run().ConfigureAwait(false);
        }
    }

    // ───────────────────────────── other sessions ─────────────────────────────

    [McpServerTool(Name = "list_agents", ReadOnly = true, Idempotent = true)]
    [Description("The agent sessions working on this object now (in the live app: every connected agent and the in-app chat): each one's name, which is you, when it joined, its last call and when, how many calls it has made, and its locks. Check it before big changes when others are at work, and lock what you're working on so they keep off it.")]
    public Task<string> ListAgents(CancellationToken cancel = default) =>
        DoScene("listed agents", (c, l) =>
        {
            var sessions = AgentSessions.For(host).All;
            return new JsonObject
            {
                ["agents"] = new JsonArray([.. sessions.Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name,
                    ["you"] = s.Name == SessionName,
                    ["joined"] = Utc(s.Joined),
                    ["last_call"] = s.LastTool,
                    ["last_call_at"] = s.LastAt is { } at ? Utc(at) : null,
                    ["calls"] = s.Calls,
                    ["locks"] = new JsonArray([.. c.Document.AgentLocks.Where(x => x.Owner == s.Name).Select(x => (JsonNode)x.Name)
                        .Concat(l.AgentLocks.Where(x => x.Owner == s.Name).Select(x => (JsonNode)x.Name))]),
                })]),
            };
        }, cancel, changes: false);

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    // ───────────────────────────── jobs ─────────────────────────────

    private sealed class Job(int id, string tool)
    {
        public int Id { get; } = id;
        public string Tool { get; } = tool;
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Ended;
        public string State = "running";
        public double Progress;
        public string? Message;
        public CallToolResult? Result;
        public readonly CancellationTokenSource Cancel = new();
        public Task? Task;
    }

    private sealed class JobProgress(Job job) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            job.Progress = value.Total is > 0 ? value.Progress / value.Total.Value : value.Progress;
            if (value.Message != null) job.Message = value.Message;
        }
    }

    private readonly List<Job> _jobs = [];
    private int _jobCount;

    /// <summary>How many finished jobs are kept for job_result.</summary>
    public const int MaxJobs = 20;

    [McpServerTool(Name = "start_job")]
    [Description("Starts a call in the background and returns at once with its job id, for long work (render_animation, render_scene at size, bake_maps, slice_to_gcode, a big run_tool Remesh): you can carry on with other calls meanwhile (they wait their turn on the object as usual), check job_status for its progress, take its answer with job_result when it's done, or cancel_job. The call's arguments are checked first. Jobs are this session's.")]
    public async Task<string> StartJob(
        McpServer server,
        [Description("The tool to run, e.g. \"render_animation\".")] string tool,
        [Description("Its arguments, as you'd give them calling it.")] Dictionary<string, JsonElement>? args = null,
        CancellationToken cancel = default)
    {
        var tools = server.ServerOptions.ToolCollection ?? throw new McpException("This server has no tools.");
        if (tool is "start_job" or "job_status" or "job_result" or "cancel_job" or "batch") throw new McpException($"{tool} can't be a job: call it on its own.");
        if (!tools.TryGetPrimitive(tool, out var target)) throw new McpException($"This server has no tool called \"{tool}\".");
        if (ModelerServer.Arguments.Check(target.ProtocolTool, args) is { } problem) throw new McpException(problem);
        Job job;
        lock (_jobs)
        {
            job = new Job(++_jobCount, tool);
            _jobs.Add(job);
            var done = _jobs.Where(j => j.State != "running").ToList();
            foreach (var old in done.Take(Math.Max(0, done.Count - MaxJobs))) _jobs.Remove(old);
        }
        job.Task = Task.Run(async () =>
        {
            // Its own call scope, so its progress goes to its status.
            Scope.Value = new CallScope { Progress = new JobProgress(job) };
            var request = new CallToolRequestParams { Name = tool, Arguments = args };
            try
            {
                var result = await target.InvokeAsync(new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, request), job.Cancel.Token).ConfigureAwait(false);
                Finish(request, target.ProtocolTool, result, Scope.Value);
                job.Result = result;
                job.State = result.IsError == true ? "failed" : "done";
                if (job.State == "done") job.Progress = 1;
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
            }
            catch (Exception e)
            {
                job.Result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Explain(e is McpException { InnerException: { } i } ? i : e) }] };
                job.State = "failed";
            }
            job.Ended = DateTime.UtcNow;
        });
        await Task.Yield();
        return new JsonObject { ["job"] = job.Id, ["tool"] = tool, ["state"] = job.State }.ToJsonString();
    }

    [McpServerTool(Name = "job_status", ReadOnly = true)]
    [Description("A background job's state (running, done, failed or cancelled), its progress (0 to 1) and last message (\"frame 12 of 30\"), and how long it has taken; with no job given, every job this session has.")]
    public Task<string> JobStatus(
        [Description("The job's id (start_job gives it). Default: all of them.")] int? job = null,
        CancellationToken cancel = default)
    {
        List<Job> list;
        lock (_jobs) list = job is { } id ? [FindJob(id)] : [.. _jobs];
        JsonObject Describe(Job j) => new()
        {
            ["job"] = j.Id,
            ["tool"] = j.Tool,
            ["state"] = j.State,
            ["progress"] = Math.Round(j.Progress, 3),
            ["message"] = j.Message,
            ["seconds"] = Math.Round(((j.Ended ?? DateTime.UtcNow) - j.Started).TotalSeconds, 1),
            ["error"] = j.State == "failed" ? string.Join("\n", j.Result?.Content.OfType<TextContentBlock>().Select(t => t.Text) ?? []) : null,
        };
        return Task.FromResult((job == null ? new JsonObject { ["jobs"] = new JsonArray([.. list.Select(j => (JsonNode)Describe(j))]) } : Describe(list[0])).ToJsonString());
    }

    [McpServerTool(Name = "job_result", ReadOnly = true)]
    [Description("A finished job's answer, exactly as the call would have given it (pictures included). Fails while it's still running (job_status shows how far it has got).")]
    public Task<CallToolResult> JobResult(
        [Description("The job's id.")] int job,
        [Description("Wait up to this many seconds for it to finish (0 to 300; default 0).")] double wait = 0,
        CancellationToken cancel = default) => JobResultAsync(job, wait, cancel);

    private async Task<CallToolResult> JobResultAsync(int id, double wait, CancellationToken cancel)
    {
        Job j;
        lock (_jobs) j = FindJob(id);
        if (j.State == "running" && wait > 0 && j.Task is { } t)
            await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0, 300)), cancel)).ConfigureAwait(false);
        return j.State switch
        {
            "running" => throw new McpException($"Job {j.Id} ({j.Tool}) is still running ({j.Progress:P0}{(j.Message != null ? $", {j.Message}" : "")}): ask again later, or give wait."),
            "cancelled" => throw new McpException($"Job {j.Id} ({j.Tool}) was cancelled."),
            _ => j.Result!,
        };
    }

    [McpServerTool(Name = "cancel_job")]
    [Description("Stops a running background job. What it had already done to the object stays (a call that changes the object is one undo step all the same); a render stops where it is.")]
    public Task<string> CancelJob(
        [Description("The job's id.")] int job,
        CancellationToken cancel = default)
    {
        Job j;
        lock (_jobs) j = FindJob(job);
        if (j.State == "running") j.Cancel.Cancel();
        return Task.FromResult(new JsonObject { ["job"] = j.Id, ["state"] = j.State == "running" ? "cancelling" : j.State }.ToJsonString());
    }

    private Job FindJob(int id) =>
        _jobs.FirstOrDefault(j => j.Id == id) ?? throw new McpException($"There's no job {id}{(_jobs.Count == 0 ? ": start one with start_job" : $". Jobs: {string.Join(", ", _jobs.Select(j => j.Id))}")}.");

    // ───────────────────────────── resources ─────────────────────────────

    /// <summary>The resources the server offers, by URI: what each is.</summary>
    public static readonly IReadOnlyList<(string Uri, string Name, string Description)> ResourceList =
    [
        ("avamodeler://object", "The object", "The object's layers, counts, bounds, surfaces, selection and undo history (as get_document gives them)."),
        ("avamodeler://scene", "The scene", "Layout's scene at the current frame: items, keys, lights, camera and settings (as get_scene gives them)."),
        ("avamodeler://history", "The undo histories", "The object's and the scene's undo steps, by name."),
        ("avamodeler://checkpoints", "This session's checkpoints", "The checkpoints this session has taken."),
        ("avamodeler://locks", "Locks", "What's locked from agents, and whose locks they are."),
        ("avamodeler://agents", "Agents", "The agent sessions working on this object."),
    ];

    /// <summary>A resource's contents as JSON text.</summary>
    internal async Task<string> ReadResourceAsync(string uri, CancellationToken cancel)
    {
        switch (uri)
        {
            case "avamodeler://object":
                return await DoScene("read the object", (c, l) => Census.Document(c.Document, brief: false), cancel, changes: false).ConfigureAwait(false);
            case "avamodeler://scene":
                return await DoScene("read the scene", (c, l) => SceneCensus.Scene(l, c.Document, l.Frame), cancel, changes: false).ConfigureAwait(false);
            case "avamodeler://history":
                return await DoScene("read the history", (c, l) => new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["undo"] = new JsonArray([.. c.Document.Undo.UndoLabels.Reverse().Select(x => (JsonNode)x)]),
                        ["redo"] = new JsonArray([.. c.Document.Undo.RedoLabels.Select(x => (JsonNode)x)]),
                    },
                    ["scene"] = SceneCensus.UndoState(l),
                }, cancel, changes: false).ConfigureAwait(false);
            case "avamodeler://checkpoints":
                return await ListCheckpoints(cancel).ConfigureAwait(false);
            case "avamodeler://locks":
                return await ListLocks(cancel).ConfigureAwait(false);
            case "avamodeler://agents":
                return await ListAgents(cancel).ConfigureAwait(false);
            default:
                throw new McpException($"There's no resource {uri}. There are: {string.Join(", ", ResourceList.Select(r => r.Uri))}.");
        }
    }

    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);

    /// <summary>
    /// The object (first) or the scene (second) was changed by the person or another session (raised a quarter of a
    /// second after a burst of changes): the in-app chat tells its model with the next message.
    /// </summary>
    public event Action<bool, bool>? ChangedElsewhere;
    private Commands? _watchedCommands;
    private Document? _watchedDocument;
    private LayoutSession? _watchedLayout;
    private int _pendingChanges;
    private bool _objectChanged, _sceneChanged;

    internal void Subscribe(string uri)
    {
        if (!ResourceList.Any(r => r.Uri == uri)) throw new McpException($"There's no resource {uri}.");
        lock (_subscribed) _subscribed.Add(uri);
    }

    internal void Unsubscribe(string uri)
    {
        lock (_subscribed) _subscribed.Remove(uri);
    }

    /// <summary>
    /// Follows the object and the scene (hooked at each call, on the host's thread, so a new document is followed too)
    /// so subscribers hear when the person, or another session, changes them. A change this session makes itself isn't
    /// announced to it.
    /// </summary>
    private void Hook(Commands c, LayoutSession? l)
    {
        if (!ReferenceEquals(_watchedCommands, c))
        {
            if (_watchedCommands != null) _watchedCommands.DocumentReplaced -= OnDocumentReplaced;
            _watchedCommands = c;
            c.DocumentReplaced += OnDocumentReplaced;
        }
        if (!ReferenceEquals(_watchedDocument, c.Document))
        {
            if (_watchedDocument != null) _watchedDocument.Changed -= OnObjectChanged;
            _watchedDocument = c.Document;
            _watchedDocument.Changed += OnObjectChanged;
        }
        if (l != null && !ReferenceEquals(_watchedLayout, l))
        {
            if (_watchedLayout != null) { _watchedLayout.Changed -= OnSceneChanged; _watchedLayout.SceneReplaced -= OnSceneChanged; }
            _watchedLayout = l;
            l.Changed += OnSceneChanged;
            l.SceneReplaced += OnSceneChanged;
        }
    }

    private void StopWatching()
    {
        if (_watchedCommands != null) _watchedCommands.DocumentReplaced -= OnDocumentReplaced;
        if (_watchedDocument != null) _watchedDocument.Changed -= OnObjectChanged;
        if (_watchedLayout != null) { _watchedLayout.Changed -= OnSceneChanged; _watchedLayout.SceneReplaced -= OnSceneChanged; }
        (_watchedCommands, _watchedDocument, _watchedLayout) = (null, null, null);
    }

    private void OnDocumentReplaced(Document doc)
    {
        if (_watchedDocument != null) _watchedDocument.Changed -= OnObjectChanged;
        _watchedDocument = doc;
        doc.Changed += OnObjectChanged;
        OnObjectChanged();
    }

    private void OnObjectChanged()
    {
        if (_depth > 0) return;
        _objectChanged = true;
        Announce();
    }

    private void OnSceneChanged()
    {
        if (_depth > 0) return;
        _sceneChanged = true;
        Announce();
    }

    /// <summary>Tells subscribers, a quarter of a second after a burst of changes (one notice per resource).</summary>
    private void Announce()
    {
        if (Interlocked.Increment(ref _pendingChanges) != 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            Interlocked.Exchange(ref _pendingChanges, 0);
            bool obj = _objectChanged, scene = _sceneChanged;
            (_objectChanged, _sceneChanged) = (false, false);
            ChangedElsewhere?.Invoke(obj, scene);
            if (Server is not { } server) return;
            static bool Changed(string u, bool obj, bool scene) =>
                u == "avamodeler://object" && obj || u == "avamodeler://scene" && scene || u is "avamodeler://history" or "avamodeler://locks" && (obj || scene);
            List<string> uris;
            // From the 2026-07-28 revision clients subscribe with subscriptions/listen, which the SDK serves and routes
            // notifications by: every changed resource is sent and it delivers each to whoever listens for it.
            bool listens = server.NegotiatedProtocolVersion is { } v && string.CompareOrdinal(v, "2026-07-28") >= 0;
            lock (_subscribed)
                uris = listens
                    ? [.. ResourceList.Select(r => r.Uri).Where(u => Changed(u, obj, scene))]
                    : [.. _subscribed.Where(u => Changed(u, obj, scene))];
            foreach (var uri in uris)
            {
                try
                {
                    await server.SendNotificationAsync(NotificationMethods.ResourceUpdatedNotification, new ResourceUpdatedNotificationParams { Uri = uri }).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or McpException) { }
            }
        });
    }
}

/// <summary>A call would remove so much that the person should say yes first.</summary>
internal sealed class NeedsConfirmationException(string message) : Exception(message);
