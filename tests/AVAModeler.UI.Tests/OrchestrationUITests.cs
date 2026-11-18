using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Chat;
using AVAModeler.Chat.Tests;
using AVAModeler.Core;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The person's side of working with agents in the window: locking a selection (and a Layout item) from them, the
/// chat asking before a big removal, and the chat's timeline. Set AVAMODELER_SHOTS to save the README's pictures.
/// </summary>
public class OrchestrationUITests
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } d ? d : null;

    private static void Press(Control root, string text) =>
        root.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as TextBlock)?.Text == text || b.Content as string == text)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static void Shot(MainWindow w, string name)
    {
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public void The_person_locks_a_selection_and_an_item_from_agents_and_unlocks_them()
    {
        var w = Ui.Open(1200, 800);
        var c = w.Modeler.Session.Commands;
        c.Run("Box");
        c.SelectMode(AVAModeler.Core.SelectionMode.Polygons);
        c.SelectPolygons([0, 1]);
        w.Modeler.LockForAgents();
        var made = Assert.Single(c.Document.AgentLocks);
        Assert.Equal(AgentLock.Person, made.Owner);
        Assert.Equal(2, made.Polygons.Count);
        c.SelectNone();
        w.Modeler.LockForAgents();
        Assert.Single(c.Document.AgentLocks);
        Assert.Contains("Select what to keep", w.Modeler.Session.Status);
        w.Modeler.UnlockForAgents();
        Assert.Empty(c.Document.AgentLocks);

        w.ShowLayout();
        var workspace = Assert.IsType<AVAModeler.Layout.UI.LayoutWorkspace>(w.Content);
        var layout = w.LayoutSession;
        layout.Select(layout.Scene.Items.First());
        workspace.LockItemForAgents();
        Assert.Equal(AgentLock.Person, Assert.Single(layout.AgentLocks).Owner);
        workspace.UnlockItemsForAgents();
        Assert.Empty(layout.AgentLocks);
        w.Close();
    }

    [AvaloniaFact]
    public void The_chat_asks_before_Claude_clears_a_layer_and_goes_ahead_when_told_to()
    {
        var api = new FakeClaude()
            .Reply(Say("I'll make a ball."), Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Say("Now clearing it away."), Call("toolu_2", "run_tool", new { name = "Remove" }))
            .Reply(Say("Cleared."));
        var w = ChatPanelTests.WithChat(api, 1400, 860);
        w.ToggleChat();
        Ui.Pump();
        ChatPanelTests.Type(w, "make a ball, then clear the layer");
        ConfirmEntry? question = null;
        for (int i = 0; i < 20000 && question == null; i++)
        {
            Ui.Pump();
            Thread.Sleep(1);
            question = w.Chat.Session.Entries.OfType<ConfirmEntry>().FirstOrDefault();
        }
        Assert.NotNull(question);
        Ui.Pump();
        Assert.Contains("polygons in layer 1", question!.Question);
        Assert.Contains(question.Question, ChatPanelTests.Texts(w.Chat));
        Shot(w, "AVAModeler-chat-confirm.png");

        Press(w.Chat, "Go Ahead");
        ChatPanelTests.Finish(w.Chat.Running);
        Assert.True(question.Answer);
        Assert.True(w.Modeler.Session.Commands.Document.Current.Mesh.IsEmpty);
        Assert.Contains("You let it go ahead.", ChatPanelTests.Texts(w.Chat));
        w.Close();
    }

    [AvaloniaFact]
    public void The_timeline_shows_each_reply_and_goes_back_to_before_one()
    {
        var api = new FakeClaude()
            .Reply(Say("A box."), Call("toolu_1", "run_tool", new { name = "Box", @params = new { Size = new[] { 1, 0.6, 1 } } }))
            .Reply(Say("Here's a box."))
            .Reply(Say("A ball on top."), Call("toolu_2", "run_tool", new { name = "Ball", @params = new { Center = new[] { 0, 0.8, 0 }, Radius = new[] { 0.35, 0.35, 0.35 } } }),
                Call("toolu_3", "set_surface", new { name = "Red", color = new[] { 220, 60, 50 } }))
            .Reply(Say("A red ball on the box."))
            .Reply(Say("A light."), Call("toolu_4", "add_light", new { name = "Key", type = "distant" }))
            .Reply(Say("Lit."));
        var w = ChatPanelTests.WithChat(api, 1400, 860);
        w.ToggleChat();
        Ui.Pump();
        foreach (var ask in new[] { "make a box", "put a red ball on it", "add a key light" })
        {
            ChatPanelTests.Type(w, ask);
            ChatPanelTests.Finish(w.Chat.Running);
        }
        Assert.Equal(3, w.Chat.Session.Timeline.Count);
        w.Chat.ToggleTimeline();
        for (int i = 0; i < 4000 && w.Chat.Session.Timeline.Any(r => r.Before == null); i++)
        {
            Ui.Pump();
            Thread.Sleep(1);
        }
        Ui.Pump();
        Assert.True(w.Chat.TimelineOpen);
        var texts = ChatPanelTests.Texts(w.Chat);
        Assert.Contains("Before reply 2", texts);
        Assert.Contains(texts, t => t.Contains("Key added"));
        Shot(w, "AVAModeler-chat-timeline.png");

        // Going back to before the second reply takes the ball and the light away.
        var second = w.Chat.Session.Timeline[1];
        ChatPanelTests.Finish(w.Chat.Session.UndoReplyAsync(second));
        Assert.Equal(ReplyUndoState.Undone, second.State);
        Assert.Equal(ReplyUndoState.Superseded, w.Chat.Session.Timeline[2].State);
        Assert.Equal(6, w.Modeler.Session.Commands.Document.Current.Mesh.Polygons.Count);
        Assert.DoesNotContain(w.LayoutSession.Scene.Items, i => i.Name == "Key");
        w.Close();
    }
}
