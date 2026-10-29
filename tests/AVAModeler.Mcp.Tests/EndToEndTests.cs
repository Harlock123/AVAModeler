using System.Diagnostics;
using AVAModeler.Core;
using System.Text.Json.Nodes;
using AVAModeler.Formats;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// Whole agent sessions through a real MCP client: in process over streams, and against the built
/// <c>AVAModeler --mcp</c> as a separate process over stdio (a couple of seconds).
/// </summary>
public class EndToEndTests
{
    /// <summary>
    /// What an agent would do to build a table: look at the tools, lathe a leg from a profile, array it to four
    /// corners, put a top on, colour it, look at it, save it.
    /// </summary>
    private static async Task BuildATable(McpClient client, string folder)
    {
        var tools = await client.ListToolsAsync(cancellationToken: Cancel);
        Assert.Equal(52, tools.Count);
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description"));
        var listed = await client.Json("list_tools");
        Assert.Contains(listed["tools"]!.AsArray(), t => (string?)t!["name"] == "Lathe");
        var lathe = await client.Json("describe_tool", new { name = "Lathe" });
        Assert.Contains(lathe["parameters"]!.AsArray(), p => (string?)p!["name"] == "Sides");

        // A turned leg: a profile of lines in the XY plane, lathed round Y.
        await client.Json("add_points", new { points = new[] { new[] { 0.03, 0, 0 }, [0.045, 0.05, 0], [0.03, 0.2, 0], [0.025, 0.6, 0], [0.04, 0.7, 0] } });
        await client.Json("add_polygons", new { polygons = new[] { new[] { 0, 1 }, [1, 2], [2, 3], [3, 4] }, surface = "Wood" });
        var leg = await client.Json("run_tool", new { name = "Lathe", @params = new { Axis = "Y", Center = new[] { 0.0, 0, 0 }, Sides = 12 } });
        Assert.Equal(48, (int)leg["polygons"]!["after"]!);
        // Four legs at the corners, centred.
        var legs = await client.Json("run_tool", new { name = "Array", @params = new { CountX = 2, CountZ = 2, Spacing = new[] { 1.5, 1, 0.8 } } });
        Assert.Equal(4 * 48, (int)legs["polygons"]!["after"]!);
        await client.Json("run_tool", new { name = "Move", @params = new { Offset = new[] { -0.75, 0, -0.4 } } });
        // The top.
        var top = await client.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 0, 0.725, 0 }, Size = new[] { 1.8, 0.05, 1.0 }, Surface = "Top" } });
        Assert.Equal(4 * 48 + 6, (int)top["polygons"]!["after"]!);
        await client.Json("set_surface", new { name = "Wood", color = new[] { 120, 72, 40 }, smoothing = true, assign = false });
        await client.Json("set_surface", new { name = "Top", color = new[] { 170, 110, 60 }, specular = 0.3, assign = false });

        var doc = await client.Json("get_document");
        var size = doc["visible_bounds"]!["size"]!.AsArray().Select(v => (double)v!).ToArray();
        Assert.Equal(1.8, size[0], 6);
        Assert.Equal(0.75, size[1], 6);
        Assert.Equal(1.0, size[2], 6);
        Assert.Equal(8, (int)doc["undo"]!["depth"]!);

        // Look at it.
        var view = await client.Call("render_view", new { view = "perspective", width = 320, height = 240 });
        var (w, h, colours) = Pngs.Inspect(Pngs.Of(view));
        Assert.Equal((320, 240), (w, h));
        Assert.True(colours > 50, "the view looks blank");
        var front = await client.Call("render_view", new { view = "front", width = 200, height = 150, mode = "wireframe" });
        Assert.True(Pngs.Inspect(Pngs.Of(front)).Colours > 4);

        // Keep it, and read it back.
        var saved = await client.Json("save", new { path = "table.lwo" });
        string file = Path.Combine(folder, "table.lwo");
        Assert.Equal(file, (string?)saved["saved"]);
        var back = ObjectFiles.Load(file);
        Assert.Equal(4 * 48 + 6, back.Current.Mesh.Polygons.Count);
        Assert.Equal(new Rgb(120, 72, 40), back.Surfaces.Single(s => s.Name == "Wood").Color);
        Assert.True(back.Surfaces.Single(s => s.Name == "Wood").Smoothing);

        // A mistake is an error the agent can read, and nothing changes.
        Assert.Contains("no tool called \"Lath\"", await client.Fails("run_tool", new { name = "Lath" }));
        Assert.Contains("limit must be a whole number", await client.Fails("get_points", new { limit = "all" }));
        Assert.Equal(8, (int)(await client.Json("get_document"))["undo"]!["depth"]!);
    }

    /// <summary>
    /// What an agent would do next: put the table in a scene, light it with a warm spot, swing the camera round it over
    /// 60 frames (keyed, and aimed at the table at each key), render frame 30 and look at it, then save the scene and
    /// open it again.
    /// </summary>
    private static async Task StageTheTable(McpClient client, string folder)
    {
        var obj = await client.Json("add_object", new { name = "Table" });
        Assert.Equal("Table", (string?)obj["name"]);
        Assert.Equal("the Modeler's object (live)", (string?)obj["source"]);
        var spot = await client.Json("add_light", new { type = "spot", name = "Warm", position = new[] { 2.0, 3, -2 }, look_at = new[] { 0, 0.4, 0 }, color = new[] { 1, 0.8, 0.55 }, intensity = 1.3, cone_angle = 35 });
        Assert.Equal("spot", (string?)spot["type"]);
        await client.Json("set_item", new { item = "Light", intensity = 0.4 });
        await client.Json("set_scene", new { background_color = new[] { 0.05, 0.05, 0.1 } });
        double[][] path = [[0, 1.5, -4], [4, 2, 0], [0, 1.5, 4]];
        int[] frames = [1, 30, 60];
        for (int i = 0; i < 3; i++)
        {
            await client.Json("set_transform", new { item = "Camera", frame = frames[i], position = path[i], key = true });
            await client.Json("look_at", new { item = "Camera", target = "Table", frame = frames[i], key = true });
        }
        var keys = await client.Json("get_keys", new { item = "Camera", channel = "heading" });
        Assert.Equal([0.0, -90, -180], keys["channels"]![0]!["keys"]!.AsArray().Select(k => Math.Round((double)k!["value"]!, 6)));
        // Between keys the camera is on its way round, still roughly facing the table.
        var at15 = await client.Json("get_scene", new { frame = 15 });
        var cam = at15["items"]!.AsArray().Single(i => (string?)i!["name"] == "Camera")!;
        var p = cam["position"]!.AsArray().Select(v => (double)v!).ToArray();
        Assert.True(p[0] > 1 && p[0] < 4 && p[2] > -4 && p[2] < 0, $"The camera at frame 15 is at [{string.Join(", ", p)}].");
        var look = cam["looks_toward"]!.AsArray().Select(v => (double)v!).ToArray();
        Assert.True(-p[0] * look[0] - p[2] * look[2] > 0, "The camera at frame 15 should still look toward the table.");

        // Check the placement cheaply, then ray-trace frame 30.
        var view = await client.Call("render_scene_view", new { view = "camera", frame = 30, width = 160 });
        Assert.Contains("object Table at (80, ", Text(view));
        var render = await client.Call("render_scene", new { frame = 30, width = 96, samples = 1 });
        var image = TextureImages.Decode(Pngs.Of(render))!;
        Assert.Equal((96, 72), (image.Width, image.Height));
        // Not blank, and the table shows: the middle isn't the backdrop the top corners are.
        (int, int, int) At(int x, int y) { int i = (y * image.Width + x) * 4; return (image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]); }
        var backdrop = At(0, 0);
        Assert.Equal(backdrop, At(95, 0));
        int different = 0;
        for (int y = 24; y < 48; y++)
            for (int x = 32; x < 64; x++)
                if (At(x, y) != backdrop) different++;
        Assert.True(different > 24 * 32 / 2, $"Only {different} of the middle's pixels show anything.");
        Assert.True(Pngs.Inspect(Pngs.Of(render)).Colours > 20);

        // Keep it, and open it again: the same items, keys and settings.
        var before = await client.Json("get_scene", new { frame = 30 });
        await client.Json("save_scene", new { path = "table.avascene" });
        Assert.True(File.Exists(Path.Combine(folder, "table.avascene")));
        await client.Json("new_scene", new { });
        Assert.Equal(2, (await client.Json("get_scene"))["items"]!.AsArray().Count);
        await client.Json("open_scene", new { path = "table.avascene" });
        var after = await client.Json("get_scene", new { frame = 30 });
        foreach (var o in new[] { before, after })
        {
            o.Remove("file");
            o.Remove("modified");
            o.Remove("undo_scene");
            // Saved, the Modeler's object is embedded in the file.
            foreach (var item in o["items"]!.AsArray()) item!.AsObject().Remove("source");
        }
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
    }

    [Fact]
    public async Task An_agent_session_stages_and_renders_a_scene_in_process()
    {
        using var folder = new TempFolder();
        await using var session = await InProcess.Start(new HeadlessHost(folder.Path));
        await BuildATable(session.Client, folder.Path);
        await StageTheTable(session.Client, folder.Path);
        Assert.Contains("undo_scene", session.Client.ServerInstructions ?? "");
        Assert.Contains("add_object", session.Client.ServerInstructions ?? "");
    }

    [Fact]
    public async Task An_agent_session_builds_a_table_in_process()
    {
        using var folder = new TempFolder();
        await using var session = await InProcess.Start(new HeadlessHost(folder.Path));
        await BuildATable(session.Client, folder.Path);
        // The server introduces itself and its conventions.
        Assert.Equal("AVAModeler", session.Client.ServerInfo?.Name);
        Assert.Contains("left-handed", session.Client.ServerInstructions ?? "");
        Assert.Contains("clockwise", session.Client.ServerInstructions ?? "");
    }

    [Fact]
    public async Task The_command_line_serves_headless_or_as_a_bridge()
    {
        using var folder = new TempFolder();
        using var up = new Wire();
        using var down = new Wire();
        var log = new StringWriter();
        var run = Task.Run(() => McpCommandLine.RunAsync(["--mcp", "--root", folder.Path, "--no-plugins"], _ => AVAModeler.Tools.ToolRegistry.Create(), up.Reader, down.Writer, log, Cancel));
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(up.Writer, down.Reader), null, null, Cancel))
        {
            await client.Json("run_tool", new { name = "Box" });
            await client.Json("save", new { path = "box.lwo" });
        }
        up.Writer.Dispose();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel));
        Assert.True(File.Exists(Path.Combine(folder.Path, "box.lwo")));
        Assert.Contains("headless", log.ToString());

        var help = new StringWriter();
        Assert.Equal(0, await McpCommandLine.RunAsync(["--mcp", "--help"], _ => AVAModeler.Tools.ToolRegistry.Create(), Stream.Null, Stream.Null, help, Cancel));
        Assert.Contains("--attach", help.ToString());
        Assert.Equal(2, await McpCommandLine.RunAsync(["--mcp", "--bogus"], _ => AVAModeler.Tools.ToolRegistry.Create(), Stream.Null, Stream.Null, new StringWriter(), Cancel));
        Assert.Equal(2, await McpCommandLine.RunAsync(["--mcp", "--root", Path.Combine(folder.Path, "nowhere")], _ => AVAModeler.Tools.ToolRegistry.Create(), Stream.Null, Stream.Null, new StringWriter(), Cancel));
        Assert.True(McpCommandLine.Wanted(["--MCP"]));
        Assert.False(McpCommandLine.Wanted(["table.lwo"]));
    }

    /// <summary>The app as built alongside these tests (same configuration), to start as a process.</summary>
    private static string BuiltApp()
    {
        // The tests run from tests/AVAModeler.Mcp.Tests/bin/<configuration>/<framework>/.
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string framework = here.Name, configuration = here.Parent!.Name;
        string dll = Path.Combine(here.FullName, "..", "..", "..", "..", "..", "src", "AVAModeler", "bin", configuration, framework, "AVAModeler.dll");
        Assert.True(File.Exists(dll), $"The app isn't built at {dll}.");
        return Path.GetFullPath(dll);
    }

    /// <summary>
    /// The real thing: the built app started as <c>AVAModeler --mcp</c> in a process of its own, talked to over its
    /// stdin and stdout by the SDK's stdio client. Nothing but MCP may reach stdout.
    /// </summary>
    [Fact]
    public async Task The_built_app_serves_mcp_over_stdio()
    {
        string dll = BuiltApp();
        using var folder = new TempFolder();
        var stderr = new List<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "AVAModeler",
            Command = "dotnet",
            Arguments = [dll, "--mcp", "--no-plugins"],
            WorkingDirectory = folder.Path,
            StandardErrorLines = line => { lock (stderr) stderr.Add(line); },
        });
        var clock = Stopwatch.StartNew();
        await using (var client = await McpClient.CreateAsync(transport, null, null, Cancel))
        {
            await BuildATable(client, folder.Path);
            // And a ray-traced still.
            var view = await client.Call("render", new { width = 64, height = 48, samples = 1 });
            Assert.True(Pngs.Inspect(Pngs.Of(view)).Colours > 4);
            // And a scene, a frame of it rendered.
            await client.Json("add_object", new { name = "Table" });
            await client.Json("look_at", new { item = "Camera", target = "Table" });
            var frame = await client.Call("render_scene", new { width = 64, samples = 1 });
            Assert.True(Pngs.Inspect(Pngs.Of(frame)).Colours > 4);
        }
        lock (stderr) Assert.Contains(stderr, l => l.Contains("headless"));
        TestContext.Current.SendDiagnosticMessage($"The process session took {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    /// <summary>The built app as <c>AVAModeler --mcp --attach</c>, bridging to a live listener on a pipe of the test's own.</summary>
    [Fact]
    public async Task The_built_app_bridges_to_a_live_app_over_stdio()
    {
        string dll = BuiltApp();
        using var folder = new TempFolder();
        using var ui = new TestUiThread();
        var modeler = new TestModeler(ui);
        string pipe = "avamodeler-test-" + Guid.NewGuid().ToString("N")[..12];
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "AVAModeler",
            Command = "dotnet",
            Arguments = [dll, "--mcp", "--attach"],
            WorkingDirectory = folder.Path,
            EnvironmentVariables = new Dictionary<string, string?> { ["AVAMODELER_MCP_PIPE"] = pipe },
        });
        await using var client = await McpClient.CreateAsync(transport, null, null, Cancel);
        Assert.Contains("No AVA Modeler is taking AI agents", await client.Fails("get_document"));
        await using var listener = new LiveListener(pipe, root => new LiveHost(ui, modeler, root));
        listener.Start();
        await client.Json("run_tool", new { name = "Box" });
        Assert.Equal(8, await ui.InvokeAsync(() => modeler.Document.Current.Mesh.Points.Count));
        await client.Json("save", new { path = "live.lwo" });
        Assert.True(File.Exists(Path.Combine(folder.Path, "live.lwo")));
    }
}
