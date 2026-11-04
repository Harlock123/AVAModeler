using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// Backdrop (D, View › Backdrop, or the Display tab): the reference image behind each flat view, as classic Modeler's
/// Display Options › Backdrop had. Choose the view, Browse for an image, and set its centre (metres on the view's two
/// axes), width (and height, or the image's own shape), brightness, contrast, opacity, invert and whether it shows; the
/// views follow every change at once. Fit to Object and Size from Pixels place it; Save Setup and Load Setup keep a
/// setup in a JSON file. The settings and their maths are <see cref="Backdrop"/> and <see cref="BackdropMapping"/>; this
/// only shows them.
/// </summary>
public sealed class BackdropPanel : Border
{
    private readonly ModelerSession _session;
    private readonly StackPanel _rows = new() { Spacing = 3, Margin = new Thickness(8, 0, 8, 8) };
    private readonly Dictionary<BackdropView, Button> _viewButtons = [];
    private readonly List<Action> _refresh = [];
    private bool _syncing;

    public const double PanelWidth = 260;

    /// <summary>The view whose backdrop is shown and edited.</summary>
    public BackdropView View { get; private set; } = BackdropView.Front;

    /// <summary>The image's path as shown (type one and press Enter to use it).</summary>
    public TextBox ImageBox { get; } = new() { Name = "BackdropImage", FontSize = 11, PlaceholderText = "No image: Browse…" };

    /// <summary>What the image is: its pixels, or why it can't be had.</summary>
    public TextBlock Info { get; } = new() { FontSize = 11, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

    /// <summary>The number boxes by name (CenterX, CenterY, Width, Height, Brightness, Contrast, Opacity, PixelSize).</summary>
    public Dictionary<string, TextBox> Fields { get; } = [];

    /// <summary>The panel's check boxes by name (Visible, Invert, KeepShape).</summary>
    public Dictionary<string, CheckBox> Checks { get; } = [];

    private readonly TextBlock _centerLabel = new() { FontSize = 11, Foreground = Look.Text };
    private double _pixelSizeMm = 5;

    /// <summary>Asks for an image file (the window shows a file dialog); null if none was chosen.</summary>
    public Func<Task<string?>>? AskImage { get; set; }

    /// <summary>Asks where to save or which setup to load (the window shows a file dialog).</summary>
    public Func<bool, Task<string?>>? AskSetup { get; set; }

    public BackdropPanel(ModelerSession session)
    {
        _session = session;
        Width = PanelWidth;
        Background = Look.PanelDark;
        BorderBrush = Look.Panel;
        BorderThickness = new Thickness(1, 0, 0, 0);
        IsVisible = false;
        var close = Look.MakeButton("×", () => IsVisible = false, "Close Backdrop (D)");
        close.Padding = new Thickness(5, 0);
        var header = new DockPanel { Margin = new Thickness(8, 6, 4, 4) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Backdrop", FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(new ScrollViewer { Content = _rows });
        Child = body;
        Build();
        session.Changed += () => { if (IsVisible) Refresh(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Refresh(); };
    }

    /// <summary>Shows or hides the panel.</summary>
    public void Toggle() => IsVisible = !IsVisible;

    private Backdrop? Current => _session.Document.Backdrops[View];

    // ───────────────────────────── building ─────────────────────────────

    private void Build()
    {
        var views = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var v in Enum.GetValues<BackdropView>())
        {
            var b = Look.MakeButton(v.ToString(), () => ShowView(v), $"The {v} view's backdrop", 76);
            b.Name = $"Backdrop{v}";
            _viewButtons[v] = b;
            views.Children.Add(b);
        }
        _rows.Children.Add(views);

        _rows.Children.Add(Label("Image"));
        ImageBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) UseImage(ImageBox.Text ?? ""); };
        _rows.Children.Add(ImageBox);
        var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        files.Children.Add(Named(Look.MakeButton("Browse…", () => _ = Browse(), "Choose a picture (PNG, JPEG, BMP…)"), "BackdropBrowse"));
        files.Children.Add(Named(Look.MakeButton("Clear", Clear, "Take this view's backdrop away"), "BackdropClear"));
        _rows.Children.Add(files);
        _rows.Children.Add(Info);

        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        flags.Children.Add(Check("Visible", "Show", b => b.Visible, (b, on) => b with { Visible = on }));
        flags.Children.Add(Check("Invert", "Invert", b => b.Invert, (b, on) => b with { Invert = on }));
        _rows.Children.Add(flags);

        _rows.Children.Add(_centerLabel);
        var centre = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var cx = Number("CenterX", b => b.Center.X, (b, v) => b with { Center = b.Center with { X = v } });
        var cy = Number("CenterY", b => b.Center.Y, (b, v) => b with { Center = b.Center with { Y = v } });
        Grid.SetColumn(cy, 1);
        centre.Children.Add(cx);
        centre.Children.Add(cy);
        _rows.Children.Add(centre);

        _rows.Children.Add(Label("Width (m)"));
        _rows.Children.Add(Number("Width", b => b.Width, (b, v) => b with { Width = v }, min: 1e-6));
        _rows.Children.Add(Check("KeepShape", "Height from the image's shape", b => b.Height == null,
            (b, on) => b with { Height = on ? null : Size(b).Height }));
        _rows.Children.Add(Label("Height (m)"));
        var height = Number("Height", b => Size(b).Height, (b, v) => b with { Height = v }, min: 1e-6);
        _refresh.Add(() => height.IsEnabled = Current is { Height: not null });
        _rows.Children.Add(height);

        _rows.Children.Add(Label("Brightness (-1 to 1)"));
        _rows.Children.Add(Number("Brightness", b => b.Brightness, (b, v) => b with { Brightness = v }, -1, 1, 0.05));
        _rows.Children.Add(Label("Contrast (0 to 4)"));
        _rows.Children.Add(Number("Contrast", b => b.Contrast, (b, v) => b with { Contrast = v }, 0, 4, 0.1));
        _rows.Children.Add(Label("Opacity (0 to 1)"));
        _rows.Children.Add(Number("Opacity", b => b.Opacity, (b, v) => b with { Opacity = v }, 0, 1, 0.05));

        _rows.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        _rows.Children.Add(Named(Look.MakeButton("Fit to Object", FitToObject, "Centre and size it round the foreground layers, keeping its shape"), "BackdropFit"));
        _rows.Children.Add(Label("Pixel size (mm)"));
        var pixel = new TextBox { Name = "BackdropPixelSize", FontSize = 11, Text = Format(_pixelSizeMm) };
        void SetPixel()
        {
            if (double.TryParse(pixel.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0) _pixelSizeMm = v;
            pixel.Text = Format(_pixelSizeMm);
        }
        pixel.LostFocus += (_, _) => SetPixel();
        pixel.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetPixel(); };
        Fields["PixelSize"] = pixel;
        _rows.Children.Add(pixel);
        _rows.Children.Add(Named(Look.MakeButton("Size from Pixels", SizeFromPixels, "The width from the image's pixels, each the size above"), "BackdropFromPixels"));

        _rows.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        var setups = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        setups.Children.Add(Named(Look.MakeButton("Save Setup…", () => _ = SaveSetupAsk(), "Save every view's backdrop to a JSON file"), "BackdropSaveSetup"));
        setups.Children.Add(Named(Look.MakeButton("Load Setup…", () => _ = LoadSetupAsk(), "Load backdrops saved with Save Setup"), "BackdropLoadSetup"));
        _rows.Children.Add(setups);
        _rows.Children.Add(Note("Backdrops aren't saved in the object: they're remembered for its file, and Save Setup keeps them in a file of their own."));
        ShowView(View);
    }

