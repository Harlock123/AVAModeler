using System.ComponentModel;
using System.Reflection;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// Server profiles: AVA Modeler's own serves every tool and recipe as before; another app (a standalone Layout) serves a
/// chosen set with tools and recipes of its own, headless, in process and through its own live pipe.
/// </summary>
public class ProfileTests
{
    /// <summary>A tool a standalone Layout might add of its own.</summary>
    public sealed class ExtraTools
    {
        [McpServerTool(Name = "hub_status", ReadOnly = true)]
        [Description("Says hello.")]
        public static string HubStatus() => "{\"linked\":false}";
    }

    [McpServerPromptType]
    public static class ExtraRecipes
    {
        [McpServerPrompt(Name = "scene_only", Title = "Scene only")]
        [Description("A recipe of the app's own.")]
        public static string SceneOnly()
        {
            var recipe = new Recipes.Recipe("Light whatever is there.");
            Recipes.LightRig(recipe, "Thing", Recipes.Number("3", 4, "distance", 0.1, 100));
            return recipe.Text("Done.");
        }
    }

    private static ServerProfile LayoutOnly => new()
    {
        Name = "TestLayout",
        Title = "Test Layout",
        Instructions = "Scene tools only.",
        Version = "0.1.0",
        IncludeTool = ToolGroups.Scene.Contains,
        IncludePrompt = name => name == "three_point_lighting",
        Describe = (name, text) => name == "add_object" ? "Loads an object file into the scene." : text,
        ExtraTools = _ => [McpServerTool.Create(typeof(ExtraTools).GetMethod(nameof(ExtraTools.HubStatus))!)],
        ExtraPrompts = () => [McpServerPrompt.Create(typeof(ExtraRecipes).GetMethod(nameof(ExtraRecipes.SceneOnly))!)],
        HasModelerObject = false,
        Program = "TestLayout",
        NotRunning = "No Test Layout is open.",
        LiveNote = "Live in the test window.",
    };

    [Fact]
    public void Every_tool_is_a_scene_tool_or_a_modeling_tool_and_not_both()
    {
        var names = ModelerServer.Definitions().Select(t => t.Name).ToHashSet();
        Assert.Empty(ToolGroups.Scene.Intersect(ToolGroups.Modeling));
        Assert.Equal(names.OrderBy(n => n), ToolGroups.Scene.Union(ToolGroups.Modeling).OrderBy(n => n));
    }

    [Fact]
    public void Modeler_s_own_profile_serves_everything_as_before()
    {
        Assert.Equal(ModelerServer.Definitions().Select(t => t.Name), ModelerServer.Definitions(ServerProfile.Modeler).Select(t => t.Name));
        Assert.Equal("AVAModeler", ModelerServer.Info.Name);
        Assert.Equal("AVA Modeler", ModelerServer.Info.Title);
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Count(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null);
        Assert.Equal(recipes, ModelerServer.Prompts().Count());
        Assert.Same(ModelerServer.Instructions, ServerProfile.Modeler.Instructions);
    }

