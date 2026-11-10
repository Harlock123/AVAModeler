using System.Globalization;
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

namespace AVAModeler.Chat.UI;

/// <summary>
/// The AI Chat panel on the right of the window (F4, or Macros › AI Chat): the conversation with the model, each tool
/// call as a compact line that opens to its input and result, small pictures of what the model rendered, and below them
/// the input box (Enter sends, Shift+Enter starts a new line), Send (Stop while the model works) and the Claude model.
/// A line under the title says which provider and model it's using; Setup… chooses between Claude (the Anthropic API)
/// and an OpenAI-compatible server (a local model through Ollama, LM Studio, vLLM or llama.cpp, or a gateway), kept in
/// Preferences. History shows the saved chats (kept only with Keep chat history ticked): open, export as Markdown, delete,
/// clear. The conversation itself is <see cref="ChatSession"/>'s, and saving it <see cref="ChatHistory"/>'s; this is only
/// their controls.
/// </summary>
public sealed class ChatPanel : DockPanel
{
    /// <summary>
    /// The app the panel is in, as its lines name it: AVA Modeler's words unless an app on this engine (AVALightWave)
    /// gives its own. Set it before the panel is shown.
    /// </summary>
    public ChatWording Wording
    {
        get => _wording;
        set
        {
            _wording = value;
            Session.NoKeyText = $"There's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting {value.App}, or enter a key with the Key… button above. Your own API account is billed for what the chat uses.";
            Input.PlaceholderText = value.Placeholder(SelectedProvider == ChatProvider.OpenAICompatible ? "the model" : "Claude");
            RefreshIntro();
        }
    }

