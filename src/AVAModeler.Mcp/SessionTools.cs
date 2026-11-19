using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>One step of a batch: a tool of this server and its arguments.</summary>
public sealed class BatchStep
{
    [JsonPropertyName("tool")]
    [Description("The tool's name, as in the tool list (e.g. \"run_tool\", \"select_polygons\", \"set_surface\").")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("args")]
    [Description("Its arguments, exactly as you'd give them calling it on its own. Optional.")]
    public Dictionary<string, JsonElement>? Args { get; set; }
}

/// <summary>
/// The tools for working in reliable steps: batches that are one undo step and all or nothing, checkpoints to go back
/// to and compare with, and points' and polygons' ids (see <see cref="GeometryIds"/>).
/// </summary>
public sealed partial class ModelerTools
{
    /// <summary>How many steps a batch may have.</summary>
    public const int MaxBatchSteps = 200;

    /// <summary>How many checkpoints are kept (the oldest go first).</summary>
    public const int MaxCheckpoints = 30;

    /// <summary>
    /// Tools a batch can't run: those that replace or write what a batch couldn't take back (new, open, save and
    /// their scene twins, exports and baked maps), step through the history, render pictures (they work away from
    /// the document), or are about the session itself.
    /// </summary>
    public static readonly IReadOnlySet<string> NotInBatch = new HashSet<string>(StringComparer.Ordinal)
    {
        "batch", "checkpoint", "restore_checkpoint", "list_checkpoints", "diff",
        "undo", "redo", "undo_scene", "redo_scene", "get_history",
        "new", "open", "save", "new_scene", "open_scene", "save_scene", "export_scene", "bake_maps", "bake_lighting", "slice_to_gcode",
        "render_view", "render", "render_scene", "render_scene_view", "render_animation", "make_movie", "compare_to_reference",
        "render_checkpoint", "run_steps", "save_steps", "start_job", "job_status", "job_result", "cancel_job", "lock", "unlock",
    };

    private sealed record Checkpoint(
        string Name, DateTime Taken, string? Note, Document Document, UndoStack.Snapshot Object, int Layers,
        GeometryIds.State Ids, AVAModeler.Scene.Scene Scene, SceneSnapshot SceneState, string? SceneJson, string[] Items)
    {
        /// <summary>Kept on disk (persist), so it outlives the session.</summary>
        public bool Kept { get; init; }
    }

    private readonly List<Checkpoint> _checkpoints = [];
    private int _checkpointCount;

    // ───────────────────────────── batches ─────────────────────────────