    [Fact]
    public async Task A_layout_only_profile_serves_the_scene_tools_its_own_and_its_recipes()
    {
        using var folder = new TempFolder();
        var host = new HeadlessHost(folder.Path);
        await using var session = await InProcess.Start(ModelerServer.Options(host, LayoutOnly));
        var tools = (await session.Client.ListToolsAsync(cancellationToken: Cancel)).Select(t => t.ProtocolTool).ToList();
        Assert.Equal(ToolGroups.Scene.Count + 1, tools.Count);
        Assert.Contains(tools, t => t.Name == "hub_status");
        Assert.DoesNotContain(tools, t => t.Name is "run_tool" or "save" or "add_morph" or "copy_layer");
        Assert.Equal("Loads an object file into the scene.", tools.Single(t => t.Name == "add_object").Description);
        Assert.Equal("TestLayout", session.Client.ServerInfo!.Name);
        Assert.Equal("0.1.0", session.Client.ServerInfo!.Version);
        Assert.Equal("Scene tools only.", session.Client.ServerInstructions);
        var prompts = (await session.Client.ListPromptsAsync(cancellationToken: Cancel)).Select(p => p.Name).ToList();
        Assert.Equal(["three_point_lighting", "scene_only"], prompts);
        var text = (await session.Client.GetPromptAsync("scene_only", cancellationToken: Cancel)).Messages.Single().Content;
        Assert.Contains("→ add_light", ((ModelContextProtocol.Protocol.TextContentBlock)text).Text);
        Assert.Equal("{\"linked\":false}", Text(await session.Client.Call("hub_status")));
        // A modeling tool isn't there to call.
        await Assert.ThrowsAsync<McpProtocolException>(() => session.Client.Call("run_tool", new { name = "Box" }));
        // The engine's own profile is unchanged by another's.
        Assert.Contains(ModelerServer.Definitions(), t => t.Name == "add_object" && t.Description!.Contains("Modeler's object", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_modeler_object_add_object_needs_a_file()
    {
        using var folder = new TempFolder();
        var doc = new Document();
        new BoxTool().Run(new ToolContext(doc));
        ObjectFiles.Save(doc, folder.File("box.lwo"));
        var host = new HeadlessHost(folder.Path);
        var tools = new ModelerTools(host) { Profile = LayoutOnly };
        Assert.Contains("give file", await Error(tools.AddObject(cancel: Cancel)));
        Assert.Contains("give file", await Error(tools.AddObject(layer: 1, cancel: Cancel)));
        Assert.Equal(2, host.Layout.Scene.Items.Count);
        var r = await Parse(tools.AddObject(file: "box.lwo", cancel: Cancel));
        Assert.Equal("box", (string?)r["name"]);
    }

    [Fact]
    public async Task A_profile_s_live_window_and_bridge_serve_its_tools_on_its_own_pipe()
    {
        using var ui = new TestUiThread();
        var app = new TestModeler(ui);
        using var folder = new TempFolder();
        string pipe = "avamodeler-profile-" + Guid.NewGuid().ToString("N")[..12];
        await using var listener = new LiveListener(pipe, root => new LiveHost(ui, app, root), LayoutOnly);
        listener.Start();
        var bridge = new LiveBridge(pipe, folder.Path, LayoutOnly) { ConnectTimeout = TimeSpan.FromSeconds(5) };
        await using var agent = await InProcess.Start(bridge.Options());
        var tools = await agent.Client.ListToolsAsync(cancellationToken: Cancel);
        Assert.Equal(ToolGroups.Scene.Count + 1, tools.Count);
        Assert.Equal("Scene tools only.\n\nLive in the test window.", agent.Client.ServerInstructions);
        await agent.Client.Json("add_null", new { name = "Pin" });
        Assert.NotNull(await ui.InvokeAsync(() => app.Layout.Scene.Find("Pin")));
        Assert.Contains("Another Test Layout", Assert.Throws<IOException>(() => new LiveListener(pipe, root => new LiveHost(ui, app, root), LayoutOnly).Start()).Message);

        // With no window open, the bridge says so in the profile's words.
        var nowhere = new LiveBridge("avamodeler-none-" + Guid.NewGuid().ToString("N")[..12], folder.Path, LayoutOnly) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        await using var lonely = await InProcess.Start(nowhere.Options());
        Assert.Contains("No Test Layout is open.", await lonely.Client.Fails("get_scene"));
    }

    [Fact]
    public async Task The_command_line_serves_a_profile_headless()
    {
        using var folder = new TempFolder();
        using var up = new Wire();
        using var down = new Wire();
        var log = new StringWriter();
        var run = Task.Run(() => McpCommandLine.RunAsync(["--mcp", "--root", folder.Path, "--no-plugins"], _ => ToolRegistry.Create(), up.Reader, down.Writer, log, LayoutOnly, Cancel));
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(up.Writer, down.Reader), null, null, Cancel))
        {
            Assert.Equal("TestLayout", client.ServerInfo!.Name);
            await client.Json("add_null", new { name = "Pin" });
            await client.Json("save_scene", new { path = "pin.avascene" });
        }
        up.Writer.Dispose();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel));
        Assert.True(File.Exists(folder.File("pin.avascene")));
        Assert.Contains("Test Layout MCP server (headless)", log.ToString());
        var help = new StringWriter();
        Assert.Equal(0, await McpCommandLine.RunAsync(["--mcp", "--help"], _ => ToolRegistry.Create(), Stream.Null, Stream.Null, help, LayoutOnly with { Usage = "TestLayout --mcp" }, Cancel));
        Assert.Equal("TestLayout --mcp", help.ToString().Trim());
    }
}
