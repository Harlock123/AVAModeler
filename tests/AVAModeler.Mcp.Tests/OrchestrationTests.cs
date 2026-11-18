using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// The tools for orchestrating agents through a client: select_where and saved selections, expect, dry runs, diff
/// between checkpoints and its picture, recording and steps files, locks, sessions, jobs and progress, structured
/// answers and error kinds, resources and subscriptions, confirmation, ids kept in files, checkpoints kept on disk.
/// </summary>
public sealed class OrchestrationTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly HeadlessHost _host;
    private readonly List<InProcess> _servers = [];

    public OrchestrationTests() => _host = new HeadlessHost(_folder.Path);

    private Document Doc => _host.Commands.Document;

    private async Task<McpClient> Client(IModelerHost? host = null, McpClientOptions? options = null)
    {
        var s = await InProcess.Start(ModelerServer.Options(host ?? _host), options);
        _servers.Add(s);
        return s.Client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        _folder.Dispose();
    }

    private static string Kind(CallToolResult r) => r.StructuredContent is { } sc && sc.TryGetProperty("kind", out var k) ? k.GetString()! : "";

    // ───────────────────────────── select_where, saved selections ─────────────────────────────

    [Fact]
    public async Task Select_where_picks_faces_by_facing_place_and_size_and_combines_with_the_selection()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var top = await client.Json("select_where", new { normal = new[] { 0, 1, 0 }, within_degrees = 10 });
        Assert.Equal(1, (int)top["selected"]!);
        var poly = Doc.Current.Mesh.Polygons[Doc.Selection.PolygonsIn(0).Single()];
        Assert.True(Doc.Current.Mesh.Normal(poly).Y > 0.99);

        var sides = await client.Json("select_where", new { axis = "y", above = -0.01, below = 0.01 });
        Assert.Equal(4, (int)sides["selected"]!);
        await client.Json("select_where", new { normal = new[] { 1, 0, 0 }, mode = "remove" });
        Assert.Equal(3, Doc.Selection.PolygonsIn(0).Count);
        var points = await client.Json("select_where", new { what = "points", axis = "y", above = 0 });
        Assert.Equal(4, (int)points["selected"]!);
        Assert.Equal(SelectionMode.Points, Doc.Selection.Mode);
        Assert.Contains("for polygons", await client.Fails("select_where", new { what = "points", normal = new[] { 0, 1, 0 } }));
        Assert.Contains("no surface", await client.Fails("select_where", new { surface = "Nope" }));
    }

    [Fact]
    public async Task A_saved_selection_follows_its_polygons_when_the_mesh_is_renumbered()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("select_where", new { normal = new[] { 0, 1, 0 } });
        var top = Doc.Current.Mesh.Centroid(Doc.Current.Mesh.Polygons[Doc.Selection.PolygonsIn(0).Single()]);
        await client.Json("save_selection", new { name = "lid" });

        await client.Json("select_polygons", new { indices = new[] { 0 } });
        await client.Json("run_tool", new { name = "Remove" });
        await client.Json("select_none");
        var back = await client.Json("select_saved", new { name = "lid" });
        Assert.Equal(1, (int)back["selected"]!);
        Assert.Equal(top, Doc.Current.Mesh.Centroid(Doc.Current.Mesh.Polygons[Doc.Selection.PolygonsIn(0).Single()]));
        var list = await client.Json("list_selections");
        Assert.Equal(1, (int)list["selections"]![0]!["polygons_there"]!);
    }

    // ───────────────────────────── expect, dry runs ─────────────────────────────

    [Fact]
    public async Task Expect_checks_the_object_and_with_fail_it_takes_a_batch_back()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var ok = await client.Json("expect", new { watertight = true, printable = true, max_polygons = 6, size_max = new[] { 1, 1, 1 }, symmetric = "x", volume_min = 0.99, volume_max = 1.01 });
        Assert.True((bool)ok["passed"]!, ok.ToJsonString());

        var bad = await client.Json("expect", new { max_polygons = 4, bounds_max = new[] { 0.2, 1, 1 } });
        Assert.False((bool)bad["passed"]!);
        Assert.Equal(2, (int)bad["failed"]!);

        // As a batch's last step with fail, the batch only stands if it passes.
        var result = await client.Call("batch", new
        {
            steps = new object[]
            {
                new { tool = "select_polygons", args = new { indices = new[] { 0 } } },
                new { tool = "run_tool", args = new { name = "Remove" } },
                new { tool = "expect", args = new { watertight = true, fail = true } },
            },
        });
        Assert.True(result.IsError);
        Assert.Contains("expectations failed", Calls.Text(result));
        Assert.Equal(6, Doc.Current.Mesh.Polygons.Count);
    }

    [Fact]
    public async Task A_dry_run_says_what_a_batch_would_change_and_changes_nothing()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var history = Doc.Undo.UndoLabels.ToList();
        var dry = await client.Json("batch", new
        {
            steps = new object[]
            {
                new { tool = "select_polygons", args = new { indices = new[] { 0, 1 } } },
                new { tool = "run_tool", args = new { name = "Remove" } },
            },
            dry_run = true,
        });
        Assert.True((bool)dry["dry_run"]!);
        Assert.Equal(2, (int)dry["would_change"]!["layers"]!["layer 1"]!["polygons"]!["removed"]!["count"]!);
        Assert.Equal(6, Doc.Current.Mesh.Polygons.Count);
        Assert.Equal(history, Doc.Undo.UndoLabels);
    }

    // ───────────────────────────── diff between checkpoints, pictures ─────────────────────────────

    [Fact]
    public async Task Diff_compares_two_checkpoints_and_draws_the_change_and_a_checkpoint_can_be_drawn()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("checkpoint", new { name = "a" });
        await client.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 2, 0, 0 } } });
        await client.Json("checkpoint", new { name = "b" });
        await client.Json("select_all");
        await client.Json("run_tool", new { name = "Remove" });

        var ab = await client.Json("diff", new { since = "a", until = "b" });
        Assert.Equal("b", (string)ab["until"]!);
        Assert.Equal(6, (int)ab["layers"]!["layer 1"]!["polygons"]!["added"]!["count"]!);
        Assert.False(ab["layers"]!["layer 1"]!["polygons"]!.AsObject().ContainsKey("removed"));

        var drawn = await client.Call("diff", new { since = "a", picture = true, width = 160, height = 120 });
        var (w, h, colours) = Pngs.Inspect(Pngs.Of(drawn));
        Assert.Equal((160, 120), (w, h));
        Assert.True(colours > 10);
        Assert.Contains("\"changed\":true", Calls.Text(drawn));

        var picture = await client.Call("render_checkpoint", new { name = "b", width = 96, height = 72 });
        var (pw, ph, _) = Pngs.Inspect(Pngs.Of(picture));
        Assert.Equal((96, 72), (pw, ph));
    }

    /// <summary>The README's picture of a diff: set AVAMODELER_SHOTS to save it.</summary>
    [Fact]
    public async Task Screenshot_a_diff_picture()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 1.2, 0.5, 0.8 }, ["Center"] = new[] { 0, 0.25, 0 }, ["Segments X"] = 3, ["Segments Z"] = 2 } });
        await client.Json("run_tool", new { name = "Box", @params = new { Size = new[] { 0.3, 0.3, 0.3 }, Center = new[] { -0.9, 0.15, 0 } } });
        await client.Json("checkpoint", new { name = "before" });
        // Added: a ball on top; moved: the small box; changed: two faces get another surface; removed: a face.
        await client.Json("run_tool", new { name = "Ball", @params = new { Center = new[] { 0.2, 0.75, 0 }, Radius = new[] { 0.25, 0.25, 0.25 } } });
        await client.Json("select_where", new { box_min = new[] { -1.1, -0.1, -0.2 }, box_max = new[] { -0.7, 0.4, 0.2 }, what = "points" });
        await client.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 0, 0.6 } } });
        await client.Json("select_where", new { normal = new[] { 1, 0, 0 }, within_degrees = 5, axis = "x", above = 0.5 });
        await client.Json("set_surface", new { name = "Painted", color = new[] { 40, 90, 200 } });
        await client.Json("select_where", new { normal = new[] { 0, 0, -1 }, within_degrees = 5, axis = "x", below = -0.2, above = -0.65 });
        await client.Json("run_tool", new { name = "Remove" });
        var drawn = await client.Call("diff", new { since = "before", picture = true, width = 640, height = 400 });
        Assert.NotEqual(true, drawn.IsError);
        var png = Pngs.Of(drawn);
        Assert.True(Pngs.Inspect(png).Colours > 20);
        if (Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "AVAModeler-diff.png"), png, Calls.Cancel);
        }
    }

    // ───────────────────────────── recording, steps files ─────────────────────────────

    [Fact]
    public async Task The_recording_becomes_a_steps_file_that_runs_again_with_other_values()
    {
        var client = await Client();
        await client.Json("checkpoint", new { name = "start" });
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("get_document");   // read-only: not recorded
        await client.Json("set_surface", new { name = "Red", color = new[] { 255, 0, 0 } });
        var recording = await client.Json("get_recording", new { since = "start" });
        Assert.Equal(2, (int)recording["count"]!);
        Assert.Equal("run_tool", (string)recording["steps"]![0]!["tool"]!);

        var saved = await client.Json("save_steps", new
        {
            path = "crate",
            description = "A red crate",
            parameters = new { size = new { @default = 1, description = "edge in metres" } },
            steps = new object[]
            {
                new { tool = "run_tool", args = new { name = "Box", @params = new { Size = new[] { "${size}", "${size}", "${size}" }, Center = new object[] { 0, "${size / 2}", 0 } } } },
                new { tool = "set_surface", args = new { name = "Red", color = new[] { 255, 0, 0 } } },
            },
        });
        Assert.Equal("crate.avasteps.json", (string)saved["file"]!);
        Assert.True(File.Exists(_folder.File("crate.avasteps.json")));
        Assert.Contains("isn't one of the parameters", await client.Fails("save_steps", new { path = "bad", steps = new object[] { new { tool = "run_tool", args = new { name = "${nope}" } } } }));

        await client.Json("new", new { discard_changes = true });
        var run = await client.Json("run_steps", new { path = "crate", values = new { size = 2 } });
        Assert.Equal(2, (int)run["values"]!["size"]!);
        var b = Doc.Bounds();
        Assert.Equal(2, b.Max.Y - b.Min.Y, 6);
        Assert.Equal(0, b.Min.Y, 6);
        Assert.Single(Doc.Undo.UndoLabels);
        Assert.Contains("crate", Doc.Undo.UndoLabel);

        var dry = await client.Json("run_steps", new { path = "crate", dry_run = true });
        Assert.True((bool)dry["dry_run"]!);
        Assert.Single(Doc.Undo.UndoLabels);
    }

    // ───────────────────────────── locks, sessions ─────────────────────────────

    [Fact]
    public async Task Locked_geometry_and_items_refuse_any_agents_change_and_only_the_owner_unlocks()
    {
        var first = await Client();
        var second = await Client();
        await first.Json("run_tool", new { name = "Box" });
        await first.Json("select_where", new { normal = new[] { 0, 1, 0 } });
        await first.Json("lock", new { name = "lid", note = "finished" });
        var points = Doc.Current.Mesh.Points.ToList();

        // The other agent can't move it, nor can the one that locked it.
        await second.Json("select_none");
        var moved = await second.Call("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 0.3, 0 } } });
        Assert.True(moved.IsError);
        Assert.Equal(ErrorKinds.Locked, Kind(moved));
        Assert.Contains("\"lid\"", Calls.Text(moved));
        Assert.Equal(points, Doc.Current.Mesh.Points);
        // Work elsewhere is fine: a box beside it.
        await second.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 3, 0, 0 } } });

        Assert.Equal(ErrorKinds.Locked, Kind(await second.Call("unlock", new { name = "lid" })));
        var locks = await second.Json("list_locks");
        Assert.NotEqual("you", (string)locks["locks"]![0]!["owner"]!);
        await first.Json("unlock", new { name = "lid" });
        await second.Json("select_none");
        await second.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 1, 0 } } });

        // Scene items too, and the person's locks are theirs.
        await first.Json("add_light", new { name = "Key", type = "distant" });
        await first.Json("lock", new { name = "key light", items = new[] { "Key" } });
        Assert.Equal(ErrorKinds.Locked, Kind(await second.Call("set_transform", new { item = "Key", position = new[] { 1, 2, 3 } })));
        Doc.AgentLocks.Add(AgentLock.Of(Doc, 0, [0], [], "the person's", AgentLock.Person));
        Assert.Contains("only they", Calls.Text(await first.Call("unlock", new { name = "the person's" })));
    }

    [Fact]
    public async Task List_agents_shows_every_session_and_a_sessions_locks_go_when_it_ends()
    {
        var first = await Client();
        var second = await Client();
        await first.Json("run_tool", new { name = "Box" });
        await second.Json("select_all");
        await second.Json("lock", new { name = "all" });
        var agents = await first.Json("list_agents");
        var list = agents["agents"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Single(list, a => (bool)a!["you"]!);
        Assert.Contains(list, a => a!["locks"]!.AsArray().Count == 1);

        await ModelerServer.EndSessionAsync(_servers[1].Options);
        Assert.Empty(Doc.AgentLocks);
        Assert.Single((await first.Json("list_agents"))["agents"]!.AsArray());
    }

    // ───────────────────────────── jobs, progress ─────────────────────────────

    [Fact]
    public async Task A_job_renders_in_the_background_with_progress_and_its_answer_waits_for_it()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Ball" });
        await client.Json("add_object");
        var started = await client.Json("start_job", new { tool = "render_scene", args = new { width = 64, height = 48, samples = 1 } });
        int id = (int)started["job"]!;
        var result = await client.Call("job_result", new { job = id, wait = 60 });
        Assert.NotEqual(true, result.IsError);
        var (rw, rh, _) = Pngs.Inspect(Pngs.Of(result));
        Assert.Equal((64, 48), (rw, rh));
        var status = await client.Json("job_status", new { job = id });
        Assert.Equal("done", (string)status["state"]!);
        Assert.Equal(1, (double)status["progress"]!);
        Assert.Contains("no job 99", await client.Fails("job_result", new { job = 99 }));
        Assert.Contains("this server has no tool", (await client.Fails("start_job", new { tool = "nope" })).ToLowerInvariant());

        // A call with a progress token hears how it's going.
        var heard = new ConcurrentBag<ProgressNotificationValue>();
        var args = new Dictionary<string, object?> { ["width"] = 64, ["height"] = 48, ["samples"] = 1 };
        await client.CallToolAsync("render_scene", args, new SyncProgress(heard), cancellationToken: Calls.Cancel);
        for (int i = 0; i < 50 && heard.IsEmpty; i++) await Task.Delay(20, Calls.Cancel);
        Assert.NotEmpty(heard);
    }

    private sealed class SyncProgress(ConcurrentBag<ProgressNotificationValue> bag) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => bag.Add(value);
    }

    // ───────────────────────────── structured answers, error kinds ─────────────────────────────

    [Fact]
    public async Task Answers_come_as_structured_content_and_errors_carry_their_kind()
    {
        var client = await Client();
        var ok = await client.Call("run_tool", new { name = "Box" });
        Assert.NotNull(ok.StructuredContent);
        Assert.Equal(JsonValueKind.Object, ok.StructuredContent!.Value.ValueKind);

        Assert.Equal(ErrorKinds.BadParameter, Kind(await client.Call("run_tool", new { nmae = "Box" })));
        Assert.Equal(ErrorKinds.NotFound, Kind(await client.Call("select_polygons", new { ids = new[] { 999 } })));
        Assert.Equal(ErrorKinds.NotFound, Kind(await client.Call("restore_checkpoint", new { name = "never" })));
        var bad = await client.Call("expect", new { max_polygons = 1, fail = true });
        Assert.Equal(ErrorKinds.ExpectationFailed, Kind(bad));
        Assert.Equal("expectation_failed", (string)bad.Meta!["kind"]!);
    }

    // ───────────────────────────── resources ─────────────────────────────

    [Fact]
    public async Task Resources_are_listed_read_and_announced_when_someone_else_changes_them()
    {
        // resources/subscribe is the protocol before 2026-07-28's (whose subscriptions/listen the SDK serves itself).
        var watcher = await Client(options: new McpClientOptions { ProtocolVersion = "2025-11-25" });
        var worker = await Client();
        var resources = await watcher.ListResourcesAsync(cancellationToken: Calls.Cancel);
        Assert.Equal(ModelerTools.ResourceList.Count, resources.Count);
        var read = await watcher.ReadResourceAsync("avamodeler://object", cancellationToken: Calls.Cancel);
        Assert.Contains("layers", Assert.IsType<TextResourceContents>(read.Contents[0]).Text);

        var heard = new ConcurrentQueue<string>();
        await watcher.SubscribeToResourceAsync("avamodeler://object", (n, _) => { heard.Enqueue(n.Uri); return ValueTask.CompletedTask; }, cancellationToken: Calls.Cancel);
        // Its own change isn't announced to it.
        await watcher.Json("run_tool", new { name = "Box" });
        await Task.Delay(600, Calls.Cancel);
        Assert.Empty(heard);
        // Another session's is.
        await worker.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 3, 0, 0 } } });
        for (int i = 0; i < 100 && heard.IsEmpty; i++) await Task.Delay(20, Calls.Cancel);
        Assert.Equal("avamodeler://object", Assert.Single(heard.Distinct()));
    }

    // ───────────────────────────── confirmation ─────────────────────────────

    [Fact]
    public async Task In_the_live_app_removing_most_of_a_layer_asks_the_person_first()
    {
        using var ui = new TestUiThread();
        var app = new TestModeler(ui);
        var answers = new Queue<string>(["decline", "accept"]);
        var asked = new List<string>();
        var options = new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    asked.Add(request?.Message ?? "");
                    return ValueTask.FromResult(new ElicitResult { Action = answers.Dequeue() });
                },
            },
        };
        var client = await Client(new LiveHost(ui, app, _folder.Path) { BusyWait = TimeSpan.FromSeconds(5), BusyPoll = TimeSpan.FromMilliseconds(20) }, options);
        await client.Json("run_tool", new { name = "Ball" });
        var doc = await ui.InvokeAsync(() => app.Commands.Document);
        int polygons = await ui.InvokeAsync(() => doc.Current.Mesh.Polygons.Count);

        var declined = await client.Call("run_tool", new { name = "Remove" });
        Assert.True(declined.IsError);
        Assert.Equal(ErrorKinds.Declined, Kind(declined));
        Assert.Contains($"of the {polygons:N0} polygons in layer 1", asked[0]);
        Assert.Equal(polygons, await ui.InvokeAsync(() => doc.Current.Mesh.Polygons.Count));
        Assert.Equal(1, await ui.InvokeAsync(() => doc.Undo.Count));

        await client.Json("run_tool", new { name = "Remove" });
        Assert.Equal(0, await ui.InvokeAsync(() => doc.Current.Mesh.Polygons.Count));
        Assert.Equal(2, asked.Count);
        Assert.Equal(2, await ui.InvokeAsync(() => doc.Undo.Count));

        // A small change doesn't ask.
        await client.Json("run_tool", new { name = "Box" });
        Assert.Equal(2, asked.Count);
    }

    // ───────────────────────────── ids in files, checkpoints on disk ─────────────────────────────

    [Fact]
    public async Task Ids_kept_in_the_file_are_the_same_in_another_session()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("select_polygons", new { indices = new[] { 0 } });
        await client.Json("run_tool", new { name = "Remove" });
        await client.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 3, 0, 0 } } });
        var before = (await client.Json("get_polygons", new { with_ids = true }))["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray();
        await client.Json("save", new { path = "kept.lwo", keep_ids = true });

        var other = await Client(new HeadlessHost(_folder.Path));
        await other.Json("open", new { path = "kept.lwo" });
        var after = (await other.Json("get_polygons", new { with_ids = true }))["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray();
        Assert.Equal(before, after);
        // New geometry there carries on the numbering.
        var made = (await other.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 6, 0, 0 } } }))["ids"]!["layer 1"]!["new_polygon_ids"]!.AsArray().Select(n => (int)n!);
        Assert.True(made.Min() > before.Max());
    }

    [Fact]
    public async Task A_checkpoint_kept_on_disk_is_restored_by_a_later_session()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("add_light", new { name = "Lamp", type = "point" });
        await client.Json("checkpoint", new { name = "boxed", persist = true });
        Assert.True(File.Exists(Path.Combine(_folder.Path, ModelerTools.KeptCheckpoints, "boxed", "object.lwo")));
        var points = Doc.Current.Mesh.Points.ToList();

        var later = new HeadlessHost(_folder.Path);
        var other = await Client(later);
        var list = await other.Json("list_checkpoints");
        Assert.Equal("boxed", (string)list["kept_on_disk"]![0]!["name"]!);
        var restored = await other.Json("restore_checkpoint", new { name = "boxed" });
        Assert.True((bool)restored["scene_restored"]!);
        Assert.Equal(points, later.Commands.Document.Current.Mesh.Points);
        Assert.Contains(later.Layout.Scene.Items, i => i.Name == "Lamp");
    }
}
