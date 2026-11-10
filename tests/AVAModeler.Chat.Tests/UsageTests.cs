using System.Text.Json.Nodes;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.Chat.Tests;

/// <summary>What the chat uses: tokens as the API reports them, their estimated cost, the turn's tag, totals, saving and the budget.</summary>
public class UsageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Each fake reply: 100 fresh, 1000 written to the cache, 5000 read from it, 20 out; at Opus 5.5's prices $0.0068.</summary>
    private static readonly (int, int, int, int) Tokens = (100, 1000, 5000, 20);
    private const double PerRequest = (100 * 4 + 20 * 20 + 1000 * 5 + 5000 * 0.2) / 1e6;

    [Fact]
    public async Task A_turns_requests_are_added_up_live_tagged_after_it_and_kept_in_the_totals()
    {
        var api = new FakeClaude { Tokens = Tokens }
            .Reply(Say("A ball."), Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Call("toolu_2", "render_view", new { view = "perspective", width = 80, height = 60 }))
            .Reply(Say("There it is."));
        await using var rig = await Rig.Start(api);
        var chat = rig.Chat;
        chat.Ledger = new UsageLedger();
        int changes = 0;
        bool sawLive = false;
        chat.UsageChanged += () => { changes++; if (chat.Working && chat.TurnTokens.Output > 0) sawLive = true; };
        await rig.Send("make a ball and show me");

        // Three requests, each counted as the API reported it, while it ran and after.
        Assert.True(sawLive);
        Assert.True(changes >= 6, $"{changes}");
        var used = chat.ChatTokens;
        Assert.Equal(new TokenUsage(300, 60, 3000, 15000, 3), used);
        Assert.Equal(3 * PerRequest, chat.ChatCost!.Value, 9);
        // The tag after the turn: its requests, the picture that went to Claude with the third, the estimate.
        var tag = Assert.IsType<UsageEntry>(chat.Entries[^1]);
        Assert.Equal(3, tag.Requests.Count);
        Assert.Equal(1, tag.Pictures);
        Assert.Equal([0, 0, 1], tag.Requests.Select(r => r.Pictures));
        Assert.Equal("This turn: 3 requests · 18.3k in (15.0k from cache) · 60 out · 1 picture · ≈ $0.020", tag.Line);
        Assert.Contains("Request 3:", tag.Details);
        Assert.Contains("1.0k written to the cache", tag.Details);
        Assert.Contains("Anthropic Console", tag.Details);
        // Today's total has it.
        var today = chat.Ledger.Day(DateOnly.FromDateTime(DateTime.Now));
        Assert.Equal(used, today.Tokens);
        Assert.Equal(3 * PerRequest, today.Cost, 9);

        // A saved chat keeps its use and its tag, and carries on from it when opened again.
        var stored = chat.ToStored(_ => null);
        Assert.Equal(used, stored.Usage);
        Assert.Equal(3 * PerRequest, stored.Cost!.Value, 9);
        Assert.Contains(stored.Entries, e => e.Kind == "usage" && e.Text == tag.Line);
        var dir = Path.Combine(Path.GetTempPath(), "AVAModeler.Chat.Tests-" + Guid.NewGuid().ToString("N"));
        StoredChat reread;
        try
        {
            var history = new ChatHistory(dir);
            history.Save(stored);
            reread = history.Load(stored.Id)!;
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        await using var again = await Rig.Start(new FakeClaude { Tokens = Tokens }.Reply(Say("You're welcome.")));
        again.Chat.Open(reread, _ => null);
        Assert.Equal(used, again.Chat.ChatTokens);
        Assert.Equal(tag.Line, again.Chat.Entries.OfType<UsageEntry>().Single().Line);
        await again.Send("thanks");
        Assert.Equal(4 * PerRequest, again.Chat.ChatCost!.Value, 9);

        // Prices of the person's own change the estimate; New Chat starts from nothing.
        chat.OwnPrices = [new ModelPrice(chat.Model, 8, 40, 10, 0.4)];
        Assert.Equal(2 * PerRequest, chat.Price!.Cost(new TokenUsage(100, 20, 1000, 5000)), 9);
        chat.NewChat();
        Assert.True(chat.ChatTokens.IsEmpty);
        Assert.Equal(0, chat.ChatCost);
    }

    [Fact]
    public async Task The_budget_stops_a_chat_between_requests_and_another_message_carries_it_on()
    {
        var api = new FakeClaude { Tokens = Tokens };
        for (int i = 0; i < 4; i++) api.Reply(Call($"toolu_{i}", "get_document", new { }));
        api.Reply(Say("Done."));
        await using var rig = await Rig.Start(api);
        // About one and a half requests' worth: the second request goes past it, so a third isn't sent.
        rig.Chat.Budget = 0.01;
        await rig.Send("look around");
        Assert.Equal(2, api.Requests.Count);
        var notice = rig.Chat.Entries.OfType<NoticeEntry>().Last();
        Assert.Contains("budget", notice.Text);
        Assert.Contains("$0.014", notice.Text);
        Assert.Contains("Send another message", notice.Text);
        Assert.IsType<UsageEntry>(rig.Chat.Entries[^1]);
        // Carrying on allows as much again: two more requests, then it stops again.
        await rig.Send("carry on");
        Assert.Equal(4, api.Requests.Count);
        Assert.Equal(2, rig.Chat.Entries.OfType<NoticeEntry>().Count(n => n.Text.Contains("budget")));
        // No budget: it goes on to the end.
        rig.Chat.Budget = null;
        await rig.Send("finish");
        Assert.Equal(0, api.Left);
        Assert.Equal("Done.", rig.Chat.Entries.OfType<AssistantEntry>().Last().Text);
    }

    [Fact]
    public async Task An_openai_compatible_server_asked_for_usage_counts_tokens_with_no_price()
    {
        var server = new FakeModelServer
        {
            Usage = new JsonObject { ["prompt_tokens"] = 500, ["completion_tokens"] = 40, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 300 } },
        };
        server.Reply(FakeModelServer.Say("Hello."));
        await using var rig = await ServerRig.Start(server);
        await rig.Send("hi");
        Assert.True((bool)rig.Server.Requests[0]["stream_options"]!["include_usage"]!);
        Assert.Equal(new TokenUsage(200, 40, 0, 300, 1), rig.Chat.ChatTokens);
        Assert.Null(rig.Chat.ChatCost);
        var tag = Assert.IsType<UsageEntry>(rig.Chat.Entries[^1]);
        Assert.EndsWith("no price", tag.Line);
        // A server that reports nothing: no tag, nothing counted.
        var quiet = new FakeModelServer().Reply(FakeModelServer.Say("Hi."));
        await using var rig2 = await ServerRig.Start(quiet);
        await rig2.Send("hi");
        Assert.DoesNotContain(rig2.Chat.Entries, e => e is UsageEntry);
        Assert.True(rig2.Chat.ChatTokens.IsEmpty);
    }

    [Fact]
    public void The_ledger_keeps_days_in_its_file_and_prices_and_counts_read_plainly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AVAModeler.Chat.Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "chat-usage.json");
            var ledger = new UsageLedger(path);
            var day1 = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
            ledger.Add(day1, new TokenUsage(1000, 100, 0, 0, 1), 0.5);
            ledger.Add(day1.AddDays(1), new TokenUsage(2000, 200, 0, 0, 1), 1.25);
            ledger.Add(day1.AddMonths(1), new TokenUsage(5, 5, 0, 0, 1), null);
            var back = new UsageLedger(path);
            Assert.Equal(1.75, back.Month(2026, 11).Cost, 9);
            Assert.Equal(new TokenUsage(3000, 300, 0, 0, 2), back.Month(2026, 11).Tokens);
            Assert.Equal(0, back.Month(2026, 12).Cost);
            Assert.Equal(10, back.Month(2026, 12).Tokens.AllInput + back.Month(2026, 12).Tokens.Output);
            Assert.DoesNotContain("make a ball", File.ReadAllText(path));
            back.Reset();
            Assert.True(new UsageLedger(path).Month(2026, 11).Tokens.IsEmpty);
            // A broken file starts afresh.
            File.WriteAllText(path, "{ not json");
            Assert.True(new UsageLedger(path).Month(2026, 11).Tokens.IsEmpty);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        Assert.Equal(("950", "12.4k", "1.20M"), (TokenUsage.Short(950), TokenUsage.Short(12_400), TokenUsage.Short(1_200_000)));
        Assert.Equal(("$0.042", "$1.37", "$128"), (ChatPricing.Dollars(0.042), ChatPricing.Dollars(1.37), ChatPricing.Dollars(128.4)));
        Assert.Equal(4, ChatPricing.Find(ChatModels.Opus)!.Input);
        Assert.Null(ChatPricing.Find("qwen3"));
        Assert.Contains("input", Assert.Throws<ArgumentException>(() => new ModelPrice("m", -1, 0, 0, 0).Checked()).Message);
    }
}