    private static T Named<T>(T c, string name) where T : Control
    {
        c.Name = name;
        return c;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11 };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };

    private static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A backdrop's size in metres, from its image's shape unless it has a height.</summary>
    private (double Width, double Height) Size(Backdrop b)
    {
        var image = TextureImages.Load(b.Image, _session.Document.ImageBase);
        return BackdropMapping.Size(b, image.Width, image.Height);
    }

    private CheckBox Check(string name, string text, Func<Backdrop, bool> get, Func<Backdrop, bool, Backdrop> set)
    {
        var cb = new CheckBox { Name = $"Backdrop{name}", Content = new TextBlock { Text = text, Foreground = Look.Text, FontSize = 11 } };
        cb.IsCheckedChanged += (_, _) =>
        {
            if (_syncing || Current is not { } b) return;
            Change(set(b, cb.IsChecked == true));
        };
        _refresh.Add(() => cb.IsChecked = Current is { } b ? get(b) : false);
        Checks[name] = cb;
        return cb;
    }

    /// <summary>A number box: type a value (Enter or leaving the box applies it); the wheel steps it.</summary>
    private TextBox Number(string name, Func<Backdrop, double> get, Func<Backdrop, double, Backdrop> set, double min = -1e6, double max = 1e6, double step = 0.05)
    {
        var box = new TextBox { Name = $"Backdrop{name}", FontSize = 11, Margin = new Thickness(1, 0), MinWidth = 0 };
        void Commit()
        {
            if (Current is not { } b) return;
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                Change(set(b, Math.Clamp(v, min, max)));
            Refresh();
        }
        box.LostFocus += (_, _) => { if (!_syncing) Commit(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.PointerWheelChanged += (_, e) =>
        {
            if (Current is not { } b) return;
            Change(set(b, Math.Clamp(get(b) + Math.Sign(e.Delta.Y) * step, min, max)));
            e.Handled = true;
        };
        _refresh.Add(() =>
        {
            box.IsEnabled = Current != null;
            if (!box.IsFocused || Current == null) box.Text = Current is { } b ? Format(get(b)) : "";
        });
        Fields[name] = box;
        return box;
    }

    // ───────────────────────────── acting ─────────────────────────────

    /// <summary>Shows (and edits) one view's backdrop.</summary>
    public void ShowView(BackdropView view)
    {
        View = view;
        foreach (var (v, b) in _viewButtons) Look.SetOn(b, v == view);
        var (r, u) = BackdropMapping.AxisNames(view);
        _centerLabel.Text = $"Centre {r}, {u} (m)";
        Refresh();
    }

    /// <summary>Puts changed settings in place (checked; a bad one is refused in the status bar), and the views follow.</summary>
    private void Change(Backdrop b)
    {
        try
        {
            _session.SetBackdrop(View, b.Checked());
        }
        catch (ArgumentException e)
        {
            _session.SetStatus($"Backdrop: {e.Message}");
        }
        Refresh();
    }

    /// <summary>Uses an image for the view's backdrop: a new one is fitted round the object (or made 2 m across at the origin if it's empty).</summary>
    public void UseImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();
        var doc = _session.Document;
        if (!Path.IsPathRooted(path) && doc.ImageBase != null) path = Path.GetFullPath(Path.Combine(doc.ImageBase, path));
        var b = (Current ?? new Backdrop()) with { Image = path, Visible = true };
        if (Current == null)
        {
            var image = TextureImages.Load(path, doc.ImageBase);
            b = Fit(b, image) ?? (b with { Width = 2 });
        }
        _session.SetBackdrop(View, b);
        if (BackdropMapping.Problems(BackdropSet.Empty.With(View, b), doc.ImageBase).Count == 0)
            _session.SetStatus($"{View} backdrop: {Path.GetFileName(path)}.");
        Refresh();
    }

    private Backdrop? Fit(Backdrop b, TextureImage image)
    {
        var doc = _session.Document;
        var box = doc.Bounds(foregroundOnly: true);
        if (box.IsEmpty) box = doc.Bounds();
        if (box.IsEmpty) return null;
        var (center, width) = BackdropMapping.FitTo(View, box, b, image.Width, image.Height);
        return b with { Center = center, Width = width };
    }

    private async Task Browse()
    {
        if (AskImage == null) return;
        if (await AskImage() is { } path) UseImage(path);
    }

    /// <summary>Takes the view's backdrop away.</summary>
    public void Clear()
    {
        _session.SetBackdrop(View, null);
        _session.SetStatus($"{View} backdrop cleared.");
        Refresh();
    }

    /// <summary>Centres and sizes the backdrop round the foreground layers (Fit to Object).</summary>
    public void FitToObject()
    {
        if (Current is not { } b) { _session.SetStatus("Give the view a backdrop image first."); return; }
        var image = TextureImages.Load(b.Image, _session.Document.ImageBase);
        if (Fit(b, image) is not { } fitted) { _session.SetStatus("There's nothing to fit the backdrop to: the object is empty."); return; }
        Change(fitted);
    }

    /// <summary>The width from the image's pixels at the pixel size given (millimetres), keeping its shape.</summary>
    public void SizeFromPixels()
    {
        if (Current is not { } b) { _session.SetStatus("Give the view a backdrop image first."); return; }
        var image = TextureImages.Load(b.Image, _session.Document.ImageBase);
        Change(b with { Width = BackdropMapping.WidthFromPixels(image.Width, _pixelSizeMm / 1000), Height = null });
    }

    /// <summary>Saves every view's backdrop to a setup file.</summary>
    public void SaveSetup(string path)
    {
        try
        {
            BackdropFiles.Save(_session.Document.Backdrops, path);
            _session.SetStatus($"Saved the backdrop setup {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _session.SetStatus($"Can't save the backdrop setup: {e.Message}");
        }
    }

    /// <summary>Loads backdrops from a setup file, in place of the ones there.</summary>
    public void LoadSetup(string path)
    {
        try
        {
            _session.Document.SetBackdrops(BackdropFiles.Load(path));
            var problems = BackdropMapping.Problems(_session.Document.Backdrops, _session.Document.ImageBase);
            _session.SetStatus($"Loaded the backdrop setup {Path.GetFileName(path)}." + (problems.Count > 0 ? " " + string.Join(" ", problems) : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            _session.SetStatus($"Can't load the backdrop setup: {e.Message}");
        }
        Refresh();
    }

    private async Task SaveSetupAsk()
    {
        if (AskSetup != null && await AskSetup(true) is { } path) SaveSetup(path);
    }

    private async Task LoadSetupAsk()
    {
        if (AskSetup != null && await AskSetup(false) is { } path) LoadSetup(path);
    }

    /// <summary>Shows the view's backdrop's settings afresh.</summary>
    public void Refresh()
    {
        _syncing = true;
        try
        {
            var b = Current;
            if (!ImageBox.IsFocused) ImageBox.Text = b?.Image ?? "";
            if (b == null) Info.Text = $"The {View} view has no backdrop.";
            else
            {
                var image = TextureImages.Load(b.Image, _session.Document.ImageBase);
                var (w, h) = BackdropMapping.Size(b, image.Width, image.Height);
                Info.Text = image.IsPlaceholder ? image.Problem : $"{image.Width} × {image.Height} pixels, {Format(w)} × {Format(h)} m.";
            }
            foreach (var r in _refresh) r();
        }
        finally { _syncing = false; }
    }
}
