using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Chat.UI;
using AVAModeler.Chat;
using AVAModeler.Chat.Tests;
using AVAModeler.Modeler.UI;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The chat panel's history, in a temporary folder (never the app-data folder) and against fakes only: Keep chat
/// history, saving after each request, History's list, opening, exporting as Markdown, deleting and clearing, reopening
/// the last chat in a new window, and a chat with the other provider shown read-only.
/// </summary>
public sealed class ChatHistoryUITests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-chat-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private string Chats => Path.Combine(_folder, "chats");

    private MainWindow Window(FakeClaude api, bool keep)
    {
        var w = ChatPanelTests.WithChat(api);
        w.Session.Preferences.ChatKeepHistory = keep;
        w.Chat.LoadSettings();
        w.Chat.History = new ChatHistory(Chats);
        w.ToggleChat();
        Ui.Pump();
        return w;
    }

    private static void Press(Control root, string text) =>
        root.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as TextBlock)?.Text == text || b.Content as string == text)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Chats_are_kept_only_with_Keep_chat_history_ticked_and_History_reopens_exports_and_deletes_them()
    {
        var api = new FakeClaude()
            .Reply(Say("I'll make a ball."), Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Say("A ball at the origin."))
            .Reply(Say("Here's a box."), Call("toolu_2", "run_tool", new { name = "Box" }))
            .Reply(Say("Done."));
        var w = Window(api, keep: false);
        ChatPanelTests.Type(w, "make a ball");
        ChatPanelTests.Finish(w.Chat.Running);
        // Off by default: nothing is written.
        Assert.False(Directory.Exists(Chats));
        Assert.False(new Preferences().ChatKeepHistory);
        // Ticking it keeps the chat on show at once, and each request after.
        Press(w.Chat, "History");
        Ui.Pump();
        Assert.True(w.Chat.HistoryOpen);
        w.Chat.KeepHistory.IsChecked = true;
        Assert.True(w.Session.Preferences.ChatKeepHistory);
        var first = Assert.Single(w.Chat.History!.List());
        Assert.Equal("make a ball", first.Title);
        Assert.Contains(ChatPanelTests.Texts(w.Chat), t => t == "● make a ball");
        Press(w.Chat, "New Chat");
        Ui.Pump();
        Assert.False(w.Chat.HistoryOpen);
        ChatPanelTests.Type(w, "now a box");
        ChatPanelTests.Finish(w.Chat.Running);
        Assert.Equal(["now a box", "make a ball"], w.Chat.History.List().Select(c => c.Title));

        // Open the first again from the list: its lines come back.
        Press(w.Chat, "History");
        Ui.Pump();
        Press(w.Chat, "make a ball");
        Ui.Pump();
        Assert.False(w.Chat.HistoryOpen);
        Assert.Equal(first.Id, w.Chat.Session.ChatId);
        Assert.Equal("make a ball", Assert.IsType<UserEntry>(w.Chat.Session.Entries[0]).Text);
        Assert.Contains(ChatPanelTests.Texts(w.Chat), t => t == "A ball at the origin.");

        // Export it as Markdown, to a file in the temporary folder.
        var md = Path.Combine(_folder, "ball.md");
        string? asked = null;
        w.Chat.PickExportPath = name => { asked = name; return Task.FromResult<string?>(md); };
        var exported = w.Chat.ExportChat();
        ChatPanelTests.Finish(exported);
        Assert.Equal("make a ball.md", asked);
        Assert.Contains("**You:** make a ball", File.ReadAllText(md));

        // Delete the one on show (a new chat starts, so it isn't saved again), then clear the rest.
        w.Chat.DeleteChat(first.Id);
        Assert.Empty(w.Chat.Session.Entries);
        Assert.Equal(["now a box"], w.Chat.History.List().Select(c => c.Title));
        w.Chat.ClearHistory();
        Assert.Empty(w.Chat.History.List());
        w.Close();
    }

    [AvaloniaFact]
    public void A_new_window_reopens_the_last_chat_and_shows_another_providers_chat_read_only()
    {
        var api = new FakeClaude().Reply(Say("Hello, what shall we make?")).Reply(Say("Still here."));
        var w = Window(api, keep: true);
        ChatPanelTests.Type(w, "hello");
        ChatPanelTests.Finish(w.Chat.Running);
        w.Close();
        // No file holds the key.
        foreach (var f in Directory.GetFiles(Chats, "*", SearchOption.AllDirectories)) Assert.DoesNotContain(Key, File.ReadAllText(f));

        // The app again: the last chat is back, and carries on with Claude.
        var again = Window(api, keep: true);
        Assert.True(again.Chat.RestoreLast());
        Assert.Equal("hello", Assert.IsType<UserEntry>(again.Chat.Session.Entries[0]).Text);
        Assert.False(again.Chat.Session.ReadOnly);

        // Switch to an OpenAI-compatible server: the saved Claude chat, opened now, is read-only.
        again.Chat.SetupOpen = true;
        again.Chat.Provider.SelectedIndex = 1;
        again.Chat.ServerModel.Text = "qwen3";
        Ui.Pump();
        Assert.Empty(again.Chat.Session.Entries);
        Assert.True(again.Chat.OpenChat(again.Chat.History!.List().Single().Id));
        Assert.True(again.Chat.Session.ReadOnly);
        Assert.Contains("was with Claude", again.Chat.ReadOnlyText);
        Assert.False(again.Chat.Input.IsEnabled);
        // Claude again in Setup: it can carry on.
        again.Chat.Provider.SelectedIndex = 0;
        Ui.Pump();
        Assert.False(again.Chat.Session.ReadOnly);
        Assert.Equal("", again.Chat.ReadOnlyText);
        Assert.True(again.Chat.Input.IsEnabled);
        ChatPanelTests.Type(again, "are you there?");
        ChatPanelTests.Finish(again.Chat.Running);
        Assert.Equal("Still here.", again.Chat.Session.Entries.OfType<AssistantEntry>().Last().Text);
        Assert.Equal(0, api.Left);
        again.Close();
    }
}
