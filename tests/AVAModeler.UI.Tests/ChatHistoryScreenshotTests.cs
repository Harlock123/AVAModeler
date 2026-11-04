using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AVAModeler.Chat;
using AVAModeler.Chat.Tests;

namespace AVAModeler.UI.Tests;

/// <summary>The README's picture of the chat history list, from chats saved in a temporary folder (never the app-data folder).</summary>
public sealed class ChatHistoryScreenshotTests : IDisposable
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } d ? d : null;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "avamodeler-chat-shot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    private static StoredChat Chat(string title, ChatProvider provider, string model, DateTimeOffset when, params string[] lines)
    {
        var chat = new StoredChat
        {
            Id = ChatHistory.NewId(when), Title = title, Provider = provider, Model = model, Created = when, Updated = when.AddMinutes(12),
            Server = provider == ChatProvider.OpenAICompatible ? "localhost:11434" : null,
        };
        for (int i = 0; i < lines.Length; i++)
        {
            chat.Entries.Add(new StoredEntry { Kind = i % 2 == 0 ? "user" : "assistant", Text = lines[i] });
            chat.Turns.Add(new StoredTurn { Role = i % 2 == 0 ? "user" : "assistant", Text = lines[i] });
        }
        return chat;
    }

    [AvaloniaFact]
    public void Screenshot_the_chat_history()
    {
        var history = new ChatHistory(Path.Combine(_folder, "chats"));
        var day = new DateTimeOffset(2026, 11, 2, 9, 40, 0, TimeSpan.Zero);
        history.Save(Chat("make a chess pawn", ChatProvider.Anthropic, ChatModels.Opus, day,
            "make a chess pawn", "Here's your pawn: a lathed 24-sided solid with a round head, a collar and a wide base."));
        history.Save(Chat("build a stylized carbon atom: white protons, grey neutrons and green electrons", ChatProvider.Anthropic, ChatModels.Opus, day.AddHours(3),
            "build a stylized carbon atom: white protons, grey neutrons and green electrons", "Done: six protons, six neutrons and six electrons on three tilted rings.",
            "make it loop in 4 seconds", "It loops every 120 frames now."));
        history.Save(Chat("make a simple wooden table", ChatProvider.OpenAICompatible, "qwen3", day.AddDays(1),
            "make a simple wooden table", "Done: a 1.2 m by 0.8 m table with four legs."));
        history.Save(Chat("an arm reaching for a bouncing ball", ChatProvider.Anthropic, ChatModels.Sonnet, day.AddDays(2),
            "an arm reaching for a bouncing ball", "The forearm reaches for the ball by IK at every frame."));
        var last = Chat("a car driving round a loop", ChatProvider.Anthropic, ChatModels.Opus, day.AddDays(2).AddHours(2),
            "a car driving round a loop", "The car follows a closed path through six nulls, facing along the road.");
        history.Save(last);

        var w = ChatPanelTests.WithChat(new FakeClaude(), 1400, 860);
        var run = w.Modeler.RunMacroFile(Ui.Sample("spaceship.lua"));
        for (int i = 0; i < 2000 && !run.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(1); }
        Ui.Fit(w);
        w.Session.Preferences.ChatKeepHistory = true;
        w.Chat.LoadSettings();
        w.Chat.History = history;
        w.ToggleChat();
        Assert.True(w.Chat.RestoreLast());
        Assert.Equal(last.Id, w.Chat.Session.ChatId);
        w.Chat.ToggleHistory();
        Ui.Pump();
        Assert.True(w.Chat.HistoryOpen);
        Assert.Equal(5, history.List().Count);
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, "AVAModeler-chat-history.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        w.Close();
    }
}
