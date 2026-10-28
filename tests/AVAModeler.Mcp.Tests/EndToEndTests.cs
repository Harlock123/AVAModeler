using System.Diagnostics;
using AVAModeler.Core;
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
        Assert.Equal(30, tools.Count);
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
