using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Mcp.Tests;

/// <summary>Batches, checkpoints and diff, and points' and polygons' ids, through a client as an agent uses them.</summary>
public sealed class SessionToolTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly HeadlessHost _host;
    private InProcess? _server;

    public SessionToolTests() => _host = new HeadlessHost(_folder.Path);

    private Document Doc => _host.Commands.Document;

    private async Task<ModelContextProtocol.Client.McpClient> Client() => (_server ??= await InProcess.Start(_host)).Client;

    public async ValueTask DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
        _folder.Dispose();
    }

    private static int[] Ints(JsonNode? node) => [.. node!.AsArray().Select(n => (int)n!)];

    private static JsonObject Row(JsonObject polygons, int index) =>
        polygons["polygons"]!.AsArray().Select(p => (JsonObject)p!).Single(p => (int)p["index"]! == index);

    // ───────────────────────────── ids ─────────────────────────────

    [Fact]
    public async Task A_polygon_keeps_its_id_when_others_before_it_are_removed_and_selecting_by_id_finds_it()
    {
        var client = await Client();
        var box = await client.Json("run_tool", new { name = "Box" });
        // A call that makes geometry says what it made.
        Assert.Equal(8, box["ids"]!["layer 1"]!["new_point_ids"]!.AsArray().Count);
        Assert.Equal(6, box["ids"]!["layer 1"]!["new_polygon_ids"]!.AsArray().Count);

        var before = await client.Json("get_polygons", new { with_ids = true });
        int lastId = (int)Row(before, 5)["id"]!;
        Assert.Equal(4, Row(before, 5)["point_ids"]!.AsArray().Count);

        await client.Json("select_polygons", new { indices = new[] { 0 } });
        var removed = await client.Json("run_tool", new { name = "Remove" });
        Assert.Equal(1, (int)removed["ids"]!["layer 1"]!["polygons_removed"]!);
        Assert.Equal(5, Doc.Current.Mesh.Polygons.Count);

        // The polygon that was index 5 is index 4 now; its id still finds it.
        var where = await client.Json("resolve_ids", new { polygons = new[] { lastId, 999 } });
        Assert.Equal(4, (int)where["polygons"]![lastId.ToString()]!);
        Assert.Null(where["polygons"]!["999"]);
        var selected = await client.Json("select_polygons", new { ids = new[] { lastId } });
        Assert.Equal([4], Doc.Selection.PolygonsIn(0).Order());
        Assert.NotNull(selected);

        // An id that's gone is refused, saying what to do.
        int firstId = (int)Row(before, 0)["id"]!;
        Assert.Contains("get_polygons with_ids", await client.Fails("select_polygons", new { ids = new[] { firstId } }));
        Assert.Contains("not both", await client.Fails("select_polygons", new { indices = new[] { 0 }, ids = new[] { lastId } }));
    }

    [Fact]
    public async Task Moving_keeps_every_id_and_undo_gives_a_removed_polygon_its_old_id_back()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var points = await client.Json("get_points", new { with_ids = true });
        var ids = points["points"]!.AsArray().Select(r => (int)r![4]!).ToArray();
        var polygons = await client.Json("get_polygons", new { with_ids = true });
        var polygonIds = polygons["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray();

        var moved = await client.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { 1, 0, 0 } } });
        Assert.Equal(8, (int)moved["ids"]!["layer 1"]!["points_moved"]!);
        var after = await client.Json("get_points", new { with_ids = true });
        Assert.Equal(ids, after["points"]!.AsArray().Select(r => (int)r![4]!).ToArray());

        // Flip turns a polygon round: the same points, so the same id.
        await client.Json("select_polygons", new { indices = new[] { 2 } });
        await client.Json("run_tool", new { name = "Flip" });
        var flipped = await client.Json("get_polygons", new { with_ids = true });
        Assert.Equal(polygonIds, flipped["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray());

        await client.Json("select_polygons", new { indices = new[] { 1 } });
        await client.Json("run_tool", new { name = "Remove" });
        await client.Json("undo");
        var back = await client.Json("get_polygons", new { with_ids = true });
        Assert.Equal(polygonIds.Order(), back["polygons"]!.AsArray().Select(p => (int)p!["id"]!).Order());
    }

    [Fact]
    public async Task New_geometry_gets_new_ids_and_ids_are_never_used_again()
    {
        var client = await Client();
        var first = Ints((await client.Json("run_tool", new { name = "Box" }))["ids"]!["layer 1"]!["new_point_ids"]);
        await client.Json("select_all");
        await client.Json("run_tool", new { name = "Remove" });
        var second = Ints((await client.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 5, 0, 0 } } }))["ids"]!["layer 1"]!["new_point_ids"]);
        Assert.Empty(first.Intersect(second));
    }

    /// <summary>A host with only the object (as AVALightWave's build steps have): modeling calls never ask it for a scene.</summary>
    private sealed class ObjectOnlyHost(string root) : IModelerHost
    {
        public AVAModeler.Tools.Commands Commands { get; } = new(new Document());
        public string Root { get; } = root;
        public Task<T> RunAsync<T>(string action, Func<AVAModeler.Tools.Commands, T> work, CancellationToken cancel = default) => Task.FromResult(work(Commands));
        public Task<T> RunSceneAsync<T>(string action, Func<AVAModeler.Tools.Commands, AVAModeler.Layout.LayoutSession, T> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("There's no scene here.");
    }

    [Fact]
    public async Task Modeling_calls_work_on_a_host_with_no_scene()
    {
        var host = new ObjectOnlyHost(_folder.Path);
        var tools = new ModelerTools(host);
        var box = await Calls.Parse(tools.RunTool("Box", cancel: Calls.Cancel));
        Assert.Equal(6, host.Commands.Document.Current.Mesh.Polygons.Count);
        Assert.NotNull(box["ids"]);
        await Calls.Parse(tools.SelectWhere(normal: [0, 1, 0], cancel: Calls.Cancel));
        Assert.Single(host.Commands.Document.Selection.PolygonsIn(0));
    }

    // ───────────────────────────── batch ─────────────────────────────

    [Fact]
    public async Task A_batch_is_one_undo_step_and_returns_each_steps_answer()
    {
        var client = await Client();
        var result = await client.Json("batch", new
        {
            steps = new object[]
            {
                new { tool = "run_tool", args = new { name = "Box" } },
                new { tool = "select_polygons", args = new { indices = new[] { 0 } } },
                new { tool = "set_surface", args = new { name = "Red", color = new[] { 255, 0, 0 } } },
            },
            label = "Red-topped box",
        });
        Assert.Equal(3, (int)result["steps"]!);
        Assert.Equal("run_tool", (string)result["results"]![0]!["tool"]!);
        Assert.Equal(6, Doc.Current.Mesh.Polygons.Count);
        Assert.Equal(["Red-topped box"], Doc.Undo.UndoLabels);

        await client.Json("undo");
        Assert.True(Doc.Current.Mesh.IsEmpty);
    }

    [Fact]
    public async Task A_failing_step_takes_the_whole_batch_back_and_says_which_step()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var history = Doc.Undo.UndoLabels.ToList();
        var points = Doc.Current.Mesh.Points.ToList();

        var message = await client.Fails("batch", new
        {
            steps = new object[]
            {
                new { tool = "run_tool", args = new { name = "Move", @params = new { Offset = new[] { 2, 0, 0 } } } },
                new { tool = "run_tool", args = new { name = "Box", @params = new { Center = new[] { 0, 5, 0 } } } },
                new { tool = "select_polygons", args = new { indices = new[] { 500 } } },
            },
        });
        Assert.Contains("Step 3 of 3 (select_polygons) failed", message);
        Assert.Contains("Nothing was changed", message);
        Assert.Equal(points, Doc.Current.Mesh.Points);
        Assert.Equal(history, Doc.Undo.UndoLabels);
    }

    [Fact]
    public async Task A_batch_checks_every_step_before_running_any()
    {
        var client = await Client();
        Assert.Contains("Step 2 (run_tool)", await client.Fails("batch", new
        {
            steps = new object[] { new { tool = "run_tool", args = new { name = "Box" } }, new { tool = "run_tool", args = new { nmae = "Ball" } } },
        }));
        Assert.Contains("no tool called", await client.Fails("batch", new { steps = new object[] { new { tool = "make_it_nice" } } }));
        Assert.Contains("can't run inside a batch", await client.Fails("batch", new { steps = new object[] { new { tool = "save", args = new { path = "x.lwo" } } } }));
        Assert.Contains("can't run inside a batch", await client.Fails("batch", new { steps = new object[] { new { tool = "undo" } } }));
        Assert.True(Doc.Current.Mesh.IsEmpty);
        Assert.Empty(Doc.Undo.UndoLabels);
    }

    [Fact]
    public async Task A_batch_of_scene_calls_is_one_step_on_the_scenes_history_and_fails_whole()
    {
        var client = await Client();
        int items = _host.Layout.Scene.Items.Count;
        int steps = _host.Layout.History.Count;
        await client.Json("batch", new
        {
            steps = new object[]
            {
                new { tool = "add_light", args = new { name = "Key", type = "distant" } },
                new { tool = "set_transform", args = new { item = "Key", position = new[] { 2, 3, -2 } } },
            },
        });
        Assert.Equal(items + 1, _host.Layout.Scene.Items.Count);
        Assert.Equal(steps + 1, _host.Layout.History.Count);

        await client.Fails("batch", new
        {
            steps = new object[]
            {
                new { tool = "add_light", args = new { name = "Fill", type = "point" } },
                new { tool = "set_transform", args = new { item = "No such thing", position = new[] { 0, 0, 0 } } },
            },
        });
        Assert.Equal(items + 1, _host.Layout.Scene.Items.Count);
        Assert.Equal(steps + 1, _host.Layout.History.Count);
    }

    // ───────────────────────────── checkpoints ─────────────────────────────

    [Fact]
    public async Task Restoring_a_checkpoint_puts_the_object_and_scene_back_exactly_as_one_undo_step_each()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        var taken = await client.Json("checkpoint", new { name = "box", note = "before the roof" });
        Assert.Equal("box", (string)taken["name"]!);
        var points = Doc.Current.Mesh.Points.ToList();
        var polygonIds = (await client.Json("get_polygons", new { with_ids = true }))["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray();
        int items = _host.Layout.Scene.Items.Count;

        await client.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { 0, 1, 0 } } });
        await client.Json("select_polygons", new { indices = new[] { 0 } });
        await client.Json("run_tool", new { name = "Remove" });
        await client.Json("add_light", new { name = "Sun", type = "distant" });

        var restored = await client.Json("restore_checkpoint", new { name = "box" });
        Assert.True((bool)restored["scene_restored"]!);
        Assert.Equal(points, Doc.Current.Mesh.Points);
        Assert.Equal(items, _host.Layout.Scene.Items.Count);
        Assert.Equal("Restore checkpoint box", Doc.Undo.UndoLabel);
        Assert.Equal(polygonIds, (await client.Json("get_polygons", new { with_ids = true }))["polygons"]!.AsArray().Select(p => (int)p!["id"]!).ToArray());
        Assert.False((bool)(await client.Json("diff", new { since = "box" }))["changed"]!);

        // Undo brings back what was there just before the restore; the checkpoint is still there to go back to.
        await client.Json("undo");
        Assert.Equal(5, Doc.Current.Mesh.Polygons.Count);
        await client.Json("undo_scene");
        Assert.Equal(items + 1, _host.Layout.Scene.Items.Count);
        await client.Json("restore_checkpoint", new { name = "box" });
        Assert.Equal(points, Doc.Current.Mesh.Points);

        var list = await client.Json("list_checkpoints");
        Assert.Equal("before the roof", (string)list["checkpoints"]![0]!["note"]!);
        Assert.Contains("\"box\"", await client.Fails("restore_checkpoint", new { name = "nope" }));
    }

    [Fact]
    public async Task Diff_says_what_changed_by_id()
    {
        var client = await Client();
        await client.Json("run_tool", new { name = "Box" });
        await client.Json("checkpoint");
        var polygons = await client.Json("get_polygons", new { with_ids = true });
        int goneId = (int)Row(polygons, 0)["id"]!;

        await client.Json("select_polygons", new { indices = new[] { 0 } });
        await client.Json("run_tool", new { name = "Remove" });
        await client.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 3, 0, 0 } } });
        await client.Json("select_polygons", new { indices = new[] { 1 } });
        await client.Json("set_surface", new { name = "Blue", color = new[] { 0, 0, 255 } });
        await client.Json("add_null", new { name = "Target" });

        var diff = await client.Json("diff");
        Assert.True((bool)diff["changed"]!);
        var layer = diff["layers"]!["layer 1"]!;
        Assert.Equal(8, (int)layer["points"]!["added"]!["count"]!);
        Assert.Equal([goneId], Ints(layer["polygons"]!["removed"]!["ids"]));
        Assert.Equal(6, (int)layer["polygons"]!["added"]!["count"]!);
        Assert.Equal(1, (int)layer["polygons"]!["changed"]!["count"]!);
        Assert.Contains("Blue", diff["surfaces"]!["added"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["Target"], diff["scene"]!["items_added"]!.AsArray().Select(n => (string)n!));

        // An item moved and a scene setting changed are named too.
        await client.Json("checkpoint", new { name = "staged" });
        var camera = _host.Layout.Scene.Items.First(i => i is AVAModeler.Scene.CameraItem).Name;
        await client.Json("set_transform", new { item = camera, position = new[] { 0, 2, -6 } });
        await client.Json("set_scene", new { ambient_intensity = 0.4 });
        var staged = await client.Json("diff", new { since = "staged" });
        Assert.Equal([camera], staged["scene"]!["items_changed"]!.AsArray().Select(n => (string)n!));
        Assert.NotEmpty(staged["scene"]!["settings_changed"]!.AsArray());
        Assert.False(staged.ContainsKey("layers"));
    }

    [Fact]
    public async Task Diff_needs_a_checkpoint_and_the_oldest_go_after_thirty()
    {
        var client = await Client();
        Assert.Contains("take one with checkpoint", await client.Fails("diff"));
        for (int i = 0; i < ModelerTools.MaxCheckpoints + 1; i++) await client.Json("checkpoint");
        var list = await client.Json("list_checkpoints");
        Assert.Equal(ModelerTools.MaxCheckpoints, list["checkpoints"]!.AsArray().Count);
        Assert.Equal("checkpoint 2", (string)list["checkpoints"]![0]!["name"]!);
    }

    // ───────────────────────────── live ─────────────────────────────

    [Fact]
    public async Task In_the_live_app_a_batch_runs_on_the_ui_thread_in_one_go()
    {
        using var ui = new TestUiThread();
        var app = new TestModeler(ui);
        await using var server = await InProcess.Start(new LiveHost(ui, app, _folder.Path) { BusyWait = TimeSpan.FromSeconds(5), BusyPoll = TimeSpan.FromMilliseconds(20) });
        await server.Client.Json("batch", new
        {
            steps = new object[]
            {
                new { tool = "run_tool", args = new { name = "Box" } },
                new { tool = "select_polygons", args = new { indices = new[] { 0, 1 } } },
                new { tool = "run_tool", args = new { name = "Remove" } },
            },
        });
        var doc = await ui.InvokeAsync(() => app.Commands.Document);
        Assert.Equal(4, await ui.InvokeAsync(() => doc.Current.Mesh.Polygons.Count));
        Assert.Equal(1, await ui.InvokeAsync(() => doc.Undo.Count));
        Assert.Equal(0, app.WrongThread);

        await server.Client.Json("checkpoint", new { name = "reply 1" });
        await server.Client.Json("run_tool", new { name = "Ball" });
        await server.Client.Json("restore_checkpoint", new { name = "reply 1" });
        Assert.Equal(4, await ui.InvokeAsync(() => doc.Current.Mesh.Polygons.Count));
    }
}
