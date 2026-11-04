using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AVAModeler.Chat;
using AVAModeler.Chat.Tests;
using AVAModeler.Layout.UI;
using static AVAModeler.Chat.Tests.FakeClaude;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The AI Chat panel in the window, against a fake Anthropic API: Claude's tool calls run in the open document
/// through the app's own MCP server, the panel shows them, and Ctrl+Z undoes them.
/// </summary>
public class ChatPanelTests
{
    /// <summary>A window with the chat on a fake API and a made-up key for this run only.</summary>
    internal static MainWindow WithChat(FakeClaude api, int width = 1400, int height = 900)
    {
        var w = Ui.Open(width, height);
        w.ChatRoot = Path.GetTempPath();
        w.Chat.ClientFactory = _ => api.Client();
        w.Chat.Keys.Override = FakeClaude.Key;
        return w;
    }

    /// <summary>A window with the chat on a fake OpenAI-compatible server (no real server), set up in the panel as a person would.</summary>
    internal static MainWindow WithServer(FakeModelServer server, int width = 1400, int height = 900, string model = "qwen3")
    {
        var w = Ui.Open(width, height);
        w.ChatRoot = Path.GetTempPath();
        w.Chat.ServerFactory = (url, key) => new AVAModeler.Chat.OpenAICompatibleClient(new HttpClient(server), url, key);
        w.ToggleChat();
        w.Chat.SetupOpen = true;
        w.Chat.Provider.SelectedIndex = 1;
        w.Chat.BaseUrl.Text = server.BaseUrl;
        w.Chat.ServerModel.Text = model;
        Ui.Pump();
        return w;
    }

    internal static void Finish(Task task)
    {
        for (int i = 0; i < 20000 && !task.IsCompleted; i++)
        {
            Ui.Pump();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "The chat didn't finish.");
        task.GetAwaiter().GetResult();
        Ui.Pump();
    }

    /// <summary>Every bit of text the panel shows.</summary>
    internal static List<string> Texts(Control panel) =>
        panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    internal static void Type(MainWindow w, string text)
    {
        w.Chat.Input.Focus();
        w.Chat.Input.Text = text;
        Ui.Pump();
        Ui.Key(w, Avalonia.Input.Key.Enter);
    }

