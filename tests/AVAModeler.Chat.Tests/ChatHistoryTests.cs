using System.Text.Json.Nodes;
using AVAModeler.Mcp;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// Saving chats and opening them again: in a temporary folder (never the app-data folder), against the fake Claude and
/// the fake OpenAI-compatible server only. A saved chat carries on in its provider's own format, is read-only with the
/// other provider, exports as Markdown, keeps no key, and keeps pictures within a size limit.
/// </summary>
public sealed class ChatHistoryTests : IDisposable
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-chats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private static readonly DateTimeOffset When = new(2026, 11, 4, 15, 30, 0, TimeSpan.Zero);

    /// <summary>Saves a chat into a history, its pictures kept as the panel keeps them.</summary>
    private static StoredChat Save(ChatSession chat, ChatHistory history)
    {
        long used = 0;
        var stored = chat.ToStored(i => history.KeepImage(chat.ChatId, i, ref used));
        history.Save(stored);
        return stored;
    }

    private static Func<string, ToolImage?> Pictures(ChatHistory h, string id) => name => h.ReadImage(id, name);

    [Fact]
    public async Task A_Claude_chat_is_saved_opened_again_and_carried_on_in_Anthropics_format()
    {
        var api = new FakeClaude()
            .Reply(FakeClaude.Say("I'll make a ball."), FakeClaude.Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(FakeClaude.Call("toolu_2", "render_view", new { view = "perspective", width = 96, height = 72 }))
            .Reply(FakeClaude.Say("There's your ball."));
        await using var rig = await Rig.Start(api);
        rig.Chat.Clock = () => When;
        await rig.Send("make a ball");
        var history = new ChatHistory(_folder);
        var stored = Save(rig.Chat, history);
        Assert.Equal("make a ball", stored.Title);
        Assert.Equal(ChatProvider.Anthropic, stored.Provider);
        Assert.Equal(ChatModels.Opus, stored.Model);
        Assert.StartsWith("20261104-153000-", stored.Id);
        Assert.Equal(["user", "assistant", "results", "assistant", "results", "assistant"], stored.Turns.Select(t => t.Role));

        var list = history.List();
        var summary = Assert.Single(list);
        Assert.Equal(("make a ball", ChatProvider.Anthropic, 3), (summary.Title, summary.Provider, summary.Messages));
        // The picture is a file of its own, kept once though the panel and the conversation both have it.
        var pictures = Directory.GetFiles(Path.Combine(_folder, stored.Id));
        Assert.Single(pictures);

        // Opened in a new session (a new run of the app), it shows as it was and carries on.
        var later = new FakeClaude().Reply(FakeClaude.Say("You're welcome."));
        await using var again = await Rig.Start(later);
        var loaded = history.Load(stored.Id)!;
        again.Chat.Open(loaded, Pictures(history, stored.Id));
        Assert.False(again.Chat.ReadOnly);
        Assert.Equal(stored.Id, again.Chat.ChatId);
        var entries = again.Chat.Entries;
        Assert.Equal("make a ball", Assert.IsType<UserEntry>(entries[0]).Text);
        Assert.Equal("I'll make a ball.", Assert.IsType<AssistantEntry>(entries[1]).Text);
        var look = entries.OfType<ToolEntry>().Single(e => e.Name == "render_view");
        Assert.Equal(0x89, Assert.Single(look.Images).Data[0]);
        Assert.Contains("\"name\": \"Ball\"", entries.OfType<ToolEntry>().First().Input);
        await again.Send("thanks");
        var r = later.Requests.Single();
        var messages = r["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user", "assistant", "user", "assistant", "user"], messages.Select(m => (string)m!["role"]!));
        // The same tool calls and results, ids and all, the picture back as an image block, and the new words last.
        var uses = messages.SelectMany(m => m!["content"] is JsonArray a ? a.OfType<JsonObject>() : []).Where(b => (string?)b["type"] == "tool_use").ToList();
        Assert.Equal(["toolu_1", "toolu_2"], uses.Select(u => (string)u["id"]!));
        Assert.Equal("Ball", (string?)uses[0]["input"]!["name"]);
        var result = Json.Results(r).Single(x => (string?)x["tool_use_id"] == "toolu_2");
        Assert.Contains(result["content"]!.AsArray(), b => (string?)b!["type"] == "image");
        var last = Json.LastBlocks(r);
        Assert.Equal("thanks", (string?)last[^1]!["text"]);
        Assert.Equal("You're welcome.", ((AssistantEntry)again.Chat.Entries[^1]).Text);
    }

    [Fact]
    public async Task An_OpenAI_compatible_chat_carries_on_in_its_own_format()
    {
        var server = new FakeModelServer()
            .Reply(FakeModelServer.Say("A ball, then."), FakeModelServer.Call("call_1", "run_tool", new { name = "Ball" }))
            .Reply(FakeModelServer.Say("Done."));
        await using var rig = await ServerRig.Start(server);
        rig.Chat.ServerName = "localhost:11434";
        await rig.Send("a ball please");
        var history = new ChatHistory(_folder);
        var stored = Save(rig.Chat, history);
        Assert.Equal((ChatProvider.OpenAICompatible, "qwen3", "localhost:11434"), (stored.Provider, stored.Model, stored.Server));

        var later = new FakeModelServer().Reply(FakeModelServer.Say("Bigger it is."));
        await using var again = await ServerRig.Start(later);
        again.Chat.Open(history.Load(stored.Id)!, Pictures(history, stored.Id));
        Assert.False(again.Chat.ReadOnly);
        await again.Send("make it bigger");
        var messages = later.Requests.Single()["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], messages.Select(m => (string)m!["role"]!));
        var call = messages[2]!["tool_calls"]!.AsArray().Single()!;
        Assert.Equal("call_1", (string?)call["id"]);
        Assert.Equal("run_tool", (string?)call["function"]!["name"]);
        Assert.Equal("Ball", (string?)JsonNode.Parse((string)call["function"]!["arguments"]!)!["name"]);
        Assert.Equal("call_1", (string?)messages[3]!["tool_call_id"]);
        Assert.Equal("make it bigger", (string?)messages[^1]!["content"]);
    }

    [Fact]
    public async Task A_chat_with_the_other_provider_opens_read_only_until_that_provider_is_chosen()
    {
        var api = new FakeClaude().Reply(FakeClaude.Say("Hello."));
        await using var rig = await Rig.Start(api);
        await rig.Send("hi");
        var history = new ChatHistory(_folder);
        var stored = Save(rig.Chat, history);

        var server = new FakeModelServer().Reply(FakeModelServer.Say("Should not be asked."));
        await using var local = await ServerRig.Start(server);
        int changed = 0;
        local.Chat.ReadOnlyChanged += () => changed++;
        local.Chat.Open(history.Load(stored.Id)!, Pictures(history, stored.Id));
        Assert.True(local.Chat.ReadOnly);
        Assert.Equal(ChatProvider.Anthropic, local.Chat.ReadOnlyProvider);
        Assert.Equal(1, changed);
        Assert.Equal(2, local.Chat.Entries.Count);
        // Nothing is sent to the other provider, and a read-only chat saves as it was.
        await local.Send("can you hear me");
        Assert.Empty(server.Requests);
        Assert.Equal(2, local.Chat.Entries.Count);
        var resaved = local.Chat.ToStored(_ => null);
        Assert.Equal((ChatProvider.Anthropic, ChatModels.Opus), (resaved.Provider, resaved.Model));
        // Choosing Claude again brings it to life: it carries on, with the fake Claude.
        var claude = new FakeClaude().Reply(FakeClaude.Say("Yes."));
        local.Chat.Client = claude.Client();
        local.Chat.Provider = ChatProvider.Anthropic;
        Assert.False(local.Chat.ReadOnly);
        await local.Send("can you hear me");
        Assert.Equal(["user", "assistant", "user"], claude.Requests.Single()["messages"]!.AsArray().Select(m => (string)m!["role"]!));
        // New Chat leaves it.
        local.Chat.NewChat();
        Assert.Empty(local.Chat.Entries);
        Assert.Equal("", local.Chat.ChatId);
    }

    [Fact]
    public async Task No_key_is_ever_written_and_files_are_the_owners_only()
    {
        var api = new FakeClaude().Reply(FakeClaude.Say("Hello."));
        await using var rig = await Rig.Start(api);
        await rig.Send("hi");
        var history = new ChatHistory(_folder);
        Save(rig.Chat, history);
        var gateway = new FakeModelServer("https://gateway.example.com/v1").Reply(FakeModelServer.Say("Hi from the gateway."));
        await using var g = await ServerRig.Start(gateway);
        g.Chat.Server = gateway.Client(FakeModelServer.Key);
        g.Chat.ServerName = OpenAICompatibleClient.Describe(gateway.BaseUrl);
        await g.Send("hello");
        Save(g.Chat, history);
        Assert.Equal(2, history.List().Count);
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(FakeClaude.Key, text);
            Assert.DoesNotContain(FakeModelServer.Key, text);
            Assert.DoesNotContain("sk-", text);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/v1", text);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_folder));
    }

    [Fact]
    public async Task Pictures_over_the_limit_are_left_out_and_said_to_be()
    {
        var api = new FakeClaude()
            .Reply(FakeClaude.Call("toolu_1", "render_view", new { view = "front", width = 160, height = 120 }))
            .Reply(FakeClaude.Say("Looks fine."));
        await using var rig = await Rig.Start(api);
        await rig.Send("look");
        var history = new ChatHistory(_folder) { MaxImageBytes = 10 };
        var stored = Save(rig.Chat, history);
        var tool = stored.Entries.Single(e => e.Kind == "tool");
        Assert.Empty(tool.Images!);
        Assert.Equal(1, tool.ImagesLeftOut);
        Assert.False(Directory.Exists(Path.Combine(_folder, stored.Id)));
        var again = new ChatSession(rig.Bridge);
        again.Open(history.Load(stored.Id)!, Pictures(history, stored.Id));
        var shown = again.Entries.OfType<ToolEntry>().Single();
        Assert.Empty(shown.Images);
        Assert.Contains("too big to keep", shown.Result);
    }

    [Fact]
    public async Task Chats_are_listed_newest_first_deleted_cleared_and_damaged_files_skipped()
    {
        var api = new FakeClaude().Reply(FakeClaude.Say("One.")).Reply(FakeClaude.Say("Two."));
        await using var rig = await Rig.Start(api);
        var history = new ChatHistory(_folder);
        rig.Chat.Clock = () => When;
        await rig.Send("first");
        var first = Save(rig.Chat, history);
        rig.Chat.NewChat();
        rig.Chat.Clock = () => When.AddHours(1);
        await rig.Send("second");
        var second = Save(rig.Chat, history);
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ not json");
        Assert.Equal(["second", "first"], history.List().Select(c => c.Title));
        Assert.Null(history.Load("../etc/passwd"));
        history.Delete(first.Id);
        Assert.Equal(["second"], history.List().Select(c => c.Title));
        Assert.Null(history.Load(first.Id));
        history.Clear();
        Assert.Empty(history.List());
        Assert.False(File.Exists(Path.Combine(_folder, second.Id + ".json")));
    }

    [Fact]
    public async Task A_chat_exports_as_Markdown_with_its_pictures_beside_it()
    {
        var api = new FakeClaude()
            .Reply(FakeClaude.Say("Making a box."), FakeClaude.Call("toolu_1", "run_tool", new { name = "Box" }))
            .Reply(FakeClaude.Call("toolu_2", "render_view", new { view = "top", width = 64, height = 48 }))
            .Reply(FakeClaude.Say("A 1 m box."));
        await using var rig = await Rig.Start(api);
        rig.Chat.Clock = () => When;
        await rig.Send("make a box");
        var images = new Dictionary<string, ToolImage>();
        var stored = rig.Chat.ToStored(i => { var n = ChatHistory.ImageName(i); images[n] = i; return n; });
        var path = Path.Combine(_folder, "box chat.md");
        Directory.CreateDirectory(_folder);
        var written = ChatMarkdown.Save(stored, n => images.GetValueOrDefault(n), path);
        Assert.Equal(2, written.Count);
        var md = File.ReadAllText(path);
        Assert.StartsWith("# make a box\n\n_Claude Opus 5.5 (Anthropic API), 2026-11-04 15:30 (AVA Modeler)_", md);
        Assert.Contains("**You:** make a box", md);
        Assert.Contains("**Claude:** Making a box.", md);
        Assert.Contains("- `run_tool` → ", md);
        Assert.Contains("```json", md);
        Assert.Contains("](box chat-images/", md);
        Assert.Contains("**Claude:** A 1 m box.", md);
        Assert.True(File.Exists(written[1]));
        // Without the pictures, they're mentioned in words.
        Assert.Contains("(a picture from render_view)", ChatMarkdown.Write(stored));
    }

    [Fact]
    public async Task A_chat_saved_in_the_middle_of_a_request_carries_on_with_its_calls_answered()
    {
        var api = new FakeClaude().Reply(FakeClaude.Say("Hi."));
        await using var rig = await Rig.Start(api);
        // A chat whose last turn asked for a tool that never ran (the app closed then).
        var stored = new StoredChat
        {
            Id = "20261104-000000-abcdef", Title = "unfinished", Provider = ChatProvider.Anthropic, Model = ChatModels.Opus,
            Entries = [new StoredEntry { Kind = "user", Text = "make a ball" }],
            Turns =
            [
                new StoredTurn { Role = "user", Text = "make a ball" },
                new StoredTurn { Role = "assistant", Text = "", Calls = [new StoredCall { Id = "toolu_9", Name = "run_tool", Input = new() { ["name"] = System.Text.Json.JsonDocument.Parse("\"Ball\"").RootElement.Clone() } }] },
            ],
        };
        rig.Chat.Open(stored, _ => null);
        await rig.Send("are you there?");
        var r = api.Requests.Single();
        var result = Json.Results(r).Single();
        Assert.Equal("toolu_9", (string?)result["tool_use_id"]);
        Assert.True((bool?)result["is_error"]);
        Assert.Equal(["user", "assistant", "user"], r["messages"]!.AsArray().Select(m => (string)m!["role"]!));
    }
}
