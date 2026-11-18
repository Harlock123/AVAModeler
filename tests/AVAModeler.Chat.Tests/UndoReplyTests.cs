using System.Text.Json.Nodes;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.Chat.Tests;

/// <summary>Undo this reply: the checkpoint the chat takes before a reply's first change, and going back to it.</summary>
public class UndoReplyTests
{
    [Fact]
    public async Task A_reply_that_changes_the_object_can_be_undone_and_the_model_is_told()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Box" }))
            .Reply(Say("Here's a box."))
            .Reply(Call("toolu_2", "run_tool", new { name = "Ball", @params = new { Center = new[] { 3, 0, 0 } } }), Call("toolu_3", "add_light", new { name = "Key", type = "distant" }))
            .Reply(Say("Added a ball and a light."))
            .Reply(Say("OK."));
        await using var rig = await Rig.Start(api);
        var doc = rig.Host.Commands.Document;
        int items = rig.Host.Layout.Scene.Items.Count;

        await rig.Send("make a box");
        int boxPolygons = doc.Current.Mesh.Polygons.Count;
        await rig.Send("add a ball and a key light");
        Assert.True(doc.Current.Mesh.Polygons.Count > boxPolygons);
        Assert.Equal(items + 1, rig.Host.Layout.Scene.Items.Count);

        var buttons = rig.Chat.Entries.OfType<ReplyUndoEntry>().ToList();
        Assert.Equal(2, buttons.Count);
        // Each comes after its reply's last words, before the usage tag.
        Assert.IsType<AssistantEntry>(rig.Chat.Entries[rig.Chat.Entries.ToList().IndexOf(buttons[1]) - 1]);

        Assert.True(await rig.Chat.UndoReplyAsync(buttons[1], TestContext.Current.CancellationToken));
        Assert.Equal(ReplyUndoState.Undone, buttons[1].State);
        Assert.Equal(boxPolygons, doc.Current.Mesh.Polygons.Count);
        Assert.Equal(items, rig.Host.Layout.Scene.Items.Count);
        // One undo step on the object (and on the scene), so Ctrl+Z brings the reply back.
        Assert.Equal("Restore checkpoint reply 2", doc.Undo.UndoLabel);
        Assert.False(buttons[1].CanUndo);

        // The next message carries the note; what the person sees is only what they typed.
        await rig.Send("thanks");
        var last = Json.LastBlocks(api.Requests[^1]).OfType<JsonObject>().Single(b => (string?)b["type"] == "text");
        Assert.StartsWith(ChatSession.UndoNote, (string)last["text"]!);
        Assert.EndsWith("thanks", (string)last["text"]!);
        Assert.Equal("thanks", rig.Chat.Entries.OfType<UserEntry>().Last().Text);
    }

    [Fact]
    public async Task Undoing_an_earlier_reply_takes_later_ones_with_it()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "run_tool", new { name = "Box" }))
            .Reply(Say("Box."))
            .Reply(Call("toolu_2", "run_tool", new { name = "Ball", @params = new { Center = new[] { 3, 0, 0 } } }))
            .Reply(Say("Ball."));
        await using var rig = await Rig.Start(api);
        await rig.Send("box");
        await rig.Send("ball");
        var buttons = rig.Chat.Entries.OfType<ReplyUndoEntry>().ToList();

        Assert.True(await rig.Chat.UndoReplyAsync(buttons[0], TestContext.Current.CancellationToken));
        Assert.True(rig.Host.Commands.Document.Current.Mesh.IsEmpty);
        Assert.Equal(ReplyUndoState.Superseded, buttons[1].State);
        Assert.False(await rig.Chat.UndoReplyAsync(buttons[1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_reply_that_only_looks_or_fails_has_no_undo_button()
    {
        var api = new FakeClaude()
            .Reply(Call("toolu_1", "get_document", new { }))
            .Reply(Say("It's empty."))
            .Reply(Call("toolu_2", "run_tool", new { name = "No such tool" }))
            .Reply(Say("That didn't work."));
        await using var rig = await Rig.Start(api);
        await rig.Send("what's there?");
        await rig.Send("make something odd");
        Assert.Empty(rig.Chat.Entries.OfType<ReplyUndoEntry>());
        Assert.Empty(rig.Host.Commands.Document.Undo.UndoLabels);
    }
}