    [AvaloniaFact]
    public void Claude_makes_a_ball_from_the_chat_and_Ctrl_Z_undoes_it()
    {
        var api = new FakeClaude()
            .Reply(Say("I'll make a ball."), Call("toolu_1", "run_tool", new { name = "Ball" }))
            .Reply(Call("toolu_2", "render_view", new { width = 200, height = 150 }))
            .Reply(Say("Done: a ball at the origin."));
        var w = WithChat(api);
        Assert.False(w.ChatOpen);
        Ui.Key(w, Avalonia.Input.Key.F4);
        Assert.True(w.ChatOpen);
        // On the right of the Modeler, below its menus.
        Assert.Same(w.Modeler, w.Chat.Parent);
        Assert.Equal(Dock.Right, DockPanel.GetDock(w.Chat));
        Assert.Equal(1, w.Modeler.Children.IndexOf(w.Chat));

        Type(w, "make a ball");
        Assert.Equal("", w.Chat.Input.Text);
        Finish(w.Chat.Running);

        // The ball is in the open document, one undo step, made through the same live host outside agents use.
        var doc = w.Session.Document;
        Assert.True(doc.Current.Mesh.Points.Count > 20);
        Assert.Equal(1, doc.Undo.Count);
        var texts = Texts(w.Chat);
        Assert.Contains("make a ball", texts);
        Assert.Contains("I'll make a ball.", texts);
        Assert.Contains(texts, t => t.StartsWith("▸ run_tool Ball →"));
        Assert.Contains(texts, t => t.StartsWith("▸ render_view"));
        Assert.Contains("Done: a ball at the origin.", texts);
        // The picture shows as a thumbnail.
        Assert.Single(w.Chat.GetLogicalDescendants().OfType<Image>());
        Assert.Equal("Claude has finished.", w.Session.Status);

        // A tool line opens to its input and result.
        var line = w.Chat.GetLogicalDescendants().OfType<Button>().First(b => b.Content is TextBlock t && (t.Text ?? "").Contains("run_tool Ball"));
        line.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        texts = Texts(w.Chat);
        Assert.Contains(texts, t => t.StartsWith("▾ run_tool Ball"));
        Assert.Contains(texts, t => t.Contains("\"name\": \"Ball\""));

        // The person's Ctrl+Z undoes Claude's step.
        w.Modeler.Focus();
        Ui.Key(w, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Assert.Empty(doc.Current.Mesh.Points);

        // The panel follows to Layout, and F4 hides it.
        Ui.Key(w, Avalonia.Input.Key.F2);
        Assert.IsType<LayoutWorkspace>(w.Content);
        Assert.Same(w.Content, w.Chat.Parent);
        Ui.Key(w, Avalonia.Input.Key.F4);
        Assert.Null(w.Chat.Parent);
        Ui.Key(w, Avalonia.Input.Key.F1);
        Assert.Null(w.Chat.Parent);
    }

    [AvaloniaFact]
    public void Auto_continue_is_on_by_default_and_remembered()
    {
        var w = Ui.Open();
        w.Modeler.Execute("AI Chat");
        Assert.True(w.Chat.AutoContinue.IsChecked);
        Assert.Equal(ChatPanel.AutoContinueTimes, w.Chat.Session.AutoContinues);
        w.Chat.AutoContinue.IsChecked = false;
        Assert.Equal(0, w.Chat.Session.AutoContinues);
        Assert.False(w.Session.Preferences.ChatAutoContinue);
        Assert.True(w.Chat.AutoContinue.IsVisible);
    }

    [AvaloniaFact]
    public void Without_a_key_the_panel_says_how_to_set_one()
    {
        var api = new FakeClaude();
        var w = Ui.Open();
        w.Chat.ClientFactory = _ => api.Client();
        w.Modeler.Execute("AI Chat");
        Assert.True(w.ChatOpen);
        Assert.Contains("ANTHROPIC_API_KEY", w.Chat.IntroText);
        Assert.Contains("Key…", w.Chat.IntroText);
        Type(w, "hello?");
        Finish(w.Chat.Running);
        Assert.Contains(Texts(w.Chat), t => t.StartsWith("There's no Anthropic API key yet.") && t.Contains("ANTHROPIC_API_KEY"));
        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public void An_API_error_shows_a_line_with_Retry()
    {
        var api = new FakeClaude().Error(429, "rate_limit_error", "Slow down.").Reply(Say("Hello again."));
        var w = WithChat(api);
        w.ToggleChat();
        Type(w, "hello");
        Finish(w.Chat.Running);
        Assert.Contains(Texts(w.Chat), t => t.Contains("429"));
        var retry = w.Chat.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Retry");
        retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Finish(w.Chat.Running);
        Assert.Contains("Hello again.", Texts(w.Chat));
        Assert.False(retry.IsVisible);
    }

    [AvaloniaFact]
    public void Stop_works_while_a_tool_call_waits_for_the_person()
    {
        var api = new FakeClaude().Reply(Call("toolu_1", "run_tool", new { name = "Box" }));
        var w = WithChat(api);
        w.ToggleChat();
        // The person has a tool armed: Claude's call waits rather than change things under them.
        w.Modeler.PickTool("Move");
        Type(w, "a box");
        for (int i = 0; i < 300 && w.Chat.Session.Entries.OfType<ToolEntry>().FirstOrDefault() is not { Running: true }; i++) { Ui.Pump(); Thread.Sleep(2); }
        Assert.True(w.Chat.Session.Working);
        var send = w.Chat.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Stop");
        send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Finish(w.Chat.Running);
        Assert.False(w.Chat.Session.Working);
        Assert.Equal("Send", send.Content);
        Assert.Empty(w.Session.Document.Current.Mesh.Points);
        Assert.Contains("Stopped.", Texts(w.Chat));
        // The stopped call is given up for good: once the person is done, it doesn't run after all.
        w.Session.CancelTool();
        for (int i = 0; i < 100; i++) { Ui.Pump(); Thread.Sleep(5); }
        Assert.Empty(w.Session.Document.Current.Mesh.Points);
    }

    [AvaloniaFact]
    public void The_panel_switches_to_a_local_model_and_builds_with_it()
    {
        var server = new FakeModelServer { ModelsJson = """{"object":"list","data":[{"id":"qwen3:latest"},{"id":"llama3.1:8b"}]}""" }
            .Reply(FakeModelServer.Say("I'll make a box."), FakeModelServer.Call("call_1", "run_tool", new { name = "Box" }), FakeModelServer.Call("call_2", "check_mesh", new { }))
            .Reply(FakeModelServer.Say("A closed box."));
        var w = Ui.Open();
        w.ChatRoot = Path.GetTempPath();
        w.Chat.ServerFactory = (url, key) => new AVAModeler.Chat.OpenAICompatibleClient(new HttpClient(server), url, key);
        w.ToggleChat();
        // Claude to start with; Setup… shows the provider, and the server's settings once it's chosen.
        Assert.Equal("Using Claude Opus 5.5 (Anthropic API)", w.Chat.UsingText);
        Assert.False(w.Chat.SetupOpen);
        var setup = w.Chat.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Setup…");
        setup.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        Assert.True(w.Chat.SetupOpen);
        Assert.True(w.Chat.Provider.IsEffectivelyVisible);
        Assert.False(w.Chat.BaseUrl.IsEffectivelyVisible);
        w.Chat.Provider.SelectedIndex = 1;
        Ui.Pump();
        Assert.True(w.Chat.BaseUrl.IsEffectivelyVisible);
        Assert.Equal("http://localhost:11434/v1", w.Chat.BaseUrl.Text);
        Assert.True(w.Chat.CoreTools.IsChecked);
        Assert.False(w.Chat.SeesPictures.IsChecked);
        Assert.Equal(AVAModeler.Chat.ChatProvider.OpenAICompatible, w.Chat.Session.Provider);
        Assert.Contains("no model chosen yet", w.Chat.UsingText);
        Assert.Contains("No model is chosen yet", w.Chat.IntroText);

        // Refresh lists the server's models; choosing one fills in the model box.
        var refresh = w.Chat.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Refresh");
        refresh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Finish(w.Chat.Refreshing);
        Assert.Equal(1, server.ModelRequests);
        Assert.True(w.Chat.FoundModels.IsVisible);
        Assert.Equal(["llama3.1:8b", "qwen3:latest"], w.Chat.FoundModels.Items.Cast<string>());
        w.Chat.FoundModels.SelectedIndex = 1;
        Ui.Pump();
        Assert.Equal("qwen3:latest", w.Chat.ServerModel.Text);
        Assert.Equal("Using qwen3:latest on localhost:11434 (OpenAI-compatible, core tools)", w.Chat.UsingText);
        Assert.Contains("nothing leaves it", w.Chat.IntroText);
        // Kept in the preferences (saved with them when the app closes).
        var prefs = w.Session.Preferences;
        Assert.Equal("OpenAICompatible", prefs.ChatProvider);
        Assert.Equal("qwen3:latest", prefs.ChatServerModel);

        // The model builds in the open window, through the same tools.
        Type(w, "make a box and check it");
        Finish(w.Chat.Running);
        Assert.Equal(8, w.Session.Document.Current.Mesh.Points.Count);
        var texts = Texts(w.Chat);
        Assert.Contains("I'll make a box.", texts);
        Assert.Contains(texts, t => t.StartsWith("▸ run_tool Box →"));
        Assert.Contains(texts, t => t.StartsWith("▸ check_mesh"));
        Assert.Contains("A closed box.", texts);
        Assert.Equal("The model has finished.", w.Session.Status);
        Assert.Equal("qwen3:latest", (string?)server.Requests[0]["model"]);
        Assert.Equal(AVAModeler.Chat.ChatTools.Core.Count, server.Requests[0]["tools"]!.AsArray().Count);
        Assert.All(server.Authorization, a => Assert.Null(a));

        // Back to Claude: a new chat (a conversation can't move between providers), and the Claude model box again.
        w.Chat.Provider.SelectedIndex = 0;
        Ui.Pump();
        Assert.Equal(AVAModeler.Chat.ChatProvider.Anthropic, w.Chat.Session.Provider);
        Assert.Empty(w.Chat.Session.Entries);
        Assert.DoesNotContain("A closed box.", Texts(w.Chat));
        Assert.StartsWith("Using Claude", w.Chat.UsingText);
        Assert.Equal("Anthropic", prefs.ChatProvider);
    }

    [AvaloniaFact]
    public void Edit_Preferences_settings_reach_the_panel()
    {
        var w = Ui.Open();
        var prefs = w.Session.Preferences;
        prefs.ChatProvider = "OpenAICompatible";
        prefs.ChatServerUrl = "http://localhost:1234/v1";
        prefs.ChatServerModel = "mistral-small";
        prefs.ChatModelSeesPictures = true;
        w.Chat.LoadSettings();
        Assert.Equal(1, w.Chat.Provider.SelectedIndex);
        Assert.Equal("http://localhost:1234/v1", w.Chat.BaseUrl.Text);
        Assert.True(w.Chat.SeesPictures.IsChecked);
        Assert.True(w.Chat.Session.ModelSeesPictures);
        Assert.Equal("mistral-small", w.Chat.Session.ServerModel);
        Assert.Equal("Using mistral-small on localhost:1234 (OpenAI-compatible, core tools)", w.Chat.UsingText);
    }

    [AvaloniaFact]
    public void A_local_server_that_isnt_running_shows_a_line_with_Retry()
    {
        var server = new FakeModelServer().Refuse().Reply(FakeModelServer.Say("Here now."));
        var w = WithServer(server);
        Type(w, "hello");
        Finish(w.Chat.Running);
        Assert.Contains(Texts(w.Chat), t => t.StartsWith("Couldn't reach the model server at localhost:11434"));
        var retry = w.Chat.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Retry");
        retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Finish(w.Chat.Running);
        Assert.Contains("Here now.", Texts(w.Chat));
    }
}
