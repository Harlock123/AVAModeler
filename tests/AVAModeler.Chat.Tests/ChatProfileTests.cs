using AVAModeler.Mcp;
using static AVAModeler.Chat.Tests.FakeModelServer;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// The chat in another app on this engine (a standalone Layout): its own server profile's tools, its own system prompt
/// and core tool set, and its name in exported chats. AVA Modeler's own are the defaults.
/// </summary>
public class ChatProfileTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly ServerProfile SceneOnly = new()
    {
        Name = "SceneApp", Title = "Scene App", Instructions = "Scene tools only.", IncludeTool = ToolGroups.Scene.Contains, HasModelerObject = false,
    };

    [Fact]
    public void The_defaults_are_ava_modelers()
    {
        var chat = new ChatSession(new NoTools());
        Assert.Equal(ChatPrompt.System, chat.SystemPrompt);
        Assert.Same(ChatTools.Core, chat.CoreTools);
        Assert.Equal(ChatPrompt.CoreToolsNote, chat.CoreToolsNote);
    }

    [Fact]
    public async Task Claude_gets_the_apps_prompt_and_its_profiles_tools()
    {
        var api = new FakeClaude().Reply(FakeClaude.Say("Hello."));
        var host = new HeadlessHost(Path.GetTempPath());
        await using var bridge = await McpToolBridge.StartAsync(host, SceneOnly, Cancel);
        var chat = new ChatSession(bridge) { Client = api.Client(), SystemPrompt = "You are in Scene App." };
        await chat.SendAsync("hi", Cancel);
        var request = api.Requests.Single();
        Assert.Equal("You are in Scene App.", (string?)request["system"]![0]!["text"]);
        var names = request["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ToolGroups.Scene.Count, names.Count);
        Assert.DoesNotContain("run_tool", names);
        Assert.Equal("Scene tools only.", bridge.ServerInstructions);
    }

    [Fact]
    public async Task A_local_model_gets_the_apps_prompt_core_tools_and_note()
    {
        var server = new FakeModelServer().Reply(Say("ok"));
        var host = new HeadlessHost(Path.GetTempPath());
        await using var bridge = await McpToolBridge.StartAsync(host, SceneOnly, Cancel);
        var chat = new ChatSession(bridge)
        {
            Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen3", CoreToolsOnly = true,
            SystemPrompt = "You are in Scene App.", CoreTools = ["get_scene", "add_light", "render_scene"], CoreToolsNote = "A few scene tools.",
        };
        await chat.SendAsync("hi", Cancel);
        var request = server.Requests.Single();
        Assert.Equal(["add_light", "get_scene", "render_scene"], request["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).Order());
        var system = (string?)request["messages"]![0]!["content"];
        Assert.StartsWith("You are in Scene App.", system);
        Assert.Contains("A few scene tools.", system);
    }

    [Fact]
    public void An_exported_chat_names_its_app()
    {
        var chat = new StoredChat { Id = "x", Title = "Lights", Provider = ChatProvider.Anthropic, Model = ChatModels.Opus, Created = DateTimeOffset.UnixEpoch };
        Assert.Contains("(AVA Modeler)_", ChatMarkdown.Write(chat));
        Assert.Contains("(Scene App)_", ChatMarkdown.Write(chat, null, "Scene App"));
    }

    private sealed class NoTools : IToolBridge
    {
        public Task<IReadOnlyList<ModelContextProtocol.Protocol.Tool>> ListToolsAsync(CancellationToken cancel = default) => Task.FromResult<IReadOnlyList<ModelContextProtocol.Protocol.Tool>>([]);
        public Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, System.Text.Json.JsonElement> input, CancellationToken cancel = default) => Task.FromResult(ToolOutcome.Error("none"));
    }
}
