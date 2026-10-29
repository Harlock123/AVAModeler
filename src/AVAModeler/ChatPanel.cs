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
/// The AI Chat panel on the right of the window (F4, or Macros › AI Chat): the conversation with Claude, each tool
/// call as a compact line that opens to its input and result, small pictures of what Claude rendered, and below them the
/// input box (Enter sends, Shift+Enter starts a new line), Send (Stop while Claude works) and the model. The
/// conversation itself is <see cref="ChatSession"/>'s; this is only its controls.
/// </summary>
internal sealed class ChatPanel : DockPanel
{
    private readonly ApiKeyStore _keys;
    private readonly Action<string> _status;
    private readonly StackPanel _messages = new() { Spacing = 6, Margin = new Thickness(6) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 12, Opacity = 0.85 };
    private readonly ComboBox _model;
    private readonly Button _send;
    private (string Key, IAnthropicClient Client)? _client;

    public ChatSession Session { get; }

    /// <summary>Where the key comes from (tests set one for the run only).</summary>
    public ApiKeyStore Keys => _keys;

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

    public ChatPanel(IToolBridge tools, ApiKeyStore keys, Action<string> status)
    {
        _keys = keys;
        _status = status;
        Session = new ChatSession(tools);
        Width = 380;
        Background = Look.Panel;

        var title = new TextBlock { Text = "AI Chat", FontWeight = FontWeight.Bold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Look.MakeButton("New Chat", NewChat, "Forget this conversation and start again"));
        buttons.Children.Add(Look.MakeButton("Key…", () => _ = EditKey(), "Your Anthropic API key"));
        buttons.Children.Add(Look.MakeButton("×", () => CloseRequested?.Invoke(), "Hide the chat (F4)"));
        var header = new DockPanel { Background = Look.PanelDark, Height = 30 };
        SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);

        _model = new ComboBox { ItemsSource = ChatModels.All.Select(m => m.Label).ToList(), SelectedIndex = 0, Width = 170, FontSize = 12 };
        _model.SelectionChanged += (_, _) => { if (_model.SelectedIndex >= 0) Session.Model = ChatModels.All[_model.SelectedIndex].Id; };
        ToolTip.SetTip(_model, "The Claude model. Opus is the most capable; Sonnet and Haiku are quicker and cost less.");
        _send = Look.MakeButton("Send", SendOrStop, "Send (Enter); Stop while Claude is working", 80);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        SetDock(_send, Dock.Right);
        row.Children.Add(_send);
        row.Children.Add(_model);
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
            _status(Session.Working ? "Claude is working…" : "Claude has finished.");
        };
        RefreshIntro();
    }

    /// <summary>Says what the chat can do, and how to give it a key if it has none.</summary>
    public void RefreshIntro()
    {
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

    /// <summary>The key dialog: enter a key, or remove the saved one. It says where the key is kept.</summary>
    private async Task EditKey()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var box = new TextBox { PasswordChar = '•', PlaceholderText = "sk-ant-…", FontSize = 13 };
        var where = _keys.Path is { } p
            ? $"It's kept in {p}, a file of its own that only your user account can read, and is sent only to the Anthropic API."
            : "It's kept only while the app is running.";
        var now = _keys.Source switch
        {
            KeySource.Environment => $"The {ApiKeyStore.EnvironmentVariable} environment variable is set ({ApiKeyStore.Mask(_keys.Get())}), and is used before a key entered here.",
            KeySource.File => $"A key is saved: {ApiKeyStore.Mask(_keys.Get())}.",
            _ => "No key is set yet.",
        };
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Look.Text,
            Text = $"Paste your Anthropic API key (from console.anthropic.com). {where} Your own API account is billed for what the chat uses.\n\n{now}",
        };
        var save = new Button { Content = "Save", Width = 90 };
        var remove = new Button { Content = "Remove Saved Key", IsEnabled = _keys.Path != null && _keys.Source != KeySource.None };
        var cancel = new Button { Content = "Cancel", Width = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { remove, save, cancel } };
        var window = new Window
        {
            Title = "Anthropic API Key", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Panel,
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Spacing = 12, Children = { text, box, buttons } } },
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            try
            {
                _keys.Save(box.Text);
                _status("The API key is saved.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status("The API key couldn't be saved: the settings folder can't be written.");
            }
            box.Text = "";
            window.Close();
        };
        remove.Click += (_, _) => { _keys.Remove(); _status("The saved API key is removed."); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await Dialogs.ShowModal(window, owner);
        RefreshIntro();
    }
}
