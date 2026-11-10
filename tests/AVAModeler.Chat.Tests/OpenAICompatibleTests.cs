using System.Text.Json.Nodes;
using AVAModeler.Mcp;
using static AVAModeler.Chat.Tests.FakeModelServer;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// The chat on an OpenAI-compatible server (a local model through Ollama's /v1, LM Studio, vLLM, or a gateway), against
/// a fake server that scripts server-sent events. No real model server is ever called.
/// </summary>
public class OpenAICompatibleTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static JsonArray Messages(JsonObject request) => request["messages"]!.AsArray();

    [Fact]
    public async Task Streamed_text_shows_and_the_request_has_the_system_prompt_tools_and_model()
    {
        var server = new FakeModelServer().Reply(Say("Hello there, what shall we build?"));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hi");

        var reply = Assert.IsType<AssistantEntry>(rig.Chat.Shown()[^1]);
        Assert.Equal("Hello there, what shall we build?", reply.Text);
        Assert.False(reply.Streaming);
        Assert.False(rig.Chat.Working);

        var r = server.Requests.Single();
        Assert.Equal("qwen3", (string?)r["model"]);
        Assert.True((bool?)r["stream"]);
        Assert.Equal("auto", (string?)r["tool_choice"]);
        var messages = Messages(r);
        Assert.Equal("system", (string?)messages[0]!["role"]);
        Assert.StartsWith(ModelerServer.Instructions.TrimEnd()[..200], (string?)messages[0]!["content"]);
        Assert.Contains("AI Chat panel", (string?)messages[0]!["content"]);
        Assert.Equal("user", (string?)messages[1]!["role"]);
        Assert.Equal("hi", (string?)messages[1]!["content"]);

        // Every MCP tool as a function, its schema as the parameters (no cache breakpoints: those are Anthropic's).
        var tools = r["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var definitions = ModelerServer.Definitions();
        Assert.Equal(definitions.Select(d => d.Name), tools.Select(t => (string?)t["function"]!["name"]));
        foreach (var (mcp, tool) in definitions.Zip(tools))
        {
            Assert.Equal("function", (string?)tool["type"]);
            Assert.Equal(mcp.Description, (string?)tool["function"]!["description"]);
            var expected = JsonNode.Parse(mcp.InputSchema.GetRawText())!.AsObject();
            expected["properties"] ??= new JsonObject();
            Assert.True(JsonNode.DeepEquals(expected, tool["function"]!["parameters"]), $"{mcp.Name}'s schema changed on the way.");
        }
        Assert.DoesNotContain("cache_control", r.ToJsonString());
        // A local server gets no key.
        Assert.Null(server.Authorization.Single());
    }

    [Fact]
    public async Task Tool_calls_streamed_in_pieces_are_put_together_run_and_answered()
    {
        var server = new FakeModelServer()
            .Reply(Say("I'll make a ball."), Call("call_1", "run_tool", new { name = "Ball" }))
            .Reply(Call("call_2", "render_view", new { view = "perspective", width = 160, height = 120 }))
            .Reply(Say("There's your ball."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("make a ball");

        // The call ran on the document as one undo step.
        var doc = rig.Host.Commands.Document;
        Assert.True(doc.Current.Mesh.Points.Count > 20);
        Assert.Equal(1, doc.Undo.Count);
        Assert.Equal(0, server.Left);
        var entries = rig.Chat.Shown();
        Assert.Equal("I'll make a ball.", Assert.IsType<AssistantEntry>(entries[1]).Text);
        var ball = Assert.IsType<ToolEntry>(entries[2]);
        Assert.StartsWith("run_tool Ball →", ball.Line);
        Assert.False(ball.IsError);
        var look = entries.OfType<ToolEntry>().Single(e => e.Name == "render_view");
        var image = Assert.Single(look.Images);
        Assert.Equal("There's your ball.", ((AssistantEntry)entries[^1]).Text);

        // The second request: the assistant's message with its call put together, then the tool's result.
        var second = Messages(server.Requests[1]);
        Assert.Equal(["system", "user", "assistant", "tool"], second.Select(m => (string)m!["role"]!));
        var assistant = second[2]!;
        Assert.Equal("I'll make a ball.", (string?)assistant["content"]);
        var call = assistant["tool_calls"]!.AsArray().Single()!;
        Assert.Equal("call_1", (string?)call["id"]);
        Assert.Equal("function", (string?)call["type"]);
        Assert.Equal("run_tool", (string?)call["function"]!["name"]);
        Assert.Equal("""{"name":"Ball"}""", (string?)call["function"]!["arguments"]);
        Assert.Equal("call_1", (string?)second[3]!["tool_call_id"]);
        Assert.Contains("Ball", (string?)second[3]!["content"]);

        // The third: the picture follows the tool message, in a user message, as an image part.
        var third = Messages(server.Requests[2]);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "tool", "user"], third.Select(m => (string)m!["role"]!));
        Assert.Null(third[4]!["content"]);
        Assert.Equal("call_2", (string?)third[5]!["tool_call_id"]);
        Assert.Contains("Perspective view", (string?)third[5]!["content"]);
        Assert.Contains("in the next message", (string?)third[5]!["content"]);
        var parts = third[6]!["content"]!.AsArray();
        Assert.Equal("text", (string?)parts[0]!["type"]);
        Assert.Contains("render_view (call_2)", (string?)parts[0]!["text"]);
        Assert.Equal("image_url", (string?)parts[1]!["type"]);
        var url = (string)parts[1]!["image_url"]!["url"]!;
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(image.Data, Convert.FromBase64String(url["data:image/png;base64,".Length..]));
    }

    [Fact]
    public async Task Several_tool_calls_in_one_turn_run_in_order_each_with_its_result()
    {
        var server = new FakeModelServer()
            .Reply(Call("call_a", "run_tool", new { name = "Box" }), Call("call_b", "get_document", new { }), Call("call_c", "check_mesh", new { }))
            .Reply(Say("A closed box."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("a box, then check it");
        var tools = rig.Chat.Shown().OfType<ToolEntry>().ToList();
        Assert.Equal(["run_tool", "get_document", "check_mesh"], tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.False(t.IsError));
        Assert.Contains("Box", tools[0].Input);

        var second = Messages(server.Requests[1]);
        var calls = second[2]!["tool_calls"]!.AsArray();
        Assert.Equal(["call_a", "call_b", "call_c"], calls.Select(c => (string)c!["id"]!));
        Assert.Equal(["run_tool", "get_document", "check_mesh"], calls.Select(c => (string)c!["function"]!["name"]!));
        Assert.Equal("{}", (string?)calls[1]!["function"]!["arguments"]);
        Assert.Equal(["call_a", "call_b", "call_c"], second.Skip(3).Select(m => (string)m!["tool_call_id"]!));
        // The box was made before it was looked at.
        Assert.Contains("\"points\"", (string?)second[4]!["content"]);
        Assert.Equal(8, rig.Host.Commands.Document.Current.Mesh.Points.Count);
    }

    [Fact]
    public async Task A_model_that_cant_see_pictures_is_told_so_instead()
    {
        var server = new FakeModelServer()
            .Reply(Call("call_1", "run_tool", new { name = "Ball" }), Call("call_2", "render_view", new { width = 120, height = 90 }))
            .Reply(Say("Done."));
        await using var rig = await ServerRig.Start(server, pictures: false);
        await rig.Send("a ball, and look");
        // The person still sees the picture.
        Assert.Single(rig.Chat.Shown().OfType<ToolEntry>().Single(t => t.Name == "render_view").Images);
        var second = server.Requests[1];
        Assert.DoesNotContain("image_url", second.ToJsonString());
        var messages = Messages(second);
        Assert.Equal("tool", (string?)messages[^1]!["role"]);
        Assert.Contains("can't see pictures", (string?)messages[^1]!["content"]);
        Assert.Contains(ChatPrompt.NoPicturesNote, (string?)messages[0]!["content"]);
    }

    [Fact]
    public async Task Core_tools_only_sends_the_short_list_and_says_so()
    {
        var server = new FakeModelServer().Reply(Say("ok"));
        await using var rig = await ServerRig.Start(server, coreTools: true);
        await rig.Send("hi");
        var sent = server.Requests[0]["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).ToList();
        var all = ModelerServer.Definitions().Select(d => d.Name).ToList();
        // Every core tool exists on the server, and they go in the server's order.
        Assert.All(ChatTools.Core, name => Assert.Contains(name, all));
        Assert.Equal(all.Where(ChatTools.Core.Contains), sent);
        Assert.Equal(ChatTools.Core.Count, sent.Count);
        Assert.True(sent.Count < all.Count / 2);
        Assert.Contains("check_mesh", sent);
        Assert.Contains("render_view", sent);
        Assert.Contains(ChatPrompt.CoreToolsNote, (string?)Messages(server.Requests[0])[0]!["content"]);
    }

    public static TheoryData<string, int, string, string[]> Errors => new()
    {
        { "unauthorized", 401, """{"error":{"message":"Invalid API key","type":"invalid_request_error"}}""", ["401", "Key…", "localhost:11434"] },
        { "model not found", 404, """{"error":{"message":"model \"qwen3\" not found, try pulling it first","type":"api_error"}}""", ["404", "ollama pull", "not found, try pulling it first"] },
        { "no tools", 400, """{"error":{"message":"registry.ollama.ai/library/gemma:2b does not support tools"}}""", ["400", "tool calls"] },
        { "no pictures", 400, """{"object":"error","message":"This model does not support image input"}""", ["400", "Model sees pictures"] },
        { "server", 500, """{"error":"llama runner process has terminated"}""", ["500", "llama runner"] },
        { "busy", 429, "", ["429"] },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Server_errors_show_as_one_plain_line_with_Retry_and_Retry_works(string kind, int status, string body, string[] says)
    {
        var server = new FakeModelServer().Error(status, body).Reply(Say("Here I am."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hello");
        var error = Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]);
        Assert.Equal(NoticeKind.Error, error.Kind);
        Assert.True(error.CanRetry);
        Assert.DoesNotContain("\n", error.Text);
        foreach (var s in says) Assert.Contains(s, error.Text);
        Assert.False(string.IsNullOrEmpty(kind));

        await rig.Chat.RetryAsync(Cancel);
        Assert.Equal("Here I am.", ((AssistantEntry)rig.Chat.Shown()[^1]).Text);
        Assert.Equal(Messages(server.Requests[0]).ToJsonString(), Messages(server.Requests[1]).ToJsonString());
    }

    [Fact]
    public async Task A_server_that_isnt_running_is_explained()
    {
        var server = new FakeModelServer().Refuse();
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hello");
        var error = Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]);
        Assert.Equal("Couldn't reach the model server at localhost:11434. Is it running (for Ollama, start it or run \"ollama serve\"), and is the base URL right? Then Retry.", error.Text);
        Assert.True(error.CanRetry);
    }

    [Fact]
    public async Task A_gateways_key_goes_only_in_its_header_and_never_shows()
    {
        // A gateway that echoes the key in its error message: the line shows only its mask.
        var server = new FakeModelServer("https://ai.example.internal/api/v1")
            .Error(401, "{\"error\":{\"message\":\"key " + Key + " has expired\"}}")
            .Reply(Say("ok"));
        await using var rig = await ServerRig.Start(server);
        rig.Chat.Server = server.Client(Key);
        await rig.Send("hello");
        var error = Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]);
        Assert.Contains("ai.example.internal", error.Text);
        Assert.DoesNotContain(Key, error.Text);
        Assert.DoesNotContain(Key[8..^4], error.Text);
        Assert.Contains(ApiKeyStore.Mask(Key), error.Text);
        await rig.Chat.RetryAsync(Cancel);
        Assert.All(server.Authorization, a => Assert.Equal("Bearer " + Key, a));
        Assert.All(server.Requests, r => Assert.DoesNotContain(Key, r.ToJsonString()));
        Assert.False(server.Client(Key).IsLocal);
    }

    [Fact]
    public async Task Stop_ends_a_reply_that_is_still_coming()
    {
        var server = new FakeModelServer().StartThenHang("I'll start with").Reply(Say("Back again."));
        await using var rig = await ServerRig.Start(server);
        var send = rig.Send("hello");
        for (int i = 0; i < 500 && !rig.Chat.Shown().OfType<AssistantEntry>().Any(); i++) await Task.Delay(10, Cancel);
        Assert.True(rig.Chat.Working);
        Assert.Equal("I'll start with", rig.Chat.Shown().OfType<AssistantEntry>().Single().Text);
        rig.Chat.Stop();
        await send.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        Assert.False(rig.Chat.Working);
        Assert.Equal("Stopped.", Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]).Text);
        Assert.EndsWith("(stopped)", rig.Chat.Shown().OfType<AssistantEntry>().Single().Text);
        // Nothing half-made is kept: the next request has the person's two messages and no reply between them.
        await rig.Send("are you there?");
        Assert.Equal(["system", "user", "user"], Messages(server.Requests[^1]).Select(m => (string)m!["role"]!));
        Assert.Equal("Back again.", ((AssistantEntry)rig.Chat.Shown()[^1]).Text);
    }

    [Fact]
    public async Task The_tool_limit_and_auto_continue_work_the_same_way()
    {
        var server = new FakeModelServer();
        for (int i = 0; i < 4; i++) server.Reply(Call($"call_{i}a", "run_tool", new { name = "Box" }), Call($"call_{i}b", "get_document", new { }));
        await using var rig = await ServerRig.Start(server);
        rig.Chat.MaxToolCalls = 3;
        rig.Chat.AutoContinues = 1;
        await rig.Send("boxes forever");
        // 3 calls, a notice, 3 more, then the seventh and eighth are refused and the chat stops.
        Assert.Equal(6, rig.Chat.Shown().OfType<ToolEntry>().Count());
        Assert.Contains("carrying on by itself (1 of 1", rig.Chat.Shown().OfType<NoticeEntry>().First().Text);
        var stop = Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]);
        Assert.Contains("Stopped after 6 tool calls", stop.Text);
        Assert.Contains("the model carry on", stop.Text);
        Assert.Equal(0, server.Left);
        // Every call has its tool message, the refused one included, so "continue" can carry on.
        server.Reply(Say("Done."));
        await rig.Send("continue");
        var last = Messages(server.Requests[^1]);
        var ids = last.Where(m => (string?)m!["role"] == "assistant").SelectMany(m => m!["tool_calls"]!.AsArray()).Select(c => (string)c!["id"]!).ToList();
        var answered = last.Where(m => (string?)m!["role"] == "tool").Select(m => (string)m!["tool_call_id"]!).ToList();
        Assert.Equal(ids, answered);
        Assert.Contains(last, m => (string?)m!["role"] == "tool" && ((string?)m["content"] ?? "").Contains("limit of 3 tool calls"));
        Assert.Equal("continue", (string?)last[^1]!["content"]);
    }

    [Fact]
    public async Task Arguments_that_arent_JSON_go_back_as_an_error_and_the_model_carries_on()
    {
        var server = new FakeModelServer()
            .Reply(RawCall("call_1", "run_tool", "{name: Ball"))
            .Reply(Call("call_2", "run_tool", new { name = "Ball" }))
            .Reply(Say("Fixed it."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("a ball");
        var tools = rig.Chat.Shown().OfType<ToolEntry>().ToList();
        Assert.True(tools[0].IsError);
        Assert.Contains("weren't valid JSON", tools[0].Result);
        Assert.False(tools[1].IsError);
        var result = Messages(server.Requests[1])[^1]!;
        Assert.Equal("call_1", (string?)result["tool_call_id"]);
        Assert.StartsWith("Error: Not run", (string?)result["content"]);
        Assert.Equal(1, rig.Host.Commands.Document.Undo.Count);
    }

    [Fact]
    public async Task A_server_that_doesnt_stream_still_answers()
    {
        var server = new FakeModelServer().Whole("All in one go.");
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hi");
        Assert.Equal("All in one go.", ((AssistantEntry)rig.Chat.Shown()[^1]).Text);
    }

    [Fact]
    public async Task A_reply_cut_short_says_so()
    {
        var server = new FakeModelServer().Reply("length", Say("A long story that"));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("tell me everything");
        Assert.Contains("The model's reply reached its length limit", Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]).Text);
    }

    [Fact]
    public async Task Without_a_model_the_chat_says_how_to_set_one()
    {
        var server = new FakeModelServer();
        await using var rig = await ServerRig.Start(server);
        rig.Chat.ServerModel = "  ";
        await rig.Send("hello");
        var notice = Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]);
        Assert.Contains("Setup…", notice.Text);
        Assert.Contains(OpenAICompatibleClient.DefaultBaseUrl, notice.Text);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_base_URL_without_http_is_explained()
    {
        await using var rig = await ServerRig.Start();
        rig.Chat.Server = new OpenAICompatibleClient(new HttpClient(rig.Server), "localhost:11434/v1");
        await rig.Send("hello");
        Assert.Contains("must start with http:// or https://", Assert.IsType<NoticeEntry>(rig.Chat.Shown()[^1]).Text);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task Changing_provider_starts_a_new_chat()
    {
        var server = new FakeModelServer().Reply(Say("Local hello."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hi");
        Assert.NotEmpty(rig.Chat.ServerMessages);
        int cleared = 0;
        rig.Chat.Cleared += () => cleared++;
        rig.Chat.Provider = ChatProvider.Anthropic;
        Assert.Equal(1, cleared);
        Assert.Empty(rig.Chat.Shown());
        Assert.Empty(rig.Chat.ServerMessages);
        Assert.Equal("Claude", rig.Chat.Who);
        rig.Chat.Provider = ChatProvider.Anthropic;
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Model_lists_are_read_from_OpenAI_and_Ollama_shapes()
    {
        Assert.Equal(["llama3.1:8b", "qwen3:latest"], OpenAICompatibleClient.ParseModels(
            """{"object":"list","data":[{"id":"qwen3:latest","object":"model","owned_by":"library"},{"id":"llama3.1:8b","object":"model"},{"id":"qwen3:latest"}]}"""));
        Assert.Equal(["mistral-small"], OpenAICompatibleClient.ParseModels("""{"models":[{"name":"mistral-small","model":"mistral-small"}]}"""));
        Assert.Empty(OpenAICompatibleClient.ParseModels("""{"data":[]}"""));
        Assert.Empty(OpenAICompatibleClient.ParseModels("""{"something":"else"}"""));
    }

    [Fact]
    public async Task Refresh_lists_the_servers_models_or_explains_why_not()
    {
        var server = new FakeModelServer { ModelsJson = """{"object":"list","data":[{"id":"qwen3:latest"},{"id":"gemma3:12b"}]}""" };
        Assert.Equal(["gemma3:12b", "qwen3:latest"], await server.Client(Key).ListModelsAsync(Cancel));
        Assert.Equal("Bearer " + Key, server.Authorization.Single());

        server.ModelsJson = null;
        var e = await Assert.ThrowsAsync<OpenAICompatibleException>(() => server.Client().ListModelsAsync(Cancel));
        Assert.Contains("404", ChatErrors.Describe(e));
        Assert.Contains("/v1", ChatErrors.Describe(e));

        var down = new FakeModelServer();
        var refused = new OpenAICompatibleClient(new HttpClient(new Refusing()), down.BaseUrl);
        var r = await Assert.ThrowsAsync<OpenAICompatibleException>(() => refused.ListModelsAsync(Cancel));
        Assert.Equal(OpenAICompatibleProblem.Unreachable, r.Problem);
        Assert.Contains("Couldn't reach the model server", ChatErrors.Describe(r));
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public void Servers_are_named_without_user_names_or_paths()
    {
        Assert.Equal("localhost:11434", OpenAICompatibleClient.Describe("http://localhost:11434/v1"));
        Assert.Equal("gateway.example", OpenAICompatibleClient.Describe("https://user:secret@gateway.example/v1"));
        Assert.Equal("an unusable address", OpenAICompatibleClient.Describe("localhost:11434"));
        Assert.True(new OpenAICompatibleClient(new HttpClient(), "http://127.0.0.1:1234/v1/").IsLocal);
        Assert.Equal("http://127.0.0.1:1234/v1", new OpenAICompatibleClient(new HttpClient(), " http://127.0.0.1:1234/v1/ ").BaseUrl);
    }

    [Fact]
    public void A_gateways_key_has_a_file_of_its_own_owner_only()
    {
        var folder = Path.Combine(Path.GetTempPath(), "avamodeler-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ApiKeyStore.ForServer(folder);
            Assert.Equal(Path.Combine(folder, ApiKeyStore.ServerFileName), store.Path);
            Assert.NotEqual(ApiKeyStore.FileName, ApiKeyStore.ServerFileName);
            Assert.Null(store.Get());
            store.Save(Key);
            Assert.Equal(Key, ApiKeyStore.ForServer(folder).Get());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path!));
            // It's not the Anthropic key's file.
            Assert.False(File.Exists(Path.Combine(folder, ApiKeyStore.FileName)));
            store.Remove();
            Assert.Null(store.Get());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
