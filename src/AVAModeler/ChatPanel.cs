using Anthropic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AVAModeler.Chat;
using AVAModeler.Modeler.UI;

namespace AVAModeler;

/// <summary>
/// The AI Chat panel on the right of the window (F4, or Macros › AI Chat): the conversation with the model, each tool
/// call as a compact line that opens to its input and result, small pictures of what the model rendered, and below them
/// the input box (Enter sends, Shift+Enter starts a new line), Send (Stop while the model works) and the Claude model.
/// A line under the title says which provider and model it's using; Setup… chooses between Claude (the Anthropic API)
/// and an OpenAI-compatible server (a local model through Ollama, LM Studio, vLLM or llama.cpp, or a gateway), kept in
/// Preferences. The conversation itself is <see cref="ChatSession"/>'s; this is only its controls.
/// </summary>
internal sealed class ChatPanel : DockPanel
{
    private readonly ApiKeyStore _keys;
    private readonly ApiKeyStore _serverKeys;
    private readonly Preferences _prefs;
    private readonly Action<string> _status;
    private readonly TextBlock _using = new() { FontSize = 11, Foreground = Look.Text, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 3) };
    private readonly StackPanel _setup = new() { Spacing = 3, Margin = new Thickness(6, 2, 6, 6), IsVisible = false };
    private readonly StackPanel _serverSettings = new() { Spacing = 3 };
    private readonly Button _refresh;
    private bool _loading;
    private (string Url, string? Key, OpenAICompatibleClient Client)? _server;

    /// <summary>The shared HTTP client for OpenAI-compatible servers: a long timeout, since a local model may take a while to load.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly StackPanel _messages = new() { Spacing = 6, Margin = new Thickness(6) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 12, Opacity = 0.85 };
    private readonly ComboBox _model;
    private readonly Button _send;

    /// <summary>How many times a request carries on by itself, with Auto-continue ticked: 600 tool calls in all.</summary>
    public const int AutoContinueTimes = 9;

    /// <summary>Auto-continue: carry on after the tool-call limit without waiting for "continue".</summary>
    public CheckBox AutoContinue { get; } = new() { Content = "Auto-continue", FontSize = 12, Foreground = Look.Text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private (string Key, IAnthropicClient Client)? _client;

    public ChatSession Session { get; }

    /// <summary>Where the key comes from (tests set one for the run only).</summary>
    public ApiKeyStore Keys => _keys;

    /// <summary>Where an OpenAI-compatible server's key (for a gateway) comes from.</summary>
    public ApiKeyStore ServerKeys => _serverKeys;

    /// <summary>The provider: Claude (the Anthropic API) or an OpenAI-compatible server.</summary>
    public ComboBox Provider { get; } = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>The OpenAI-compatible server's base URL.</summary>
    public TextBox BaseUrl { get; } = new() { FontSize = 12, PlaceholderText = OpenAICompatibleClient.DefaultBaseUrl };

    /// <summary>The model's name on the OpenAI-compatible server.</summary>
    public TextBox ServerModel { get; } = new() { FontSize = 12, PlaceholderText = "qwen3, llama3.1, mistral-small…" };

    /// <summary>The models Refresh found on the server; choosing one fills in <see cref="ServerModel"/>.</summary>
    public ComboBox FoundModels { get; } = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false, PlaceholderText = "Models on the server…" };

    /// <summary>Offer the OpenAI-compatible model only the core tools.</summary>
    public CheckBox CoreTools { get; } = new() { Content = "Core tools only", FontSize = 12, Foreground = Look.Text };

    /// <summary>Send the OpenAI-compatible model its pictures (a vision model).</summary>
    public CheckBox SeesPictures { get; } = new() { Content = "Model sees pictures", FontSize = 12, Foreground = Look.Text, Margin = new Thickness(10, 0, 0, 0) };

    /// <summary>Whether the settings (Setup…) are showing.</summary>
    public bool SetupOpen { get => _setup.IsVisible; set => _setup.IsVisible = value; }

    /// <summary>The line saying which provider and model the chat is using.</summary>
    public string UsingText => _using.Text ?? "";

    /// <summary>Makes an OpenAI-compatible server's client for a base URL and key (tests give one on a fake server).</summary>
    public Func<string, string?, OpenAICompatibleClient> ServerFactory { get; set; } = (url, key) => new OpenAICompatibleClient(Http, url, key);

    /// <summary>The model list being fetched, if any (tests wait for it).</summary>
    public Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>The input box.</summary>
    public TextBox Input { get; } = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 160, FontSize = 13,
        PlaceholderText = "Ask Claude to model or stage something… (Enter sends)",
    };

    /// <summary>Makes the API client for a key (tests give one on a fake API).</summary>
    public Func<string, IAnthropicClient> ClientFactory { get; set; } = key => new AnthropicClient { ApiKey = key };

    /// <summary>The request under way, if any (tests wait for it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Raised by the panel's close button.</summary>
    public event Action? CloseRequested;

    public ChatPanel(IToolBridge tools, ApiKeyStore keys, Action<string> status, Preferences? prefs = null, ApiKeyStore? serverKeys = null)
    {
        _keys = keys;
        _serverKeys = serverKeys ?? new ApiKeyStore(null, null);
        _prefs = prefs ?? new Preferences();
        _status = status;
        Session = new ChatSession(tools);
        Width = 380;
        Background = Look.Panel;

        var title = new TextBlock { Text = "AI Chat", FontWeight = FontWeight.Bold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Look.MakeButton("New Chat", NewChat, "Forget this conversation and start again"));
        buttons.Children.Add(Look.MakeButton("Setup…", () => SetupOpen = !SetupOpen, "Choose the model: Claude, or a local or self-hosted server"));
        buttons.Children.Add(Look.MakeButton("Key…", () => _ = EditKey(), "Your Anthropic API key, or an OpenAI-compatible gateway's"));
        buttons.Children.Add(Look.MakeButton("×", () => CloseRequested?.Invoke(), "Hide the chat (F4)"));
        var header = new DockPanel { Background = Look.PanelDark, Height = 30 };
        SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);
        var usingBar = new Border { Background = Look.PanelDark, Child = _using, BorderBrush = Look.Panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        SetDock(usingBar, Dock.Top);
        Children.Add(usingBar);

        // Setup…: the provider, and for an OpenAI-compatible server its base URL, model and options.
        Provider.ItemsSource = PreferencesWindow.ChatProviders.Select(c => c.Label).ToList();
        _refresh = Look.MakeButton("Refresh", () => Refreshing = RefreshModels(), "List the models the server offers (GET …/models)", 70);
        var modelRow = new DockPanel();
        SetDock(_refresh, Dock.Right);
        modelRow.Children.Add(_refresh);
        modelRow.Children.Add(ServerModel);
        ToolTip.SetTip(BaseUrl, "The server's OpenAI-compatible API: Ollama http://localhost:11434/v1, LM Studio http://localhost:1234/v1, llama.cpp http://localhost:8080/v1, or a gateway's URL. Only this address is ever contacted.");
        ToolTip.SetTip(CoreTools, $"Offer the model {ChatTools.Core.Count} core tools instead of all of them: small local models choose better among fewer. Untick for a large model.");
        ToolTip.SetTip(SeesPictures, "Send the model the pictures it renders. Tick only for a vision model (qwen2.5vl, gemma3, llama3.2-vision…); otherwise it's told a picture was made.");
        _serverSettings.Children.Add(SettingLabel("Base URL"));
        _serverSettings.Children.Add(BaseUrl);
        _serverSettings.Children.Add(SettingLabel("Model"));
        _serverSettings.Children.Add(modelRow);
        _serverSettings.Children.Add(FoundModels);
        _serverSettings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { CoreTools, SeesPictures } });
        _setup.Children.Add(SettingLabel("Provider"));
        _setup.Children.Add(Provider);
        _setup.Children.Add(_serverSettings);
        var setupBar = new Border { Background = Look.Panel, Child = _setup };
        SetDock(setupBar, Dock.Top);
        Children.Add(setupBar);

        _model = new ComboBox { ItemsSource = ChatModels.All.Select(m => m.Label).ToList(), SelectedIndex = 0, Width = 170, FontSize = 12 };
        _model.SelectionChanged += (_, _) =>
        {
            if (_model.SelectedIndex >= 0) Session.Model = ChatModels.All[_model.SelectedIndex].Id;
            if (!_loading && Provider.SelectedIndex >= 0) _using.Text = UsingLine();
        };
        ToolTip.SetTip(_model, "The Claude model. Opus is the most capable; Sonnet and Haiku are quicker and cost less.");
        _send = Look.MakeButton("Send", SendOrStop, "Send (Enter); Stop while Claude is working", 80);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        SetDock(_send, Dock.Right);
        row.Children.Add(_send);
        SetDock(_model, Dock.Left);
        row.Children.Add(_model);
        row.Children.Add(AutoContinue);
        var bottom = new StackPanel { Margin = new Thickness(6), Children = { Input, row } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        _scroll = new ScrollViewer { Content = _messages, Background = Look.PanelDark, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Children.Add(new Border { BorderBrush = Look.PanelDark, BorderThickness = new Thickness(1, 0, 0, 0), Child = _scroll });
        _messages.Children.Add(_intro);

        // Enter sends; Shift+Enter is a new line (the box handles that itself).
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                if (!Session.Working) Send();
            }
        }, RoutingStrategies.Tunnel);

        Session.EntryAdded += entry => { _messages.Children.Add(View(entry)); ScrollToEnd(); };
        Session.Cleared += () => { _messages.Children.Clear(); _messages.Children.Add(_intro); };
        Session.WorkingChanged += () =>
        {
            _send.Content = Session.Working ? "Stop" : "Send";
            _status(Session.Working ? $"{Session.Who} is working…" : $"{Session.Who} has finished.");
        };
        AutoContinue.IsCheckedChanged += (_, _) =>
        {
            bool on = AutoContinue.IsChecked == true;
            Session.AutoContinues = on ? AutoContinueTimes : 0;
            if (prefs != null) prefs.ChatAutoContinue = on;
        };
        AutoContinue.IsChecked = prefs?.ChatAutoContinue ?? true;
        Session.AutoContinues = AutoContinue.IsChecked == true ? AutoContinueTimes : 0;
        ToolTip.SetTip(AutoContinue, $"When the model has made {Session.MaxToolCalls} tool calls for one request, carry on by itself instead of waiting for you to say \"continue\" (up to {Session.MaxToolCalls * (AutoContinueTimes + 1)} calls). Stop ends it at any time.");

        Provider.SelectionChanged += (_, _) => SettingsChanged();
        BaseUrl.TextChanged += (_, _) => SettingsChanged();
        ServerModel.TextChanged += (_, _) => SettingsChanged();
        CoreTools.IsCheckedChanged += (_, _) => SettingsChanged();
        SeesPictures.IsCheckedChanged += (_, _) => SettingsChanged();
        FoundModels.SelectionChanged += (_, _) => { if (FoundModels.SelectedItem is string m) ServerModel.Text = m; };
        LoadSettings();
    }

    private static TextBlock SettingLabel(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 3, 0, 0) };

    /// <summary>Takes up the chat settings from Preferences (at the start, and after Edit › Preferences saves).</summary>
    public void LoadSettings()
    {
        _loading = true;
        try
        {
            Provider.SelectedIndex = Math.Max(0, PreferencesWindow.ChatProviders.FindIndex(c => c.Id == _prefs.ChatProvider));
            BaseUrl.Text = _prefs.ChatServerUrl;
            ServerModel.Text = _prefs.ChatServerModel;
            CoreTools.IsChecked = _prefs.ChatCoreTools;
            SeesPictures.IsChecked = _prefs.ChatModelSeesPictures;
        }
        finally
        {
            _loading = false;
        }
        SettingsChanged();
    }

    private ChatProvider SelectedProvider => Provider.SelectedIndex == 1 ? ChatProvider.OpenAICompatible : ChatProvider.Anthropic;

    /// <summary>A setting changed: the session, Preferences, the using line and which controls show all follow.</summary>
    private void SettingsChanged()
    {
        if (_loading) return;
        var provider = SelectedProvider;
        _prefs.ChatProvider = PreferencesWindow.ChatProviders[provider == ChatProvider.Anthropic ? 0 : 1].Id;
        _prefs.ChatServerUrl = string.IsNullOrWhiteSpace(BaseUrl.Text) ? OpenAICompatibleClient.DefaultBaseUrl : BaseUrl.Text.Trim();
        _prefs.ChatServerModel = ServerModel.Text?.Trim() ?? "";
        _prefs.ChatCoreTools = CoreTools.IsChecked == true;
        _prefs.ChatModelSeesPictures = SeesPictures.IsChecked == true;
        bool hadChat = Session.Entries.Count > 0;
        if (Session.Provider != provider)
        {
            Session.Provider = provider;
            if (hadChat) _status("New chat: a conversation can't move between providers.");
        }
        Session.ServerModel = _prefs.ChatServerModel;
        Session.CoreToolsOnly = _prefs.ChatCoreTools;
        Session.ModelSeesPictures = _prefs.ChatModelSeesPictures;
        bool server = provider == ChatProvider.OpenAICompatible;
        _serverSettings.IsVisible = server;
        _model.IsVisible = !server;
        Input.PlaceholderText = server ? "Ask the model to model or stage something… (Enter sends)" : "Ask Claude to model or stage something… (Enter sends)";
        _using.Text = UsingLine();
        ToolTip.SetTip(_using, _using.Text);
        RefreshIntro();
    }

    /// <summary>"Using Claude Opus 5.5 (Anthropic API)", or "Using qwen3 on localhost:11434 (OpenAI-compatible)".</summary>
    private string UsingLine()
    {
        if (SelectedProvider == ChatProvider.Anthropic) return $"Using {ChatModels.Label(Session.Model)} (Anthropic API)";
        var server = OpenAICompatibleClient.Describe(_prefs.ChatServerUrl);
        var model = _prefs.ChatServerModel.Length > 0 ? _prefs.ChatServerModel : "no model chosen yet";
        return $"Using {model} on {server} (OpenAI-compatible{(_prefs.ChatCoreTools ? ", core tools" : "")})";
    }

    /// <summary>Lists the server's models into <see cref="FoundModels"/>, or says in the status bar why it can't.</summary>
    public async Task RefreshModels()
    {
        _refresh.IsEnabled = false;
        try
        {
            var models = await Server().ListModelsAsync();
            FoundModels.ItemsSource = models.ToList();
            FoundModels.IsVisible = models.Count > 0;
            _status(models.Count == 0 ? "The server lists no models (with Ollama, \"ollama pull qwen3\" fetches one)." : $"The server offers {models.Count} model{(models.Count == 1 ? "" : "s")}: choose one below the model box.");
        }
        catch (Exception e)
        {
            _status(ChatErrors.Describe(e));
        }
        finally
        {
            _refresh.IsEnabled = true;
        }
    }

    /// <summary>The OpenAI-compatible server's client for the current base URL and key (made again when either changes).</summary>
    private OpenAICompatibleClient Server()
    {
        var url = _prefs.ChatServerUrl;
        var key = _serverKeys.Get();
        if (_server is not { } s || s.Url != url || s.Key != key) _server = (url, key, ServerFactory(url, key));
        return _server.Value.Client;
    }

    /// <summary>Says what the chat can do, and how to give it a key if it has none.</summary>
    public void RefreshIntro()
    {
        if (SelectedProvider == ChatProvider.OpenAICompatible)
        {
            var server = OpenAICompatibleClient.Describe(_prefs.ChatServerUrl);
            var local = OpenAICompatibleClient.IsLocalUrl(_prefs.ChatServerUrl);
            _intro.Text = _prefs.ChatServerModel.Length == 0
                ? $"Chat with a model on your own server ({server}) to build in the open window.\n\n" +
                  "No model is chosen yet: press Setup… above, check the base URL (Ollama's is http://localhost:11434/v1), and enter a model name or press Refresh to list the server's models. The model must support tool calls (qwen3, llama3.1, mistral-small…)."
                : $"Chat with {_prefs.ChatServerModel} on {server} to build in the open window: \"make a chess pawn\", \"put a box on a table\". " +
                  "You watch it work; Ctrl+Z undoes its modeling steps. " +
                  (local ? "It runs on this computer: nothing leaves it, and there's no bill." : "Requests go only to that server.") +
                  " Small local models handle simple jobs best.";
            return;
        }
        var key = _keys.Get();
        _intro.Text = key == null
            ? "Chat with Claude to build in the open window: \"make a chess pawn\", \"put it on a table with a spotlight and render it\".\n\n" +
              $"There's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting AVA Modeler, or press Key… above to enter one. " +
              "Your own Anthropic API account is billed for what the chat uses."
            : "Chat with Claude to build in the open window: \"make a chess pawn\", \"put it on a table with a spotlight and render it\". " +
              "You watch it work; Ctrl+Z undoes its modeling steps. Your Anthropic API account is billed for what it uses.";
    }

    /// <summary>The intro's words (for tests).</summary>
    public string IntroText => _intro.Text ?? "";

    /// <summary>Puts the cursor in the input box.</summary>
    public void FocusInput() => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Background);

    private void SendOrStop()
    {
        if (Session.Working) Session.Stop();
        else Send();
    }

    /// <summary>Sends what's in the input box.</summary>
    public void Send()
    {
        var text = Input.Text?.Trim() ?? "";
        if (text.Length == 0 || Session.Working) return;
        Input.Text = "";
        UseKey();
        Running = Guarded(Session.SendAsync(text));
    }

    private void Retry()
    {
        if (Session.Working) return;
        UseKey();
        Running = Guarded(Session.RetryAsync());
    }

    /// <summary>A request's failure shows as a line in the chat; it never takes the app down.</summary>
    private async Task Guarded(Task request)
    {
        try { await request; }
        catch (Exception e)
        {
            _messages.Children.Add(View(new NoticeEntry(ChatErrors.Describe(e), NoticeKind.Error)));
        }
    }

    /// <summary>The client for the current key (made again only when the key changes), or none.</summary>
    private void UseKey()
    {
        if (Session.Provider == ChatProvider.OpenAICompatible)
        {
            Session.Server = Server();
            return;
        }
        var key = _keys.Get();
        if (key == null) { Session.Client = null; _client = null; return; }
        if (_client is not { } c || c.Key != key) _client = (key, ClientFactory(key));
        Session.Client = _client.Value.Client;
    }

    private void NewChat()
    {
        Session.NewChat();
        RefreshIntro();
        _status("New chat.");
    }

    public void Stop() => Session.Stop();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    // ───────────────────────────── the entries ─────────────────────────────

    private Control View(ChatEntry entry) => entry switch
    {
        UserEntry u => Bubble(new SelectableTextBlock { Text = u.Text, TextWrapping = TextWrapping.Wrap, Foreground = Look.TextDark, FontSize = 13 }, Look.Button, new Thickness(40, 0, 0, 0)),
        AssistantEntry a => AssistantView(a),
        ToolEntry t => ToolView(t),
        NoticeEntry n => NoticeView(n),
        _ => new TextBlock(),
    };

    private static Border Bubble(Control child, IBrush background, Thickness margin) => new()
    {
        Background = background, CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 5), Margin = margin, Child = child,
    };

    private Control AssistantView(AssistantEntry a)
    {
        var text = new SelectableTextBlock { Text = a.Text, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 13 };
        a.Changed += () => { text.Text = a.Text; ScrollToEnd(); };
        return Bubble(text, Look.Panel, new Thickness(0, 0, 20, 0));
    }

    /// <summary>A tool call: "▸ run_tool Ball → …", which opens to its input, result and full-size pictures.</summary>
    private Control ToolView(ToolEntry t)
    {
        var line = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        var toggle = new Button { Content = line, Background = Brushes.Transparent, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        var thumbs = new WrapPanel { Orientation = Orientation.Horizontal };
        var details = new StackPanel { Spacing = 4, IsVisible = false, Margin = new Thickness(14, 2, 0, 4) };
        var box = new StackPanel { Children = { toggle, thumbs, details } };
        void Refresh()
        {
            line.Text = (details.IsVisible ? "▾ " : "▸ ") + t.Line;
            line.Foreground = t.IsError ? new SolidColorBrush(Color.FromRgb(255, 150, 140)) : t.Running ? Look.Text : Look.ButtonOn;
            ToolTip.SetTip(toggle, t.Running ? "Running…" : "Show the input and result");
            thumbs.Children.Clear();
            foreach (var image in t.Images) if (Picture(image, 150) is { } p) thumbs.Children.Add(p);
            details.Children.Clear();
            if (details.IsVisible)
            {
                details.Children.Add(Detail("Input", t.Input));
                if (!t.Running) details.Children.Add(Detail(t.IsError ? "Error" : "Result", t.Result.Length > 0 ? t.Result : "(no text)"));
                foreach (var image in t.Images) if (Picture(image, 340) is { } p) details.Children.Add(p);
            }
        }
        toggle.Click += (_, _) => { details.IsVisible = !details.IsVisible; Refresh(); };
        t.Changed += Refresh;
        Refresh();
        return box;
    }

    private static Control Detail(string label, string text) => new StackPanel
    {
        Children =
        {
            new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Look.Text },
            new SelectableTextBlock { Text = text.Length > 4000 ? text[..4000] + "\n…" : text, FontFamily = new FontFamily("monospace"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text },
        },
    };

    private static Control? Picture(ToolImage image, double width)
    {
        try
        {
            using var stream = new MemoryStream(image.Data);
            var bitmap = new Bitmap(stream);
            return new Border
            {
                BorderBrush = Look.Panel, BorderThickness = new Thickness(1), Margin = new Thickness(14, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = bitmap, Width = width, Stretch = Stretch.Uniform },
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private Control NoticeView(NoticeEntry n)
    {
        var text = new TextBlock
        {
            Text = n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontStyle = n.Kind == NoticeKind.Info ? FontStyle.Italic : FontStyle.Normal,
            Foreground = n.Kind == NoticeKind.Error ? new SolidColorBrush(Color.FromRgb(255, 150, 140)) : Look.Text,
        };
        var panel = new StackPanel { Spacing = 4, Children = { text } };
        if (n.CanRetry)
        {
            var retry = Look.MakeButton("Retry", Retry, "Send the conversation again", 80);
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(retry);
            n.Changed += () => retry.IsVisible = n.CanRetry;
        }
        return panel;
    }

    // ───────────────────────────── the key ─────────────────────────────

    /// <summary>
    /// The key dialog: enter a key, or remove the saved one. It says where the key is kept. With an OpenAI-compatible
    /// server it's that server's (optional) key, for a gateway that asks for one, kept in a file of its own.
    /// </summary>
    private async Task EditKey()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        bool server = SelectedProvider == ChatProvider.OpenAICompatible;
        var keys = server ? _serverKeys : _keys;
        var box = new TextBox { PasswordChar = '•', PlaceholderText = server ? "the gateway's key" : "sk-ant-…", FontSize = 13 };
        var name = OpenAICompatibleClient.Describe(_prefs.ChatServerUrl);
        var where = keys.Path is { } p
            ? $"It's kept in {p}, a file of its own that only your user account can read, and is sent only to {(server ? name : "the Anthropic API")}."
            : "It's kept only while the app is running.";
        var now = keys.Source switch
        {
            KeySource.Environment => $"The {ApiKeyStore.EnvironmentVariable} environment variable is set ({ApiKeyStore.Mask(keys.Get())}), and is used before a key entered here.",
            KeySource.File => $"A key is saved: {ApiKeyStore.Mask(keys.Get())}.",
            _ => "No key is set yet.",
        };
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Look.Text,
            Text = server
                ? $"An API key for the OpenAI-compatible server, if it asks for one (a company gateway, say). A server on this computer (Ollama, LM Studio) needs none. {where}\n\n{now}"
                : $"Paste your Anthropic API key (from console.anthropic.com). {where} Your own API account is billed for what the chat uses.\n\n{now}",
        };
        var save = new Button { Content = "Save", Width = 90 };
        var remove = new Button { Content = "Remove Saved Key", IsEnabled = keys.Path != null && keys.Source != KeySource.None };
        var cancel = new Button { Content = "Cancel", Width = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { remove, save, cancel } };
        var window = new Window
        {
            Title = server ? "Model Server API Key" : "Anthropic API Key", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Panel,
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Spacing = 12, Children = { text, box, buttons } } },
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            try
            {
                keys.Save(box.Text);
                _status("The API key is saved.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status("The API key couldn't be saved: the settings folder can't be written.");
            }
            box.Text = "";
            window.Close();
        };
        remove.Click += (_, _) => { keys.Remove(); _status("The saved API key is removed."); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await Dialogs.ShowModal(window, owner);
        RefreshIntro();
    }
}