    [McpServerTool(Name = "batch", Destructive = true)]
    [Description("Runs several of this server's tools in order as one step: all of them or none. If a step fails, everything the steps before it did to the object and the scene is taken back and the call fails saying which step and why. The object's changes become one undo step (undo takes the whole batch back) and the scene's one step on Layout's history. Each step is {\"tool\": name, \"args\": {...}} with the arguments the tool takes on its own; every step's arguments are checked before any runs. Returns each step's answer in order. Steps can't be tools that replace, write or render (new, open, save, new_scene, open_scene, save_scene, export_scene, bake_maps, slice_to_gcode, save_steps, render_view, render, render_scene, render_scene_view, render_animation, render_checkpoint, make_movie, compare_to_reference), step the history (undo, redo, undo_scene, redo_scene) or are about the session (batch, run_steps, checkpoint, restore_checkpoint, list_checkpoints, diff, lock, unlock, start_job, job_status, job_result, cancel_job): call those on their own. expect with fail true as the last step makes the batch stand only if its checks pass. dry_run says what the steps would change and takes them back. In the live app the person sees nothing until the whole batch is done. At most 200 steps.")]
    public async Task<string> Batch(
        McpServer server,
        [Description("The steps, in order: [{\"tool\": \"run_tool\", \"args\": {\"name\": \"Box\"}}, {\"tool\": \"select_polygons\", \"args\": {\"indices\": [0]}}, ...].")] BatchStep[] steps,
        [Description("The undo step's name (default \"Batch of N steps\").")] string? label = null,
        [Description("Run the steps, say what they would change (as diff does), then take it all back: nothing is changed and no undo step is left.")] bool dry_run = false,
        CancellationToken cancel = default)
    {
        if (steps.Length == 0) throw new McpException("Give at least one step.");
        if (steps.Length > MaxBatchSteps) throw new McpException($"A batch has at most {MaxBatchSteps} steps; this has {steps.Length}. Split it.");
        var tools = server.ServerOptions.ToolCollection ?? throw new McpException("This server has no tools to batch.");
        var resolved = new McpServerTool[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            var name = steps[i].Tool?.Trim() ?? "";
            string where = $"Step {i + 1} ({(name.Length > 0 ? name : "no tool named")})";
            if (name.Length == 0) throw new McpException($"Step {i + 1} names no tool: give \"tool\".");
            if (NotInBatch.Contains(name)) throw new McpException($"{where} can't run inside a batch: it replaces, writes or renders what a batch couldn't take back, steps the history, or is about the session. Call it on its own, before or after the batch.");
            if (!tools.TryGetPrimitive(name, out var tool)) throw new McpException($"{where}: this server has no tool called \"{name}\".");
            if (ModelerServer.Arguments.Check(tool.ProtocolTool, steps[i].Args) is { } problem) throw new McpException($"{where}: {problem}");
            resolved[i] = tool;
        }
        string title = string.IsNullOrWhiteSpace(label) ? $"Batch of {steps.Length} step{(steps.Length == 1 ? "" : "s")}" : label.Trim();

        if (dry_run)
            return await DoScene($"tried a batch: {title}", (c, l) =>
            {
                var before = Capture("before", null, c.Document, l);
                JsonObject? result = null;
                try
                {
                    l.Edit(AgentStep($"tried {title}"), () => c.Document.Group(title, () =>
                    {
                        var answers = RunSteps(server, resolved, steps, cancel);
                        var diff = Compare(before, Capture("after", null, c.Document, l));
                        diff.Remove("since");
                        diff.Remove("until");
                        result = new JsonObject { ["dry_run"] = true, ["steps"] = steps.Length, ["would_change"] = diff, ["results"] = answers };
                        throw new DryRunFinished();
                    }), apart: true);
                }
                catch (DryRunFinished) { }
                _ids.Sync(c.Document);
                result!["note"] = "Nothing was changed: the steps were taken back.";
                return result;
            }, cancel, changes: false).ConfigureAwait(false);

        return await DoScene($"batch: {title}", (c, l) =>
        {
            JsonArray answers = [];
            c.Document.Group(title, () =>
            {
                answers = RunSteps(server, resolved, steps, cancel);
            });
            return new JsonObject
            {
                ["done"] = title,
                ["steps"] = steps.Length,
                ["results"] = answers,
                ["undo"] = Census.UndoState(c.Document),
            };
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>Thrown at the end of a dry run's steps so the group and the scene's edit put everything back.</summary>
    private sealed class DryRunFinished : Exception;

    /// <summary>Runs a batch's steps in order, here and now; a failing step throws, naming itself.</summary>
    private JsonArray RunSteps(McpServer server, McpServerTool[] resolved, BatchStep[] steps, CancellationToken cancel)
    {
        var answers = new JsonArray();
        for (int i = 0; i < steps.Length; i++)
        {
            var request = new CallToolRequestParams { Name = resolved[i].ProtocolTool.Name, Arguments = steps[i].Args };
            var context = new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, request);
            string where = $"Step {i + 1} of {steps.Length} ({request.Name})";
            // The step runs here and now (the host runs a call made from inside another at once), so it has
            // finished by the time it returns; one that would go off to work elsewhere can't be part of it.
            var task = resolved[i].InvokeAsync(context, cancel).AsTask();
            if (!task.IsCompleted)
                throw new InvalidOperationException($"{where} can't run inside a batch: it works away from the document. Call it on its own.");
            CallToolResult result;
            try { result = task.GetAwaiter().GetResult(); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{where} failed: {Explain(e is McpException { InnerException: { } inner } ? inner : e)} Nothing was changed: the steps before it were taken back.", e);
            }
            string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            if (result.IsError == true)
                throw new InvalidOperationException($"{where} failed: {text} Nothing was changed: the steps before it were taken back.");
            JsonNode? answer;
            try { answer = JsonNode.Parse(text); }
            catch (JsonException) { answer = text; }
            answers.Add(new JsonObject { ["step"] = i + 1, ["tool"] = request.Name, ["result"] = answer });
        }
        return answers;
    }

    // ───────────────────────────── checkpoints ─────────────────────────────

    [McpServerTool(Name = "checkpoint")]
    [Description("Remembers the object and the scene as they are now, by name, to go back to (restore_checkpoint) or compare with (diff) later. Unlike undo, going back to a checkpoint doesn't depend on what else is in the history, so it can't take back the person's own edits by counting steps wrong. Changes nothing. It lasts as long as this session (the 30 newest are kept), unless persist is true: then it's also kept on disk, in the working folder's .avacheckpoints, for any later session to list, compare with and restore. The same name again replaces it. Returns its name and what it holds.")]
    public Task<string> TakeCheckpoint(
        [Description("A name for it (default \"checkpoint 1\", \"checkpoint 2\" and so on).")] string? name = null,
        [Description("A note to yourself about it, shown by list_checkpoints.")] string? note = null,
        CancellationToken cancel = default,
        [Description("Also keep it on disk (the object, the scene with its meshes, the ids), so it outlives this session.")] bool persist = false) =>
        DoScene("took a checkpoint", (c, l) =>
        {
            var doc = c.Document;
            string n = string.IsNullOrWhiteSpace(name) ? $"checkpoint {++_checkpointCount}" : name.Trim();
            var cp = Capture(n, note, doc, l);
            if (persist)
            {
                Persist(cp);
                cp = cp with { Kept = true };
            }
            _checkpoints.RemoveAll(x => x.Name == n);
            _checkpoints.Add(cp);
            string? dropped = null;
            if (_checkpoints.Count > MaxCheckpoints)
            {
                dropped = _checkpoints[0].Name;
                _checkpoints.RemoveAt(0);
            }
            var json = Describe(cp);
            json["in_session"] = _checkpoints.Count;
            if (dropped != null) json["dropped"] = $"\"{dropped}\", the oldest, to keep {MaxCheckpoints}";
            return json;
        }, cancel, changes: false);

    [McpServerTool(Name = "list_checkpoints", ReadOnly = true, Idempotent = true)]
    [Description("Lists the checkpoints this session has (oldest first): each one's name, when it was taken, its note, and the object's points and polygons and the scene's items then; then those kept on disk (persist) by earlier sessions, which restore_checkpoint, diff and render_checkpoint read back by name.")]
    public Task<string> ListCheckpoints(CancellationToken cancel = default) =>
        DoScene("listed checkpoints", (c, l) => new JsonObject
        {
            ["checkpoints"] = new JsonArray([.. _checkpoints.Select(cp => (JsonNode)Describe(cp))]),
            ["kept_on_disk"] = new JsonArray([.. KeptOnDisk().Where(k => !_checkpoints.Any(x => x.Name == k.Name)).Select(k => (JsonNode)new JsonObject
            {
                ["name"] = k.Name,
                ["taken"] = k.Taken.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                ["note"] = k.Note,
            })]),
        }, cancel, changes: false);

    [McpServerTool(Name = "restore_checkpoint", Destructive = true)]
    [Description("Puts the object and the scene back exactly as they were when the checkpoint was taken: every layer (its points, polygons, maps, name and whether it's in the foreground or background), the surfaces and the selection, and the scene's items, keys and settings. It's one undo step on the object (\"Restore checkpoint <name>\") and one on the scene, so undo and undo_scene bring back what was there just before. Points and polygons get back the ids they had then. The checkpoint stays, to go back to again. If the scene was replaced since (new_scene, open_scene), it is left as it is and the answer says so.")]
    public Task<string> RestoreCheckpoint(
        [Description("The checkpoint's name (list_checkpoints lists them).")] string name,
        CancellationToken cancel = default) =>
        DoScene($"restored checkpoint {name}", (c, l) =>
        {
            var cp = Find(name, c.Document, l);
            var doc = c.Document;
            while (doc.Layers.Count < cp.Layers) doc.Layers.Add(new Layer());
            string label = $"Restore checkpoint {cp.Name}";
            var before = doc.Snapshot(label, Enumerable.Range(0, doc.Layers.Count));
            doc.Revert(cp.Object);
            // Layers added since are emptied (and turned off); the checkpoint had none there.
            for (int i = cp.Layers; i < doc.Layers.Count; i++) doc.Layers[i] = new Layer();
            doc.Commit(before);
            _ids.Load(doc, cp.Ids);

            var json = new JsonObject { ["restored"] = cp.Name, ["object"] = Census.Document(doc, brief: true) };
            if (ReferenceEquals(cp.Scene, l.Scene))
            {
                l.Edit(AgentStep($"restored checkpoint {cp.Name}"), () => cp.SceneState.Restore(), apart: true);
                json["scene_restored"] = true;
                json["undo_scene"] = SceneCensus.UndoState(l);
            }
            else if (cp.Kept && !_checkpoints.Contains(cp))
            {
                // Read back from disk: the scene it kept becomes the scene (its own copy, so the Modeler's object in it is
                // a copy too, no longer the live one), and the scene's undo history starts again.
                l.Replace(cp.Scene, l.ScenePath);
                json["scene_restored"] = true;
                json["scene_note"] = "The scene was read back from disk: its undo history starts again, and an object that followed the Modeler's live is a copy now.";
            }
            else
            {
                json["scene_restored"] = false;
                json["scene_note"] = "The scene was replaced (new_scene or open_scene) since the checkpoint, so it was left as it is.";
            }
            if (!ReferenceEquals(cp.Document, doc))
                json["object_note"] = "Another object was made or opened since the checkpoint; its layers are now the checkpoint's.";
            json["undo"] = Census.UndoState(doc);
            return json;
        }, cancel, changes: false);

    [McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true)]
    [Description("What changed since a checkpoint (or between two: since, then until), by id rather than index, so it says what a tool (or the person) really did: for each layer that changed, the points added, removed and moved (with the largest move in metres) and the polygons added, removed and changed (other points, or another surface), the layer's name and state, and its bounds then and now; surfaces added, removed and changed; and the scene's items added, removed and changed and its settings changed. Ids are listed up to 50 of each. changed is false when nothing did. With picture, it also draws the change: the changed layers shaded, added polygons green, changed orange, moved yellow, removed red (as they were), the rest grey. Changes nothing.")]
    public async Task<CallToolResult> Diff(
        [Description("The checkpoint to compare from (default: the newest).")] string? since = null,
        [Description("A later checkpoint to compare to (default: the object and scene now).")] string? until = null,
        [Description("Also draw the change as a PNG.")] bool picture = false,
        [Description("The picture's view: perspective (default), top, front or side.")] string view = "perspective",
        [Description("The picture's width in pixels, 16 to 2048 (default 640).")] int width = 640,
        [Description("The picture's height in pixels, 16 to 2048 (default 480).")] int height = 480,
        CancellationToken cancel = default)
    {
        var kind = Json.Choice<ViewKind>(view, "view", ViewKind.UV);
        if (picture) Json.Size(width, height, 2048);
        var (json, drawing) = await DoScene("compared with a checkpoint", (c, l) =>
        {
            if (_checkpoints.Count == 0 && since == null) throw new InvalidOperationException("There's no checkpoint to compare with: take one with checkpoint first.");
            var cp = since == null ? _checkpoints[^1] : Find(since, c.Document, l);
            var later = until == null ? Capture("", null, c.Document, l) : Find(until, c.Document, l);
            return (Compare(cp, later), picture ? DiffDocument(cp, later) : null);
        }, cancel, changes: false).ConfigureAwait(false);
        var text = new TextContentBlock { Text = json.ToJsonString() };
        if (drawing == null) return new CallToolResult { Content = [text] };
        var png = await Task.Run(() => Pictures.View(drawing, kind, width, height, ViewMode.Shaded), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [text, ImageContentBlock.FromBytes(png, "image/png")] };
    }

    [McpServerTool(Name = "render_checkpoint", ReadOnly = true, Idempotent = true)]
    [Description("Draws a checkpoint's object as render_view draws the object now (its visible layers, framed to fit), so you can see what restore_checkpoint would put back. Changes nothing.")]
    public async Task<CallToolResult> RenderCheckpoint(
        [Description("The checkpoint's name.")] string name,
        [Description("perspective (default), top, front or side.")] string view = "perspective",
        [Description("Width in pixels, 16 to 2048 (default 640).")] int width = 640,
        [Description("Height in pixels, 16 to 2048 (default 480).")] int height = 480,
        [Description("wireframe, shaded (default) or textured.")] string mode = "shaded",
        CancellationToken cancel = default)
    {
        var kind = Json.Choice<ViewKind>(view, "view", ViewKind.UV);
        var style = Json.Choice<ViewMode>(mode, "mode");
        Json.Size(width, height, 2048);
        var (doc, about) = await DoScene("looked at a checkpoint", (c, l) =>
        {
            var cp = Find(name, c.Document, l);
            return (DocumentOf(cp), $"Checkpoint \"{cp.Name}\" ({cp.Taken:yyyy-MM-dd HH:mm:ss} UTC), {kind} view, {width}×{height}.");
        }, cancel, changes: false).ConfigureAwait(false);
        var png = await Task.Run(() => Pictures.View(doc, kind, width, height, style), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "resolve_ids", ReadOnly = true, Idempotent = true)]
    [Description("Finds where points and polygons are now by their ids (from get_points or get_polygons with_ids, or a tool's new_point_ids and new_polygon_ids): each id's current index in the layer, or null if it's gone (deleted, merged, or rebuilt by a tool that moved it while renumbering the mesh). Ids are numbers that stay with a point or polygon while tools renumber the mesh around it: by index while the polygons keep their points, else by place (a point) or by the points joined (a polygon); geometry put back exactly (undo, restore_checkpoint) gets its id back. They're this session's, not saved with the object.")]
    public Task<string> ResolveIds(
        [Description("The layer, from 1. Default: the current layer.")] int? layer = null,
        [Description("Point ids to find.")] int[]? points = null,
        [Description("Polygon ids to find.")] int[]? polygons = null,
        CancellationToken cancel = default) =>
        Do("resolved ids", c =>
        {
            var (l, _) = Census.Layer(c.Document, layer);
            var known = _ids.Of(l);
            JsonObject Find(int[]? ids, int[] all)
            {
                var map = GeometryIds.IndexOf(all);
                var json = new JsonObject();
                foreach (int id in ids ?? [])
                    json[id.ToString(CultureInfo.InvariantCulture)] = map.TryGetValue(id, out int index) ? index : null;
                return json;
            }
            return new JsonObject
            {
                ["layer"] = l + 1,
                ["points"] = Find(points, known.PointIds),
                ["polygons"] = Find(polygons, known.PolygonIds),
            };
        }, cancel);

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>The object and the scene as they are now, as a checkpoint (not kept unless the caller keeps it).</summary>
    private Checkpoint Capture(string name, string? note, Document doc, LayoutSession l) =>
        new(name, DateTime.UtcNow, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), doc,
            doc.Snapshot(name, Enumerable.Range(0, doc.Layers.Count)), doc.Layers.Count, _ids.Save(doc),
            l.Scene, l.Scene.Snapshot(), SceneText(l.Scene), [.. l.Scene.Items.Select(i => i.Name)]);