    private ChatWording _wording = ChatWording.Modeler;

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
    private readonly StackPanel _historyList = new() { Spacing = 4, Margin = new Thickness(6) };
    private readonly Border _history;
    private readonly Border _readOnlyBar;
    private readonly TextBlock _readOnlyText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 12 };

    /// <summary>
    /// The saved conversations (the app gives it the chats folder in its settings folder; null, as in tests unless they
    /// give one in a temporary folder, saves nothing). Chats are saved only while <see cref="KeepHistory"/> is ticked.
    /// </summary>
    public ChatHistory? History { get; set; }

    /// <summary>Keep chat history: save each chat after every request and on closing, and reopen the last on start (Preferences.ChatKeepHistory; off by default).</summary>
    public CheckBox KeepHistory { get; } = new() { Content = "Keep chat history on this computer", FontSize = 12, Foreground = Look.Text };

    /// <summary>Whether the history list is showing in place of the conversation.</summary>
    public bool HistoryOpen => _history.IsVisible;

    /// <summary>Asks where to export a chat as Markdown, given a suggested file name (tests give their own).</summary>
    public Func<string, Task<string?>> PickExportPath { get; set; }

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

    /// <summary>
    /// The live usage line under the input: this chat's tokens and estimated cost, and the turn under way's, updated as
    /// the API reports them; its tooltip has today's and this month's totals.
    /// </summary>
    public TextBlock UsageText { get; } = new() { FontSize = 11, Foreground = Look.Text, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };

    /// <summary>Setup…'s budget: about how many dollars a chat may spend before it stops between requests (empty: no limit).</summary>
    public TextBox BudgetBox { get; } = new() { FontSize = 12, Width = 70, PlaceholderText = "none" };

    /// <summary>Setup…'s prices, one box a Claude model: dollars a million tokens for input, output, cache writes and reads.</summary>
    public Dictionary<string, TextBox> PriceBoxes { get; } = [];

    /// <summary>Setup…'s today and this-month totals.</summary>
    public TextBlock Totals { get; } = new() { FontSize = 11, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap };

    private readonly StackPanel _cost = new() { Spacing = 3 };

    /// <summary>Where the chat's use is added up day by day (the app sets it outside tests), or null.</summary>
    public UsageLedger? Ledger
    {
        get => Session.Ledger;
        set
        {
            if (Session.Ledger != null) Session.Ledger.Changed -= ShowUsageSoon;
            Session.Ledger = value;
            if (value != null) value.Changed += ShowUsageSoon;
            ShowUsage();
        }
    }

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
        PlaceholderText = ChatWording.Modeler.Placeholder("Claude"),
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
        buttons.Children.Add(Look.MakeButton("New Chat", NewChat, "Start a new conversation (a kept one stays in History)"));
        buttons.Children.Add(Look.MakeButton("History", ToggleHistory, "Saved chats: reopen, export as Markdown or delete them, and whether to keep them"));
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
        BuildCost();
        _setup.Children.Add(_cost);
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
        var bottom = new StackPanel { Margin = new Thickness(6), Children = { Input, row, UsageText } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        // A saved chat with the other provider is shown read-only, with a way out.
        var readOnlyNew = Look.MakeButton("New Chat", NewChat, "Start a new conversation with the provider in use", 90);
        readOnlyNew.HorizontalAlignment = HorizontalAlignment.Left;
        _readOnlyBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(96, 80, 48)), Padding = new Thickness(8, 5), IsVisible = false,
            Child = new StackPanel { Spacing = 4, Children = { _readOnlyText, readOnlyNew } },
        };
        SetDock(_readOnlyBar, Dock.Bottom);
        Children.Add(_readOnlyBar);

        _scroll = new ScrollViewer { Content = _messages, Background = Look.PanelDark, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _history = new Border
        {
            Background = Look.PanelDark, IsVisible = false,
            Child = new ScrollViewer { Content = _historyList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        Children.Add(new Border { BorderBrush = Look.PanelDark, BorderThickness = new Thickness(1, 0, 0, 0), Child = new Panel { Children = { _scroll, _history } } });
        _messages.Children.Add(_intro);
        PickExportPath = async name => TopLevel.GetTopLevel(this) is { } top
            ? await Dialogs.Save(top, "Export the chat as Markdown", name, new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = ["*.md"] })
            : null;
        KeepHistory.IsChecked = _prefs.ChatKeepHistory;
        KeepHistory.IsCheckedChanged += (_, _) =>
        {
            _prefs.ChatKeepHistory = KeepHistory.IsChecked == true;
            if (_prefs.ChatKeepHistory) SaveHistory();
            ShowHistory();
        };
        ToolTip.SetTip(KeepHistory, "Save each conversation (after every request and when the app closes) and open the last one again on start. Off unless you turn it on: a chat may hold a client's details.");
        Session.ReadOnlyChanged += ShowReadOnly;

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
            // Each finished request is saved, so a crash loses at most the one under way.
            if (!Session.Working) SaveHistory();
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
        Session.UsageChanged += ShowUsage;
        LoadSettings();
    }

    // ───────────────────────────── what it costs ─────────────────────────────

    /// <summary>Setup…'s Cost section: the budget, each Claude model's prices, today's and this month's totals, and Reset.</summary>
    private void BuildCost()
    {
        _cost.Children.Add(SettingLabel("Cost"));
        ToolTip.SetTip(BudgetBox, "When a chat has used about this many dollars, it stops between requests and says so; sending another message lets it carry on for as much again. Empty for no limit.");
        BudgetBox.LostFocus += (_, _) => ReadBudget();
        _cost.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { new TextBlock { Text = "Stop a chat at about $", FontSize = 12, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center }, BudgetBox },
        });
        _cost.Children.Add(new TextBlock
        {
            Text = $"Prices, $ a million tokens: input, output, cache write, cache read (Anthropic's as of {ChatPricing.AsOf}; change them if they change)",
            FontSize = 11, Foreground = Look.Text, Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
        });
        foreach (var (id, label) in ChatModels.All)
        {
            var box = new TextBox { FontSize = 12, Width = 170 };
            ToolTip.SetTip(box, $"{label}'s prices in dollars a million tokens: input, output, cache write, cache read. Empty puts back the built-in ones.");
            box.LostFocus += (_, _) => ReadPrice(id, box);
            PriceBoxes[id] = box;
            _cost.Children.Add(new DockPanel
            {
                Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Look.Text, Width = 120, VerticalAlignment = VerticalAlignment.Center }, box },
            });
        }
        var reset = Look.MakeButton("Reset totals", () => { Ledger?.Reset(); _status("The chat's usage totals are reset."); }, "Forget today's and this month's totals (saved chats keep their own)", 100);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        _cost.Children.Add(Totals);
        _cost.Children.Add(reset);
    }

    private static string PriceText(ModelPrice p) => string.Join(" ", new[] { p.Input, p.Output, p.CacheWrite, p.CacheRead }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

    /// <summary>The person's own prices, as the session uses them.</summary>
    private List<ModelPrice> OwnPrices() => _prefs.ChatPrices
        .Where(kv => kv.Value is { Length: 4 })
        .Select(kv => new ModelPrice(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
        .Where(p => { try { p.Checked(); return true; } catch (ArgumentException) { return false; } })
        .ToList();

    private void ReadPrice(string model, TextBox box)
    {
        var text = box.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _prefs.ChatPrices.Remove(model);
        }
        else
        {
            var parts = text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            try
            {
                var v = parts.Select(x => double.Parse(x.TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 4) throw new FormatException();
                new ModelPrice(model, v[0], v[1], v[2], v[3]).Checked();
                _prefs.ChatPrices[model] = v;
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                _status($"{ChatModels.Label(model)}'s prices are four numbers of dollars a million tokens: input, output, cache write, cache read.");
            }
        }
        Session.OwnPrices = OwnPrices();
        box.Text = PriceText(ChatPricing.Find(model, Session.OwnPrices)!);
        ShowUsage();
    }

    private void ReadBudget()
    {
        var text = BudgetBox.Text?.Trim().TrimStart('$') ?? "";
        if (text.Length == 0) _prefs.ChatBudget = null;
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 && b < 1e6) _prefs.ChatBudget = b;
        else _status("The budget is a number of dollars above 0, or empty for no limit.");
        Session.Budget = _prefs.ChatBudget;
        BudgetBox.Text = _prefs.ChatBudget is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Takes up the prices and budget from Preferences.</summary>
    private void LoadCost()
    {
        Session.OwnPrices = OwnPrices();
        Session.Budget = _prefs.ChatBudget;
        BudgetBox.Text = _prefs.ChatBudget is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";
        foreach (var (id, box) in PriceBoxes) box.Text = PriceText(ChatPricing.Find(id, Session.OwnPrices)!);
    }

    private void ShowUsageSoon() => Dispatcher.UIThread.Post(ShowUsage);

    /// <summary>The usage line and the totals, as they are now.</summary>
    public void ShowUsage()
    {
        var chat = Session.ChatTokens;
        var priced = Session.Provider == ChatProvider.Anthropic;
        string Cost(double? c) => c is { } d ? "≈ " + ChatPricing.Dollars(d) : priced ? "no price" : "no charge here";
        if (chat.IsEmpty && chat.Requests == 0) UsageText.Text = priced ? "This chat: nothing used yet." : "This chat: nothing used yet (the server's own prices, if any, aren't known here).";
        else
        {
            var line = $"This chat: {chat.Describe()} · {Cost(Session.ChatCost)}";
            if (Session.Working) line += $" · this turn {Cost(Session.TurnCost)}";
            UsageText.Text = line;
        }
        var now = Session.Clock();
        string Total(UsageDay d) => d.Tokens.IsEmpty ? "nothing" : $"{d.Tokens.Describe()} · ≈ {ChatPricing.Dollars(d.Cost)}";
        if (Ledger is { } l)
        {
            var today = l.Day(DateOnly.FromDateTime(now.LocalDateTime));
            var month = l.Month(now.LocalDateTime.Year, now.LocalDateTime.Month);
            Totals.Text = $"Today: {Total(today)}\nThis month: {Total(month)}";
        }
        else Totals.Text = "Totals aren't kept in this window.";
        ToolTip.SetTip(UsageText, $"{Totals.Text}\n\nEstimated from the tokens the API reports and the prices in Setup…; the Anthropic Console has the bill. Each tool call sends the conversation again, so a turn is often many requests; the prompt cache makes most of each one cheap.");
    }

    private static TextBlock SettingLabel(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 3, 0, 0) };

    /// <summary>Takes up the chat settings from Preferences (at the start, and after Edit › Preferences saves).</summary>
    public void LoadSettings()
    {
        _loading = true;
        LoadCost();
        try
        {
            Provider.SelectedIndex = Math.Max(0, PreferencesWindow.ChatProviders.FindIndex(c => c.Id == _prefs.ChatProvider));
            BaseUrl.Text = _prefs.ChatServerUrl;
            ServerModel.Text = _prefs.ChatServerModel;
            CoreTools.IsChecked = _prefs.ChatCoreTools;
            SeesPictures.IsChecked = _prefs.ChatModelSeesPictures;
            KeepHistory.IsChecked = _prefs.ChatKeepHistory;
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
        bool hadChat = Session.Entries.Count > 0 && !Session.ReadOnly;
        if (Session.Provider != provider)
        {
            // The chat so far is kept (if history is on) before a new one starts.
            if (hadChat) SaveHistory();
            Session.Provider = provider;
            if (hadChat) _status("New chat: a conversation can't move between providers.");
        }
        Session.ServerName = OpenAICompatibleClient.Describe(_prefs.ChatServerUrl);
        Session.ServerModel = _prefs.ChatServerModel;
        Session.CoreToolsOnly = _prefs.ChatCoreTools;
        Session.ModelSeesPictures = _prefs.ChatModelSeesPictures;
        bool server = provider == ChatProvider.OpenAICompatible;
        _serverSettings.IsVisible = server;
        _model.IsVisible = !server;
        Input.PlaceholderText = Wording.Placeholder(server ? "the model" : "Claude");
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
                : $"Chat with {_prefs.ChatServerModel} on {server} to build in the open window: {Wording.ServerExamples}. " +
                  $"You watch it work; {Wording.Undo} " +
                  (local ? "It runs on this computer: nothing leaves it, and there's no bill." : "Requests go only to that server.") +
                  " Small local models handle simple jobs best.";
            return;
        }
        var key = _keys.Get();
        _intro.Text = key == null
            ? $"Chat with Claude to build in the open window: {Wording.Examples}.\n\n" +
              $"There's no Anthropic API key yet. Set the {ApiKeyStore.EnvironmentVariable} environment variable before starting {Wording.App}, or press Key… above to enter one. " +
              "Your own Anthropic API account is billed for what the chat uses."
            : $"Chat with Claude to build in the open window: {Wording.Examples}. " +
              $"You watch it work; {Wording.Undo} Your Anthropic API account is billed for what it uses.";
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
        SaveHistory();
        Session.NewChat();
        RefreshIntro();
        if (HistoryOpen) ToggleHistory();
        _status("New chat.");
    }

    // ───────────────────────────── history ─────────────────────────────

    /// <summary>
    /// Saves the conversation on show in the history, if there is one, history is kept, and the person has said
    /// something; a read-only chat (another provider's) is left as it was saved. Failures go to the status bar.
    /// </summary>
    public void SaveHistory()
    {
        if (History is not { } history || KeepHistory.IsChecked != true || !Session.HasConversation || Session.ReadOnly) return;
        try
        {
            long used = 0;
            history.Save(Session.ToStored(image => history.KeepImage(Session.ChatId, image, ref used)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status("The chat couldn't be saved: the settings folder can't be written.");
        }
    }

    /// <summary>Opens the newest saved chat again (on start, when history is kept). Returns whether there was one.</summary>
    public bool RestoreLast()
    {
        if (History is not { } history || KeepHistory.IsChecked != true || history.List().FirstOrDefault() is not { } last) return false;
        return OpenChat(last.Id);
    }

    /// <summary>Opens a saved chat in place of this one (which is saved first). Returns whether it could be read.</summary>
    public bool OpenChat(string id)
    {
        if (History is not { } history || history.Load(id) is not { } chat) return false;
        if (Session.Working) Session.Stop();
        SaveHistory();
        Session.Open(chat, name => history.ReadImage(id, name));
        if (HistoryOpen) ToggleHistory();
        ShowReadOnly();
        ScrollToEnd();
        _status(Session.ReadOnly ? $"Opened \"{chat.Title}\" read-only: it was with {Who(chat.Provider)}." : $"Opened \"{chat.Title}\".");
        return true;
    }

    /// <summary>Deletes a saved chat; if it's the one on show, a new chat starts so it isn't saved again.</summary>
    public void DeleteChat(string id)
    {
        if (History is not { } history) return;
        history.Delete(id);
        if (Session.ChatId == id)
        {
            Session.NewChat();
            RefreshIntro();
        }
        ShowHistory();
        _status("The chat is deleted.");
    }

    /// <summary>Deletes every saved chat (Clear History) and starts a new one.</summary>
    public void ClearHistory()
    {
        History?.Clear();
        Session.NewChat();
        RefreshIntro();
        ShowHistory();
        _status("Chat history cleared.");
    }

    /// <summary>
    /// Writes a chat as Markdown where the person chooses (the one on show, or a saved one by id), its pictures in a
    /// folder beside it. Returns the file, or null if nothing was written.
    /// </summary>
    public async Task<string?> ExportChat(string? id = null)
    {
        StoredChat chat;
        Func<string, ToolImage?> pictures;
        if (id != null && History?.Load(id) is { } saved)
        {
            chat = saved;
            pictures = name => History.ReadImage(id, name);
        }
        else
        {
            if (!Session.HasConversation) { _status("There's nothing to export yet."); return null; }
            var images = new Dictionary<string, ToolImage>();
            chat = Session.ToStored(image => { var name = ChatHistory.ImageName(image); images[name] = image; return name; });
            pictures = name => images.GetValueOrDefault(name);
        }
        var suggested = new string((chat.Title.Length > 0 ? chat.Title : "chat").Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : ' ').ToArray()).Trim();
        if (suggested.Length > 40) suggested = suggested[..40].Trim();
        var path = await PickExportPath((suggested.Length > 0 ? suggested : "chat") + ".md");
        if (path == null) return null;
        try
        {
            ChatMarkdown.Save(chat, pictures, path, Wording.App);
            _status($"Exported the chat as {Path.GetFileName(path)}.");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status($"The chat couldn't be exported: {e.Message}");
            return null;
        }
    }

    private static string Who(ChatProvider p) => p == ChatProvider.Anthropic ? "Claude (the Anthropic API)" : "an OpenAI-compatible server";

    /// <summary>Shows or hides the history list in place of the conversation.</summary>
    public void ToggleHistory()
    {
        _history.IsVisible = !_history.IsVisible;
        _scroll.IsVisible = !_history.IsVisible;
        if (_history.IsVisible) ShowHistory();
    }

    /// <summary>The history list: Keep chat history, Export This Chat and Clear History, then each saved chat with Open, Export and Delete.</summary>
    private void ShowHistory()
    {
        _historyList.Children.Clear();
        _historyList.Children.Add(new TextBlock { Text = "Chat history", FontWeight = FontWeight.Bold, Foreground = Look.Text, FontSize = 13 });
        _historyList.Children.Add(KeepHistory);
        _historyList.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 11, Opacity = 0.85,
            Text = "Kept chats are saved in the chats folder beside your preferences, readable only by you, after each request and when the app closes; the last one opens again on start. " +
                   "No API key is ever saved with them; pictures are kept up to 2 MB each. A chat with the other provider opens read-only. Off unless you tick it, as a chat may hold a client's details.",
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(Look.MakeButton("Export This Chat…", () => _ = ExportChat(), "Write the conversation on show as a Markdown file, its pictures beside it"));
        actions.Children.Add(Look.MakeButton("Clear History", ClearHistory, "Delete every saved chat"));
        _historyList.Children.Add(actions);
        var chats = History?.List() ?? [];
        if (chats.Count == 0)
            _historyList.Children.Add(new TextBlock { Text = History == null ? "History isn't available here." : "No saved chats.", Foreground = Look.Text, FontSize = 12, FontStyle = FontStyle.Italic, Margin = new Thickness(0, 6) });
        foreach (var c in chats)
        {
            bool current = c.Id == Session.ChatId;
            var open = new Button
            {
                Content = new TextBlock { Text = (current ? "● " : "") + (c.Title.Length > 0 ? c.Title : "(untitled)"), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Look.TextDark },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Look.Button, Padding = new Thickness(6, 3),
            };
            var id = c.Id;
            open.Click += (_, _) => OpenChat(id);
            ToolTip.SetTip(open, current ? "The chat on show" : "Open this chat (the one on show is saved first)");
            var who = c.Provider == ChatProvider.Anthropic ? ChatModels.Label(c.Model) : $"{(c.Model.Length > 0 ? c.Model : "a model")} (OpenAI-compatible)";
            var details = new TextBlock
            {
                Text = $"{who} · {c.Updated.ToLocalTime():d MMM yyyy HH:mm} · {c.Messages} message{(c.Messages == 1 ? "" : "s")}",
                FontSize = 10, Foreground = Look.Text, Opacity = 0.8, Margin = new Thickness(2, 1, 0, 0),
            };
            var export = Look.MakeButton("Export…", () => _ = ExportChat(id), "Write this chat as a Markdown file");
            var delete = Look.MakeButton("Delete", () => DeleteChat(id), "Delete this saved chat");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Children = { export, delete } };
            var head = new DockPanel();
            SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            head.Children.Add(open);
            _historyList.Children.Add(new Border
            {
                Background = Look.Panel, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 4), Margin = new Thickness(0, 2),
                Child = new StackPanel { Children = { head, details } },
            });
        }
    }

    /// <summary>The read-only bar and the input follow whether the chat on show can be carried on.</summary>
    private void ShowReadOnly()
    {
        bool ro = Session.ReadOnly;
        _readOnlyBar.IsVisible = ro;
        Input.IsEnabled = !ro;
        _send.IsEnabled = !ro;
        if (ro && Session.ReadOnlyProvider is { } was)
            _readOnlyText.Text = $"This chat was with {Who(was)}, and the chat now uses {Who(Session.Provider)}: it's shown read-only. Start a New Chat, or choose {(was == ChatProvider.Anthropic ? "Claude" : "the OpenAI-compatible server")} in Setup… to carry it on.";
    }

    /// <summary>The read-only bar's words (for tests).</summary>
    public string ReadOnlyText => _readOnlyBar.IsVisible ? _readOnlyText.Text ?? "" : "";

    public void Stop() => Session.Stop();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    // ───────────────────────────── the entries ─────────────────────────────

    private Control View(ChatEntry entry) => entry switch
    {
        UserEntry u => Bubble(new SelectableTextBlock { Text = u.Text, TextWrapping = TextWrapping.Wrap, Foreground = Look.TextDark, FontSize = 13 }, Look.Button, new Thickness(40, 0, 0, 0)),
        AssistantEntry a => AssistantView(a),
        ToolEntry t => ToolView(t),
        NoticeEntry n => NoticeView(n),
        UsageEntry u => UsageView(u),
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

    /// <summary>A turn's use, a small line after it; its tooltip lists each request.</summary>
    private static Control UsageView(UsageEntry u)
    {
        var text = new TextBlock { Text = u.Line, FontSize = 10, Foreground = Look.Text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(text, u.Details);
        return text;
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

/// <summary>
/// How the chat panel words what it's for: the app's name, the input box's prompt, examples to try, and what Ctrl+Z
/// undoes. <see cref="Modeler"/> is AVA Modeler's.
/// </summary>
public sealed record ChatWording(string App, string Verb, string Examples, string ServerExamples, string Undo)
{
    public static ChatWording Modeler { get; } = new(
        "AVA Modeler", "model or stage something",
        "\"make a chess pawn\", \"put it on a table with a spotlight and render it\"",
        "\"make a chess pawn\", \"put a box on a table\"",
        "Ctrl+Z undoes its modeling steps.");

    /// <summary>The input box's prompt for a model ("Claude", "the model").</summary>
    public string Placeholder(string who) => $"Ask {who} to {Verb}… (Enter sends)";
}
