using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AVAModeler.Chat;
using AVAModeler.Chat.Tests;
using static AVAModeler.Chat.Tests.FakeClaude;
using static AVAModeler.UI.Tests.ChatPanelTests;

namespace AVAModeler.UI.Tests;

/// <summary>The chat panel's usage line, each turn's tag, and Setup…'s Cost section.</summary>
public class ChatUsageUITests
{
    [AvaloniaFact]
    public void The_panel_shows_what_a_chat_uses_and_setup_sets_prices_and_a_budget()
    {
        var api = new FakeClaude { Tokens = (120, 2400, 9800, 60) }
            .Reply(Say("I'll make a ball."), Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Call("toolu_2", "render_view", new { width = 200, height = 150 }))
            .Reply(Say("Done: a ball at the origin."));
        var w = WithChat(api);
        w.Chat.Ledger = new UsageLedger();
        w.ToggleChat();
        Ui.Pump();
        Assert.Equal("This chat: nothing used yet.", w.Chat.UsageText.Text);
        Type(w, "make a ball");
        Finish(w.Chat.Running);
        // The line under the input, and the tag after the turn.
        Assert.StartsWith("This chat: 37.0k in (29.4k from cache) · 180 out · ≈ $", w.Chat.UsageText.Text);
        Assert.Contains(Texts(w.Chat), t => t.StartsWith("This turn: 3 requests") && t.Contains("1 picture"));
        // Setup…'s Cost section: the built-in prices, today's total, a budget and prices of one's own.
        w.Chat.SetupOpen = true;
        Ui.Pump();
        Assert.Equal("4 20 5 0.2", w.Chat.PriceBoxes[ChatModels.Opus].Text);
        Assert.StartsWith("Today: 37.0k in", w.Chat.Totals.Text);
        w.Chat.BudgetBox.Text = "2.5";
        w.Chat.BudgetBox.Focus();
        w.Chat.Input.Focus();
        Ui.Pump();
        Assert.Equal(2.5, w.Session.Preferences.ChatBudget);
        Assert.Equal(2.5, w.Chat.Session.Budget);
        var opus = w.Chat.PriceBoxes[ChatModels.Opus];
        opus.Focus();
        opus.Text = "8 40 10 0.4";
        w.Chat.Input.Focus();
        Ui.Pump();
        Assert.Equal([8.0, 40, 10, 0.4], w.Session.Preferences.ChatPrices[ChatModels.Opus]);
        Assert.Equal(8, w.Chat.Session.Price!.Input);
        // Nonsense is refused, and the box shows what's in use.
        opus.Focus();
        opus.Text = "cheap";
        w.Chat.Input.Focus();
        Ui.Pump();
        Assert.Equal("8 40 10 0.4", opus.Text);
        // Emptied, it's the built-in price again.
        opus.Focus();
        opus.Text = "";
        w.Chat.Input.Focus();
        Ui.Pump();
        Assert.Equal("4 20 5 0.2", opus.Text);
        Assert.False(w.Session.Preferences.ChatPrices.ContainsKey(ChatModels.Opus));
        // Reset totals.
        w.Chat.Ledger.Reset();
        Ui.Pump();
        Assert.StartsWith("Today: nothing", w.Chat.Totals.Text);
        if (Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } dir)
        {
            w.Chat.SetupOpen = false;
            Ui.Pump();
            var frame = w.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, "AVAModeler-chat-usage.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