    /// <summary>A document holding a checkpoint's object (its layers, surfaces and selection), to draw.</summary>
    private static Document DocumentOf(Checkpoint cp)
    {
        var doc = new Document();
        while (doc.Layers.Count < cp.Layers) doc.Layers.Add(new Layer());
        doc.Revert(cp.Object);
        return doc;
    }

    /// <summary>
    /// The change from <paramref name="a"/> to <paramref name="b"/> as a document to draw: the changed layers (all of
    /// them in the foreground) with every polygon on one of five surfaces by what happened to it (added green, changed
    /// orange, touching a moved point yellow, unchanged grey) and the removed polygons, taken from <paramref name="a"/>,
    /// in red.
    /// </summary>
    private static Document DiffDocument(Checkpoint a, Checkpoint b)
    {
        var doc = new Document();
        doc.Surfaces = [Look("Unchanged", 190, 190, 190), Look("Added", 60, 200, 80), Look("Changed", 240, 150, 40), Look("Moved", 240, 220, 60), Look("Removed", 220, 50, 50)];
        var shown = new List<int>();
        int count = Math.Max(a.Layers, b.Layers);
        while (doc.Layers.Count < count) doc.Layers.Add(new Layer());
        for (int i = 0; i < count; i++)
        {
            var then = i < a.Ids.Layers.Count ? a.Ids.Layers[i] : new GeometryIds.LayerIds();
            var now = i < b.Ids.Layers.Count ? b.Ids.Layers[i] : new GeometryIds.LayerIds();
            var nowLayer = b.Object.Layers.TryGetValue(i, out var nl) ? nl : new Layer();
            var thenLayer = a.Object.Layers.TryGetValue(i, out var tl) ? tl : new Layer();
            if (now.PointIds.Length + then.PointIds.Length == 0) continue;
            var thenPoints = GeometryIds.IndexOf(then.PointIds);
            var thenPolygons = GeometryIds.IndexOf(then.PolygonIds);
            var nowPolygons = GeometryIds.IndexOf(now.PolygonIds);
            var moved = new HashSet<int>();
            for (int j = 0; j < now.PointIds.Length; j++)
                if (thenPoints.TryGetValue(now.PointIds[j], out int k) && (now.Points[j] - then.Points[k]).Length > 1e-9) moved.Add(j);

            var mesh = new Mesh();
            foreach (var p in now.Points) mesh.AddPoint(p);
            bool any = false;
            for (int j = 0; j < now.Polygons.Length; j++)
            {
                int look = 0;
                if (!thenPolygons.TryGetValue(now.PolygonIds[j], out int k)) look = 1;
                else if (!then.PolygonPointIds(k).AsSpan().SequenceEqual(now.PolygonPointIds(j))
                         || SurfaceName(a.Object.Surfaces, then.Surfaces[k]) != SurfaceName(b.Object.Surfaces, now.Surfaces[j])) look = 2;
                else if (now.Polygons[j].Any(moved.Contains)) look = 3;
                any |= look != 0;
                mesh.AddPolygon(now.Polygons[j], look);
            }
            int offset = mesh.Points.Count;
            foreach (var p in then.Points) mesh.AddPoint(p);
            for (int k = 0; k < then.Polygons.Length; k++)
                if (!nowPolygons.ContainsKey(then.PolygonIds[k]))
                {
                    mesh.AddPolygon(then.Polygons[k].Select(x => x + offset), 4);
                    any = true;
                }
            any |= moved.Count > 0 || then.PointIds.Length != now.PointIds.Length;
            if (!any && thenLayer.Name == nowLayer.Name && thenLayer.State == nowLayer.State) continue;
            mesh.RemoveUnusedPoints();
            doc.Layers[i].Mesh = mesh;
            shown.Add(i);
        }
        doc.SetLayers(shown.Count > 0 ? shown : [0]);
        return doc;
    }

