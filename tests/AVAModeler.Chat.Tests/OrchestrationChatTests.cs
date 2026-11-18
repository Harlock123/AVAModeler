using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Tools;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.Chat.Tests;

/// <summary>
/// The chat's side of orchestration: the person asked before a big removal, told of others' edits, the timeline's
/// summaries and pictures, and reply checkpoints kept with a saved chat so Undo this reply works after reopening it.
/// </summary>
public sealed class OrchestrationChatTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-chat-orch-" + Guid.NewGuid().ToString("N"));

    public OrchestrationChatTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The live app, not busy: calls run at once on the caller's thread.</summary>
    private sealed class Person : ILiveModeler, IUiThread
    {
        public Commands Commands { get; } = new(new AVAModeler.Core.Document());
        public LayoutSession Layout { get; } = new();
        public string? Busy => null;
        public void Report(string text) { }
        public Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
    }

    [Fact]
    public async Task Before_removing_most_of_a_layer_the_chat_asks_and_no_means_nothing_changes()
    {
        var person = new Person();
        await using var bridge = await McpToolBridge.StartAsync(new LiveHost(person, person, _folder), Cancel);
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Call("toolu_2", "run_tool", new { name = "Remove" }))
            .Reply(Say("You didn't want that."));
        var chat = new ChatSession(bridge) { Client = api.Client() };
        chat.EntryAdded += e => { if (e is ConfirmEntry c) c.Choose(false); };
        await chat.SendAsync("make a ball, then clear it away", Cancel);

        var doc = person.Commands.Document;
        Assert.True(doc.Current.Mesh.Polygons.Count > 20);
        var question = Assert.Single(chat.Entries.OfType<ConfirmEntry>());
        Assert.Contains("polygons in layer 1", question.Question);
        Assert.False(question.Answer);
        var remove = chat.Entries.OfType<ToolEntry>().Last();
        Assert.True(remove.IsError);
        Assert.Contains("said no", remove.Result);
    }

    [Fact]
    public async Task Another_agents_change_is_told_to_the_model_with_the_next_message()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Box" }))
            .Reply(Say("A box."))
            .Reply(Say("I'll look again."));
        await using var rig = await Rig.Start(api);
        await rig.Send("make a box");

        // Another session on the same object (another agent, or the person) changes it.
        await using var other = await McpToolBridge.StartAsync(rig.Host, Cancel);
        await other.CallAsync("run_tool", new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Ball") }, Cancel);
        await Task.Delay(700, Cancel);

        await rig.Send("what's there now?");
        var text = (string)Json.LastBlocks(api.Requests[^1]).OfType<JsonObject>().Single(b => (string?)b["type"] == "text")["text"]!;
        Assert.StartsWith(ChatSession.ChangedElsewhereNote(true, false), text);
        Assert.EndsWith("what's there now?", text);
    }

    [Fact]
    public async Task The_timeline_lists_what_each_reply_changed_and_draws_how_it_was_before()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Box" }), Call("toolu_2", "add_light", new { name = "Key", type = "distant" }))
            .Reply(Say("A box and a light."));
        await using var rig = await Rig.Start(api);
        await rig.Send("box and light");
        var row = Assert.Single(rig.Chat.Timeline);
        Assert.Equal(1, row.Number);
        Assert.Contains("+6 polygons", row.Summary);
        Assert.Contains("Key added", row.Summary);
        var picture = await rig.Chat.PictureBeforeAsync(row, cancel: Cancel);
        Assert.NotNull(picture);
        Assert.Equal(0x89, picture!.Data[0]);
    }

    [Fact]
    public async Task With_history_kept_a_reopened_chats_undo_button_still_works()
    {
        var history = new ChatHistory(_folder);
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Box" }))
            .Reply(Say("Here's a box."));
        StoredChat stored;
        await using (var rig = await Rig.Start(api))
        {
            rig.Chat.CheckpointsFolder = history.Folder;
            await rig.Send("make a box");
            Assert.True(File.Exists(Path.Combine(_folder, rig.Chat.ChatId, "checkpoints", "reply 1", "checkpoint.json")));
            long used = 0;
            stored = rig.Chat.ToStored(i => history.KeepImage(rig.Chat.ChatId, i, ref used));
            history.Save(stored);
        }

        // Another time (a new session, as after the app restarts): the object has the box in it.
        await using var later = await Rig.Start(new FakeClaude());
        later.Chat.CheckpointsFolder = history.Folder;
        later.Chat.Open(history.Load(stored.Id)!, _ => null);
        await later.Bridge.CallAsync("run_tool", new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Box") }, Cancel);
        var row = Assert.Single(later.Chat.Timeline);
        Assert.True(row.CanUndo);
        Assert.True(await later.Chat.UndoReplyAsync(row, Cancel), row.Message);
        Assert.True(later.Host.Commands.Document.Current.Mesh.IsEmpty);
    }
}
