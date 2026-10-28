using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AVAModeler.UI.Tests;

/// <summary>
/// An AI agent driving the open window through the live MCP door (as <c>AVAModeler --mcp --attach</c> does): its
/// calls run on the UI thread, the views redraw, Ctrl+Z undoes its steps, and it waits while the person is busy.
/// </summary>
internal static class Agent
{
    /// <summary>A new pipe name, so tests never meet a real AVA Modeler.</summary>
    public static string Pipe() => "avamodeler-uitest-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Waits for a task while keeping the window's dispatcher running (the agent's calls need it).</summary>
    public static T Wait<T>(Task<T> task)
    {
        for (int i = 0; i < 20000 && !task.IsCompleted; i++)
        {
            Ui.Pump();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "The call didn't finish.");
        return task.GetAwaiter().GetResult();
    }

    public static McpClient Connect(string pipe, string root) =>
        Wait(LiveBridge.ConnectAsync(pipe, root, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

    public static CallToolResult Call(McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return Wait(client.CallToolAsync(tool, dict, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    public static JsonObject Json(McpClient client, string tool, object? args = null)
    {
        var r = Call(client, tool, args);
        Assert.True(r.IsError != true, $"{tool} failed: {Text(r)}");
        return (JsonObject)JsonNode.Parse(Text(r))!;
    }

    public static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>The table the README shows: lathed legs arrayed to the corners, a top, wood colours.</summary>
    public static void BuildATable(McpClient client)
    {
        Json(client, "add_points", new { points = new[] { new[] { 0.03, 0, 0 }, [0.045, 0.05, 0], [0.03, 0.2, 0], [0.025, 0.6, 0], [0.04, 0.68, 0], [0.04, 0.7, 0] } });
        Json(client, "add_polygons", new { polygons = new[] { new[] { 0, 1 }, [1, 2], [2, 3], [3, 4], [4, 5] }, surface = "Wood" });
        Json(client, "run_tool", new { name = "Lathe", @params = new { Axis = "Y", Center = new[] { 0.0, 0, 0 }, Sides = 16 } });
        Json(client, "run_tool", new { name = "Array", @params = new { CountX = 2, CountZ = 2, Spacing = new[] { 1.5, 1, 0.8 } } });
        Json(client, "run_tool", new { name = "Move", @params = new { Offset = new[] { -0.75, 0, -0.4 } } });
        Json(client, "run_tool", new { name = "Box", @params = new { Center = new[] { 0, 0.725, 0 }, Size = new[] { 1.8, 0.05, 1.0 }, Surface = "Top" } });
        Json(client, "set_surface", new { name = "Wood", color = new[] { 125, 78, 42 }, smoothing = true, assign = false });
        Json(client, "set_surface", new { name = "Top", color = new[] { 176, 118, 66 }, specular = 0.3, assign = false });
    }
}

public class LiveAgentTests
{
    [AvaloniaFact]
    public void An_agent_builds_in_the_open_document_and_the_views_follow()
    {
        var w = Ui.Open();
        var root = Path.GetTempPath();
        string pipe = Agent.Pipe();
        // Not the 20 seconds a person gets to finish what they're doing: the test makes them busy below.
        w.Agents.BusyWait = TimeSpan.FromMilliseconds(300);
        Assert.Null(w.Agents.Start(pipe));
        try
        {
            var front = Ui.View(w, ViewKind.Front);
            Ui.Pump();
            w.CaptureRenderedFrame();
            var before = front.Software!.Pixels.ToArray();
            var client = Agent.Connect(pipe, root);
            var r = Agent.Json(client, "run_tool", new { name = "Box", @params = new { Size = new[] { 1.5, 1, 1 } } });
            Assert.Equal(8, (int)r["points"]!["after"]!);
            // The same document the person sees, redrawn in the views, with the agent's step in the status bar.
            Assert.Equal(8, w.Session.Document.Current.Mesh.Points.Count);
            Ui.Pump();
            w.CaptureRenderedFrame();
            Assert.NotEqual(before, front.Software!.Pixels);
            Assert.Contains("AI agent: ran Box", w.Session.Status);
            Assert.StartsWith("8 points · 6 polygons", w.Session.Counts());

            // The person's Ctrl+Z undoes it.
            Ui.Key(w, Key.Z, Avalonia.Input.RawInputModifiers.Control);
            Assert.Empty(w.Session.Document.Current.Mesh.Points);
            Assert.Equal(0, (int)Agent.Json(client, "get_document")["undo"]!["depth"]!);

            // While the person has a tool armed the agent waits, then is told why; nothing changes underneath them.
            w.Modeler.PickTool("Move");
            var busy = Agent.Call(client, "run_tool", new { name = "Ball" });
            Assert.True(busy.IsError);
            Assert.Contains("the person is using the Move tool", Agent.Text(busy));
            Assert.Empty(w.Session.Document.Current.Mesh.Points);
            w.Session.CancelTool();
            Agent.Json(client, "run_tool", new { name = "Ball" });
            Assert.NotEmpty(w.Session.Document.Current.Mesh.Points);

            // Opening a file through the agent replaces the open document, as File › Open would.
            Agent.Json(client, "new", new { discard_changes = true });
            Assert.Empty(w.Session.Document.Current.Mesh.Points);
            Agent.Json(client, "run_tool", new { name = "Box" });
            Assert.Equal(8, w.Session.Document.Current.Mesh.Points.Count);
            Wait(client.DisposeAsync().AsTask());
        }
        finally
        {
            Wait(w.Agents.StopAsync());
        }
    }

    [AvaloniaFact]
    public void The_preference_opens_and_closes_the_door()
    {
        var w = Ui.Open();
        Assert.False(w.Agents.Running);
        w.AgentPipe = Agent.Pipe();
        w.Session.Preferences.AllowAgents = true;
        w.SyncAgents();
        Assert.True(w.Agents.Running);
        Assert.Contains("AI agents may connect", w.Session.Status);
        // A second window can't take the same pipe.
        var other = Ui.Open();
        other.AgentPipe = w.AgentPipe;
        other.Session.Preferences.AllowAgents = true;
        other.SyncAgents();
        Assert.False(other.Agents.Running);
        Assert.Contains("Another AVA Modeler", other.Session.Status);
        w.Session.Preferences.AllowAgents = false;
        w.SyncAgents();
        Assert.False(w.Agents.Running);
        Assert.Contains("can no longer connect", w.Session.Status);
        // The menu item turns it on and off too.
        w.Modeler.Execute("AI Agents");
        Assert.True(w.Session.Preferences.AllowAgents);
        Assert.True(w.Agents.Running);
        w.Modeler.Execute("AI Agents");
        Assert.False(w.Session.Preferences.AllowAgents);
        Assert.False(w.Agents.Running);
    }

    private static void Wait(Task task) => Agent.Wait(task.ContinueWith(_ => 0, TaskScheduler.Default));
}