    private static Surface Look(string name, byte r, byte g, byte b) => new(name) { Color = new Rgb(r, g, b), DoubleSided = true };

    private static string SurfaceName(List<Surface> list, int s) => s >= 0 && s < list.Count ? list[s].Name : $"#{s}";

    /// <summary>The indices to select: those given, or the current layer's points or polygons with the ids given.</summary>
    private int[] Pick(Document doc, int[]? indices, int[]? ids, bool points)
    {
        string kind = points ? "point" : "polygon";
        if (indices != null && ids != null) throw new ArgumentException($"Give the {kind}s' indices or their ids, not both.");
        if (ids == null) return indices ?? throw new ArgumentException($"Give the {kind}s' indices (or their ids).");
        var known = _ids.Of(doc.CurrentLayer);
        var map = GeometryIds.IndexOf(points ? known.PointIds : known.PolygonIds);
        var missing = ids.Where(id => !map.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"No {kind} in layer {doc.CurrentLayer + 1} has the id{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing.Take(20))}: an edit since removed or rebuilt {(missing.Count == 1 ? "it" : "them")}. get_{kind}s with_ids lists the ones there now.");
        return [.. ids.Select(id => map[id])];
    }

    private Checkpoint Find(string name, Document doc, LayoutSession l)
    {
        var cp = _checkpoints.LastOrDefault(x => x.Name == name.Trim()) ?? LoadKept(name, doc, l);
        if (cp != null) return cp;
        throw new ArgumentException(_checkpoints.Count == 0
            ? $"There's no checkpoint called \"{name}\": this session has none (take one with checkpoint)."
            : $"There's no checkpoint called \"{name}\". This session has: {string.Join(", ", _checkpoints.Select(x => $"\"{x.Name}\""))}. (The oldest go once there are {MaxCheckpoints}.)");
    }

    private static JsonObject Describe(Checkpoint cp)
    {
        var json = new JsonObject
        {
            ["name"] = cp.Name,
            ["taken"] = cp.Taken.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
        };
        if (cp.Note != null) json["note"] = cp.Note;
        json["points"] = cp.Object.Layers.Values.Sum(x => x.Mesh.Points.Count);
        json["polygons"] = cp.Object.Layers.Values.Sum(x => x.Mesh.Polygons.Count);
        json["scene_items"] = cp.Items.Length;
        return json;
    }

    /// <summary>What changed from checkpoint <paramref name="cp"/> to <paramref name="later"/> (another checkpoint, or now).</summary>
    private static JsonObject Compare(Checkpoint cp, Checkpoint later)
    {
        bool changed = false;
        var layers = new JsonObject();
        int count = Math.Max(cp.Layers, later.Layers);
        for (int i = 0; i < count; i++)
        {
            var then = i < cp.Ids.Layers.Count ? cp.Ids.Layers[i] : new GeometryIds.LayerIds();
            var now = i < later.Ids.Layers.Count ? later.Ids.Layers[i] : new GeometryIds.LayerIds();
            var thenLayer = cp.Object.Layers.TryGetValue(i, out var t) ? t : new Layer();
            var nowLayer = later.Object.Layers.TryGetValue(i, out var n) ? n : new Layer();
            if (LayerDiff(then, now, thenLayer, nowLayer, cp.Object.Surfaces, later.Object.Surfaces) is { } diff)
            {
                layers[$"layer {i + 1}"] = diff;
                changed = true;
            }
        }

        var surfaces = new JsonObject();
        var thenSurfaces = cp.Object.Surfaces.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
        var nowSurfaces = later.Object.Surfaces.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
        var added = nowSurfaces.Keys.Except(thenSurfaces.Keys).ToList();
        var removed = thenSurfaces.Keys.Except(nowSurfaces.Keys).ToList();
        var altered = nowSurfaces.Keys.Intersect(thenSurfaces.Keys).Where(n => nowSurfaces[n].Signature() != thenSurfaces[n].Signature()).ToList();
        if (added.Count > 0) surfaces["added"] = Names(added);
        if (removed.Count > 0) surfaces["removed"] = Names(removed);
        if (altered.Count > 0) surfaces["changed"] = Names(altered);
        changed |= surfaces.Count > 0;

        var scene = new JsonObject();
        if (!ReferenceEquals(cp.Scene, later.Scene))
        {
            scene["replaced"] = true;
            changed = true;
        }
        else
        {
            var itemsNow = later.Items.ToList();
            var itemsAdded = Leftover(itemsNow, cp.Items);
            var itemsRemoved = Leftover(cp.Items, itemsNow);
            if (itemsAdded.Count > 0) scene["items_added"] = Names(itemsAdded);
            if (itemsRemoved.Count > 0) scene["items_removed"] = Names(itemsRemoved);
            // What else changed: each item's settings and keys, and the scene's own settings, as a scene file has them.
            if (later.SceneJson is { } nowText && cp.SceneJson is { } thenText && nowText != thenText)
            {
                var (items, settings) = SceneChanges(thenText, nowText);
                if (items.Count > 0) scene["items_changed"] = Names(items);
                if (settings.Count > 0) scene["settings_changed"] = Names(settings);
                if (items.Count + settings.Count == 0) scene["changed"] = true;
            }
            changed |= scene.Count > 0;
        }

        var json = new JsonObject { ["since"] = cp.Name, ["changed"] = changed };
        if (later.Name.Length > 0) json["until"] = later.Name;
        if (!ReferenceEquals(cp.Document, later.Document)) json["object_note"] = "Another object was made or opened since the checkpoint, so every point and polygon counts as new.";
        if (layers.Count > 0) json["layers"] = layers;
        if (surfaces.Count > 0) json["surfaces"] = surfaces;
        if (scene.Count > 0) json["scene"] = scene;
        return json;
    }

    private static JsonObject? LayerDiff(GeometryIds.LayerIds then, GeometryIds.LayerIds now, Layer thenLayer, Layer nowLayer,
        List<Surface> thenSurfaces, List<Surface> nowSurfaces)
    {
        var json = new JsonObject();
        var thenPoints = GeometryIds.IndexOf(then.PointIds);
        var nowPoints = GeometryIds.IndexOf(now.PointIds);
        var pointsAdded = now.PointIds.Where(id => !thenPoints.ContainsKey(id)).ToList();
        var pointsRemoved = then.PointIds.Where(id => !nowPoints.ContainsKey(id)).ToList();
        var moved = new List<int>();
        double farthest = 0;
        foreach (var (id, j) in nowPoints)
            if (thenPoints.TryGetValue(id, out int i))
            {
                double d = (now.Points[j] - then.Points[i]).Length;
                if (d > 1e-9) { moved.Add(id); farthest = Math.Max(farthest, d); }
            }
        if (pointsAdded.Count + pointsRemoved.Count + moved.Count > 0)
        {
            var points = new JsonObject { ["then"] = then.PointIds.Length, ["now"] = now.PointIds.Length };
            if (pointsAdded.Count > 0) points["added"] = Ids(pointsAdded);
            if (pointsRemoved.Count > 0) points["removed"] = Ids(pointsRemoved);
            if (moved.Count > 0)
            {
                moved.Sort();
                var m = Ids(moved);
                m["farthest_m"] = Census.Round(farthest);
                points["moved"] = m;
            }
            json["points"] = points;
        }

        var thenPolygons = GeometryIds.IndexOf(then.PolygonIds);
        var nowPolygons = GeometryIds.IndexOf(now.PolygonIds);
        var polygonsAdded = now.PolygonIds.Where(id => !thenPolygons.ContainsKey(id)).ToList();
        var polygonsRemoved = then.PolygonIds.Where(id => !nowPolygons.ContainsKey(id)).ToList();
        var altered = new List<int>();
        string SurfaceName(List<Surface> list, int s) => s >= 0 && s < list.Count ? list[s].Name : $"#{s}";
        foreach (var (id, j) in nowPolygons)
            if (thenPolygons.TryGetValue(id, out int i)
                && (!then.PolygonPointIds(i).AsSpan().SequenceEqual(now.PolygonPointIds(j))
                    || SurfaceName(thenSurfaces, then.Surfaces[i]) != SurfaceName(nowSurfaces, now.Surfaces[j])))
                altered.Add(id);
        if (polygonsAdded.Count + polygonsRemoved.Count + altered.Count > 0)
        {
            var polygons = new JsonObject { ["then"] = then.PolygonIds.Length, ["now"] = now.PolygonIds.Length };
            if (polygonsAdded.Count > 0) polygons["added"] = Ids(polygonsAdded);
            if (polygonsRemoved.Count > 0) polygons["removed"] = Ids(polygonsRemoved);
            if (altered.Count > 0) { altered.Sort(); polygons["changed"] = Ids(altered); }
            json["polygons"] = polygons;
        }

        if (thenLayer.Name != nowLayer.Name) json["name"] = new JsonObject { ["then"] = thenLayer.Name, ["now"] = nowLayer.Name };
        if (thenLayer.State != nowLayer.State) json["state"] = new JsonObject { ["then"] = State(thenLayer.State), ["now"] = State(nowLayer.State) };
        if (json.Count == 0) return null;
        if (json.ContainsKey("points"))
            json["bounds"] = new JsonObject { ["then"] = Bounds(then.Points), ["now"] = Bounds(now.Points) };
        return json;
    }

    /// <summary>The scene as a scene file has it (objects by reference), to tell whether it changed; null if it can't be written.</summary>
    private static string? SceneText(AVAModeler.Scene.Scene scene)
    {
        try { return SceneJson.Write(scene); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException or JsonException) { return null; }
    }

    /// <summary>The items (by name) whose settings or keys differ, and the scene settings that do.</summary>
    private static (List<string> Items, List<string> Settings) SceneChanges(string thenText, string nowText)
    {
        var then = JsonNode.Parse(thenText) as JsonObject;
        var now = JsonNode.Parse(nowText) as JsonObject;
        var items = new List<string>();
        var settings = new List<string>();
        if (then == null || now == null) return (items, settings);
        string ItemName(JsonNode? item) => (string?)item?["Name"] ?? (string?)item?["name"] ?? "";
        var thenItems = (then["Items"] ?? then["items"]) as JsonArray ?? [];
        var nowItems = (now["Items"] ?? now["items"]) as JsonArray ?? [];
        var thenByName = thenItems.GroupBy(ItemName).ToDictionary(g => g.Key, g => g.First()!.ToJsonString());
        foreach (var item in nowItems)
            if (thenByName.TryGetValue(ItemName(item), out var was) && was != item!.ToJsonString())
                items.Add(ItemName(item));
        void Differences(JsonObject a, JsonObject b, string prefix)
        {
            foreach (var key in b.Select(kv => kv.Key).Union(a.Select(kv => kv.Key)))
            {
                if (prefix.Length == 0 && key is "items" or "Items") continue;
                var (x, y) = (a[key], b[key]);
                if ((x?.ToJsonString() ?? "null") == (y?.ToJsonString() ?? "null")) continue;
                // The scene's settings are one object in the file: name the settings within it.
                if (prefix.Length == 0 && key is "settings" && x is JsonObject xs && y is JsonObject ys) Differences(xs, ys, "settings.");
                else settings.Add(key);
            }
        }
        Differences(then, now, "");
        return (items, settings);
    }

    private static string State(LayerState s) => s switch
    {
        LayerState.Foreground => "foreground", LayerState.Background => "background", _ => "off",
    };

    private static JsonNode? Bounds(Vec3[] points)
    {
        if (points.Length == 0) return null;
        var b = Box3.Of(points);
        return new JsonObject
        {
            ["min"] = new JsonArray(Census.Round(b.Min.X), Census.Round(b.Min.Y), Census.Round(b.Min.Z)),
            ["max"] = new JsonArray(Census.Round(b.Max.X), Census.Round(b.Max.Y), Census.Round(b.Max.Z)),
        };
    }

    private static JsonObject Ids(List<int> ids)
    {
        var json = new JsonObject { ["count"] = ids.Count, ["ids"] = new JsonArray([.. ids.Take(50).Select(i => (JsonNode)i)]) };
        if (ids.Count > 50) json["more"] = ids.Count - 50;
        return json;
    }

    private static JsonArray Names(IEnumerable<string> names) => new([.. names.Select(n => (JsonNode)n)]);

    /// <summary>The names in <paramref name="a"/> that <paramref name="b"/> hasn't (as many times as they're over).</summary>
    private static List<string> Leftover(IEnumerable<string> a, IEnumerable<string> b)
    {
        var counts = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var left = new List<string>();
        foreach (var n in a)
            if (counts.TryGetValue(n, out int k) && k > 0) counts[n] = k - 1;
            else left.Add(n);
        return left;
    }
}
