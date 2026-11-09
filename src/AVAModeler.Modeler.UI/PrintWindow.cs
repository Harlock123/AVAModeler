using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAModeler.Core;
using AVAModeler.Slicing;

namespace AVAModeler.Modeler.UI;

/// <summary>
/// File › 3D Print: the printer, the material and the print settings down the left, a top-down view of one layer's
/// toolpaths on the bed on the right with a slider through the layers, Slice and Save G-code. Slicing runs on a worker
/// thread from a copy of the object, so the Modeler stays usable. The setup is kept in the preferences each time the
/// object is sliced. A thin view of <see cref="PrintJob"/>: the work is in AVAModeler.Slicing.
/// </summary>
public sealed class PrintWindow : Window
{
    private readonly ModelerSession _session;
    private PrintSetup _setup;
    private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    public ComboBox Profiles => _profiles;
    private readonly ComboBox _printers = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 480 };
    private readonly ComboBox _materials = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly ComboBox _pattern = Choice(Enum.GetNames<InfillPattern>());
    private readonly ComboBox _seam = Choice(Enum.GetNames<SeamPosition>());
    private readonly ComboBox _position = Choice(["Centre on the bed", "Origin at the bed's centre"]);
    private readonly ComboBox _layFlat = Choice(["As modelled", "Largest face down", "Selected face down"]);
    private readonly ComboBox _supportOn = Choice(["On the bed or the object", "On the bed only"]);
    private readonly CheckBox _supports = new() { Content = "Supports under overhangs", Foreground = Look.Text };
    private readonly ComboBox _hostKind = Choice(["None", "OctoPrint", "Moonraker (Klipper)"]);
    private readonly TextBox _hostKey = new() { PasswordChar = '•', Width = 150, FontSize = 12, Name = "host.key" };
    private readonly CheckBox _hostStart = new() { Content = "Start printing once it's there", Foreground = Look.Text };
    /// <summary>Uploads to the printer: the person's button, the G-code bytes; tests give a fake server's client.</summary>
    public Func<HttpClient> HttpFactory { get; set; } = () => SharedHttp;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
    /// <summary>Where the printer's API key is kept (a file beside the preferences; nowhere for a window made for tests).</summary>
    public PrintHostKey HostKey { get; set; }
    public Button SendButton { get; }
    public ComboBox HostKindBox => _hostKind;
    public TextBox HostKeyBox => _hostKey;
    public CheckBox HostStartBox => _hostStart;
    private readonly ComboBox _modelExtruder = Choice(["Extruder 1", "Extruder 2"]);
    private readonly ComboBox _supportExtruder = Choice(["The part's", "Extruder 1", "Extruder 2"]);
    private readonly ComboBox _supportMaterial = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _primeTower = new() { Content = "Prime tower (each extruder primes after a change)", Foreground = Look.Text };
    public CheckBox PrimeTowerBox => _primeTower;
    private Control? _dualRows;
    public ComboBox ModelExtruderBox => _modelExtruder;
    public ComboBox SupportExtruderBox => _supportExtruder;
    public ComboBox SupportMaterialBox => _supportMaterial;
    private readonly CheckBox _originCentre = new() { Content = "Origin at the bed's middle", Foreground = Look.Text };
    private readonly CheckBox _roundBed = new() { Content = "Round bed (a delta: width is its diameter)", Foreground = Look.Text };
    /// <summary>The chosen printer's notes (its firmware's quirks), under its name.</summary>
    public TextBlock PrinterNotes { get; } = new() { Foreground = Brushes.Khaki, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private string _notes = "";
    private TextBox? _profileNameBox;
    public TextBox ProfileNameBox => _profileNameBox!;
    private string _materialNotes = "";
    /// <summary>The chosen material's notes (what it needs), under its name.</summary>
    public TextBlock MaterialNotes { get; } = new() { Foreground = Brushes.Khaki, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private string? _x3gMachine;
    private readonly CheckBox _gapFill = new() { Content = "Fill gaps between walls", Foreground = Look.Text };
    private readonly CheckBox _bridging = new() { Content = "Bridge spans over air", Foreground = Look.Text };
    private readonly CheckBox _adaptive = new() { Content = "Adaptive layer heights (thin on slopes)", Foreground = Look.Text };
    public CheckBox AdaptiveBox => _adaptive;
    private readonly CheckBox _ironing = new() { Content = "Iron top surfaces", Foreground = Look.Text };
    public CheckBox IroningBox => _ironing;
    private readonly CheckBox _combing = new() { Content = "Keep travel inside the object", Foreground = Look.Text };
    private readonly TextBox _start = Script();
    private readonly TextBox _end = Script();
    private readonly Slider _layer = new() { Minimum = 0, Maximum = 0, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 10 };
    private readonly TextBlock _layerText = new() { Foreground = Look.Text, FontSize = 12 };
    private bool _filling;

    /// <summary>What the last slice made (null until something is sliced).</summary>
    public PrintJob? Job { get; private set; }
    /// <summary>The document version and setup the job was sliced from, to tell when it's out of date.</summary>
    private (int Version, PrintSetup Setup)? _slicedFrom;
    public LayerPreview Preview { get; } = new();
    /// <summary>The print in 3D, stacked up to the chosen layer.</summary>
    public PrintPreview3D Preview3D { get; } = new() { IsVisible = false };
    /// <summary>Whether the 3D view is shown (else the top view of one layer).</summary>
    public bool Show3D
    {
        get => Preview3D.IsVisible;
        set
        {
            Preview3D.IsVisible = value;
            Preview.IsVisible = !value;
            Look.SetOn(_topButton, !value);
            Look.SetOn(_threeDButton, value);
        }
    }
    private readonly Button _topButton;
    private readonly Button _threeDButton;
    /// <summary>The summary, the warnings, or what's wrong with the settings.</summary>
    public TextBlock Status { get; } = new() { Foreground = Look.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    public Button SliceButton { get; }
    public Button SaveButton { get; }
    /// <summary>Closes the window: there's always one, as a tiling window manager may draw no close box.</summary>
    public Button CloseButton { get; }

    /// <summary>
    /// Shrinks the window to fit the screen it's on (its working area, less a margin) and keeps it inside, so it opens
    /// usable however small or scaled the screen is. The settings scroll, so a smaller window loses nothing.
    /// </summary>
    public void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        double maxW = area.Width / scale - 40, maxH = area.Height / scale - 40;
        if (maxW < MinWidth || maxH < MinHeight) return;
        Width = Math.Min(Width, maxW);
        Height = Math.Min(Height, maxH);
        // Back inside the working area if it opened (centred on a large owner) partly off it.
        int w = (int)(Width * scale), h = (int)(Height * scale);
        int x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - w));
        int y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
        if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
    }
    /// <summary>A slice is running.</summary>
    public bool Busy { get; private set; }

    public PrintWindow(ModelerSession session)
    {
        _session = session;
        try { _setup = session.Preferences.Print.Checked(); }
        catch (ArgumentException) { _setup = new PrintSetup(); }
        Title = "3D Print";
        Width = 1040;
        Height = 720;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Panel;
        // A tiling window manager may give the window no title bar or close box: Close and Escape always work.
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) => FitToScreen();

        SliceButton = Look.MakeButton("Slice", () => _ = Slice(), "Slice the foreground layers with these settings and show the layers.", 100);
        SaveButton = Look.MakeButton("Save G-code…", () => _ = SaveGCode(), "Slice if need be, then save the G-code for the printer.", 120);
        SendButton = Look.MakeButton("Send to printer", () => _ = Send(), "Slice if need be, then upload the G-code to OctoPrint or Moonraker (set below, under Send to printer).", 120);
        SendButton.Name = "Send";
        HostKey = PrintHostKey.In(session.Preferences.Location is { } loc ? Path.GetDirectoryName(loc) : null);
        SliceButton.Name = "Slice";
        SaveButton.Name = "SaveGCode";
        var savePrinter = Look.MakeButton("Save as my printer", SavePrinter, "Keep these printer settings under the name above, beside the presets.");
        var saveMaterial = Look.MakeButton("Save as my material", SaveMaterial, "Keep these material settings under the name above, beside the presets.");

        _printers.SelectionChanged += (_, _) => { if (!_filling && _printers.SelectedIndex >= 0) FillPrinter(_setup.AllPrinters.ElementAt(_printers.SelectedIndex)); };
        _profiles.SelectionChanged += (_, _) =>
        {
            if (_filling || _profiles.SelectedItem is not string name) return;
            // Over what's in the fields now, keeping this object's supports, brim, skirt and extruders.
            PrintSetup current;
            try { current = Read(); }
            catch (ArgumentException) { current = _setup; }
            try
            {
                _setup = current.WithProfile(name);
                Fill(_setup);
                Status.Text = $"The {name} profile: change any setting, or Save as my profile.";
                Status.Foreground = Look.Text;
            }
            catch (ArgumentException e) { Complain(e.Message); }
        };
        var profileName = new TextBox { Width = 150, FontSize = 12, Name = "profile.name", PlaceholderText = "My profile's name" };
        _profileNameBox = profileName;
        var saveProfile = Look.MakeButton("Save as my profile", () =>
        {
            try
            {
                _setup = Read().SaveProfile(profileName.Text ?? "");
                Remember($"Saved the print profile \"{_setup.ProfileName}\".");
            }
            catch (ArgumentException e) { Complain(e.Message); }
        }, "Keep these print settings under the name beside it, among the profiles.");
        _materials.SelectionChanged += (_, _) => { if (!_filling && _materials.SelectedIndex >= 0) FillMaterial(_setup.AllMaterials.ElementAt(_materials.SelectedIndex)); };
        _layer.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) ShowLayer(); };

        var printer = Section("Printer", _printers,
            Field("printer.name", "Name"), Field("printer.width", "Bed width (mm)"), Field("printer.depth", "Bed depth (mm)"), Field("printer.height", "Build height (mm)"),
            PrinterNotes, Field("printer.nozzle", "Nozzle (mm)"), Field("printer.filament", "Filament (mm)"),
            Field("printer.retract", "Retraction (mm, blank: the material's)"), Field("printer.accel", "Acceleration (mm/s²)"),
            Field("printer.corner", "Corner speed (mm/s)"), Field("printer.extruders", "Extruders (1 or 2)"), _originCentre, _roundBed,
            Label("Start G-code ({bed_temp}, {nozzle_temp}… filled in)"), _start, Label("End G-code"), _end, savePrinter);
        var material = Section("Material", _materials,
            MaterialNotes, Field("material.name", "Name"), Field("material.nozzle", "Nozzle (°C)"), Field("material.first", "First layer nozzle (°C)"), Field("material.bed", "Bed (°C)"),
            Field("material.retract", "Retraction (mm)"), Field("material.retractSpeed", "Retraction speed (mm/s)"), Field("material.fan", "Fan (%)"),
            Field("material.fanFrom", "Fan from layer"), Field("material.density", "Density (g/cm³)"),
            Field("material.minLayer", "Shortest layer time (s)"), Field("material.minSpeed", "Slowest cooling speed (mm/s)"), saveMaterial);
        var print = Section("Print", _profiles, Row("", profileName), saveProfile,
            Field("layer", "Layer height (mm)"), Field("first", "First layer (mm)"), _adaptive,
            Field("adaptMin", "Thinnest layer (mm)"), Field("adaptMax", "Thickest layer (mm)"), Field("adaptCusp", "Largest step on a slope (mm)"), Field("width", "Line width (mm)"), Field("walls", "Walls"),
            Field("top", "Top layers"), Field("bottom", "Bottom layers"), Field("infill", "Infill (%)"), Row("Infill pattern", _pattern), Field("angle", "Infill angle (°)"),
            Row("Seam", _seam), _gapFill, _bridging, _ironing, Field("ironFlow", "Ironing flow (%)"), _combing, Field("skirt", "Skirt loops"), Field("brim", "Brim lines"), _supports, Field("supportAngle", "Support angle (°)"),
            Row("Supports stand", _supportOn), (_dualRows = new StackPanel
            {
                Spacing = 3,
                Children = { Row("Part's extruder", _modelExtruder), Row("Supports' extruder", _supportExtruder), Row("Supports' material", _supportMaterial), _primeTower, Field("tower", "Prime tower size (mm)") },
            }), Field("interface", "Interface layers"), Field("interfacePercent", "Interface density (%)"),
            Field("speed", "Print speed (mm/s)"), Field("outer", "Outer wall speed (mm/s)"), Field("infillSpeed", "Infill speed (mm/s)"),
            Field("firstSpeed", "First layer speed (mm/s)"), Field("bridgeSpeed", "Bridge speed (mm/s)"), Field("gapSpeed", "Gap fill speed (mm/s)"),
            Field("travel", "Travel speed (mm/s)"), Field("zhop", "Z hop (mm)"),
            Field("scale", "Scale (%)"), Row("Way up", _layFlat), Field("turn", "Turn on the bed (°)"), Row("Position", _position),
            Field("copies", "Copies"), Field("gap", "Gap between copies (mm)"));

        var send = Section("Send to printer", null,
            Row("Server", _hostKind), Field("host.url", "Address"), Row("API key", _hostKey), _hostStart,
            new TextBlock { Text = "The key is kept in its own file beside the preferences, readable only by you.", Foreground = Look.Text, FontSize = 10, TextWrapping = TextWrapping.Wrap });
        var left = new ScrollViewer
        {
            Width = 340,
            Content = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 12, 0), Children = { print, material, printer, send } },
        };
        _topButton = Look.MakeButton("Layer", () => Show3D = false, "One layer from above.", 70);
        _threeDButton = Look.MakeButton("3D", () => Show3D = true, "The print in 3D up to the chosen layer: drag to turn, right-drag to move, scroll to zoom.", 50);
        CloseButton = Look.MakeButton("Close", Close, "Close the 3D Print window (Esc). The settings are kept.", 70);
        CloseButton.Name = "Close";
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { SliceButton, SaveButton, SendButton, new Border { Width = 16 }, _topButton, _threeDButton, Look.MakeButton("Fit", () => Preview3D.Refit(), "Frame the print in the 3D view again.", 44) },
        };
        var buttons = new DockPanel { Children = { Dock(CloseButton, Avalonia.Controls.Dock.Right), row } };
        var legend = new WrapPanel();
        foreach (var kind in Enum.GetValues<PathKind>())
            legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Spacing = 4,
                Children = { new Border { Width = 12, Height = 12, Background = LayerPreview.BrushFor(kind) }, new TextBlock { Text = LayerPreview.Describe(kind), Foreground = Look.Text, FontSize = 11 } },
            });
        var right = new DockPanel
        {
            Children =
            {
                Dock(buttons, Avalonia.Controls.Dock.Top),
                Dock(new Border { Margin = new Thickness(0, 6), Child = Status }, Avalonia.Controls.Dock.Top),
                Dock(legend, Avalonia.Controls.Dock.Bottom),
                Dock(new DockPanel { Children = { Dock(_layerText, Avalonia.Controls.Dock.Right), _layer } }, Avalonia.Controls.Dock.Bottom),
                new Border { Background = Look.PanelDark, Child = new Panel { Children = { Preview, Preview3D } }, Margin = new Thickness(0, 0, 0, 6) },
            },
        };
        Content = new DockPanel { Margin = new Thickness(12), Children = { Dock(left, Avalonia.Controls.Dock.Left), right } };
        Fill(_setup);
        Show3D = false;
        Version();
        Closed += (_, _) => { if (_watched != null) _watched.Changed -= Changed; };
        Status.Text = "Set up the print, then Slice. The foreground layers are printed: 1 m in the Modeler is 1000 mm on the bed (see Scale).";
    }

    // ───────────────────────────── slicing ─────────────────────────────

    /// <summary>Reads the settings and slices a copy of the object on a worker thread. False if the settings are wrong.</summary>
    public async Task<bool> Slice()
    {
        if (Busy) return false;
        PrintSetup setup;
        try { setup = Read(); }
        catch (ArgumentException e)
        {
            Status.Text = e.Message;
            Status.Foreground = Brushes.Orange;
            return false;
        }
        _setup = setup;
        _session.Preferences.Print = setup;
        _session.Preferences.Save();
        var copy = _session.Document.Copy();
        int version = Version();
        Busy = true;
        SliceButton.IsEnabled = SaveButton.IsEnabled = false;
        Status.Foreground = Look.Text;
        Status.Text = "Slicing…";
        try
        {
            var job = await Task.Run(() => PrintJob.Run(copy, setup));
            Job = job;
            _slicedFrom = (version, setup);
            Preview.Job = job;
            Preview3D.Job = job;
            _layer.Maximum = Math.Max(0, job.Plan.Layers.Count - 1);
            _layer.Value = Math.Min(_layer.Value, _layer.Maximum);
            if (job.Plan.Layers.Count > 0 && _layer.Value == 0) _layer.Value = Math.Min(_layer.Maximum, job.Plan.Layers.Count / 2);
            ShowLayer();
            var text = new StringBuilder(job.Model.Triangles.Count == 0 ? "Nothing to print." : job.Stats.Summary);
            if (job.Model.Triangles.Count > 0)
            {
                var size = job.Model.Bounds.Size;
                text.Append(CultureInfo.InvariantCulture, $" · {size.X:0.#} × {size.Y:0.#} × {size.Z:0.#} mm");
            }
            foreach (var w in job.Warnings) text.Append('\n').Append(w);
            Status.Text = text.ToString();
            Status.Foreground = job.Warnings.Count > 0 ? Brushes.Orange : Look.Text;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Status.Text = e.Message;
            Status.Foreground = Brushes.Orange;
            return false;
        }
        finally
        {
            Busy = false;
            SliceButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Whether the job is for the object and settings as they are now.</summary>
    public bool UpToDate
    {
        get
        {
            if (Job == null || _slicedFrom is not { } from) return false;
            try { return from.Version == Version() && from.Setup.ToJson() == Read().ToJson(); }
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>Counts the document's changes (and a different document, after New or Open, counts as one).</summary>
    private int Version()
    {
        if (!ReferenceEquals(_watched, _session.Document))
        {
            if (_watched != null) _watched.Changed -= Changed;
            _watched = _session.Document;
            _watched.Changed += Changed;
            _changes++;
        }
        return _changes;
    }

    private Document? _watched;
    private int _changes;
    private void Changed() => _changes++;

    /// <summary>Slices if the job is out of date, then writes its G-code to <paramref name="path"/>.</summary>
    public async Task<bool> SaveTo(string path)
    {
        if (!UpToDate && !await Slice()) return false;
        if (Job is not { } job || job.Model.Triangles.Count == 0)
        {
            Status.Text = "There's nothing to print: the foreground layers have no faces.";
            Status.Foreground = Brushes.Orange;
            return false;
        }
        bool x3g = Path.GetExtension(path).Equals(".x3g", StringComparison.OrdinalIgnoreCase);
        if (x3g && job.Setup.Printer.X3gMachine == null)
        {
            Status.Text = $"{job.Setup.Printer.Name} doesn't read .x3g: save .gcode, or choose a Sailfish printer such as the Flashforge Creator Pro.";
            Status.Foreground = Brushes.Orange;
            return false;
        }
        try
        {
            string temp = path + ".tmp";
            try
            {
                if (x3g)
                {
                    var bytes = X3g.Convert(job.GCode, SailfishMachine.ById[job.Setup.Printer.X3gMachine!], Path.GetFileNameWithoutExtension(path));
                    await File.WriteAllBytesAsync(temp, bytes);
                }
                else await File.WriteAllTextAsync(temp, job.GCode, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            Status.Text = $"Saved {Path.GetFileName(path)}: {job.Stats.Summary}" + string.Concat(job.Warnings.Select(w => "\n" + w));
            _session.SetStatus($"Saved the G-code {Path.GetFileName(path)} ({job.Stats.Time}, {job.Stats.Grams:0.0} g).");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Status.Text = $"Can't save {Path.GetFileName(path)}: {e.Message}";
            Status.Foreground = Brushes.Orange;
            return false;
        }
    }

    /// <summary>
    /// Slices if need be, then uploads the G-code to the printer's OctoPrint or Moonraker (and starts it if asked). The
    /// key typed in is kept (in its own file) when the upload works. Returns whether it was sent.
    /// </summary>
    public async Task<bool> Send()
    {
        if (!UpToDate && !await Slice()) return false;
        if (Job is not { } job || job.Model.Triangles.Count == 0)
        {
            Complain("There's nothing to print: the foreground layers have no faces.");
            return false;
        }
        var host = job.Setup.Host;
        string typed = _hostKey.Text ?? "";
        string? key = typed.Length > 0 ? typed : HostKey.Get();
        string name = Path.GetFileNameWithoutExtension(_session.Document.FilePath ?? "untitled") + ".gcode";
        SendButton.IsEnabled = false;
        Status.Foreground = Look.Text;
        Status.Text = $"Sending {name}…";
        try
        {
            string said = await PrintHostClient.SendAsync(host, key, name, new UTF8Encoding(false).GetBytes(job.GCode), HttpFactory());
            if (typed.Length > 0)
            {
                HostKey.Set(typed);
                _hostKey.Text = "";
            }
            if (job.Setup.Printer.X3gMachine != null) said += " (Its Sailfish firmware reads .x3g: OctoPrint needs its GPX plugin to print G-code on it.)";
            Status.Text = said;
            _session.SetStatus(said);
            return true;
        }
        catch (Exception e) when (e is PrintHostException or ArgumentException)
        {
            Complain(e.Message);
            return false;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    public static readonly FilePickerFileType GCodeFile = new("G-code") { Patterns = ["*.gcode", "*.gco", "*.g"] };
    public static readonly FilePickerFileType X3gFile = new("Sailfish .x3g (MakerBot-style printers)") { Patterns = ["*.x3g"] };

    private async Task SaveGCode()
    {
        // A Sailfish printer reads .x3g: offer it first.
        bool sailfish = _setup.Printer.X3gMachine != null || ReadPrinterOrNull()?.X3gMachine != null;
        string name = Path.GetFileNameWithoutExtension(_session.Document.FilePath ?? "untitled") + (sailfish ? ".x3g" : ".gcode");
        var path = sailfish
            ? await Dialogs.Save(this, "Save for the printer", name, X3gFile, GCodeFile)
            : await Dialogs.Save(this, "Save G-code", name, GCodeFile);
        if (path != null) await SaveTo(path);
    }

    private PrinterProfile? ReadPrinterOrNull()
    {
        try { return ReadPrinter(); }
        catch (ArgumentException) { return null; }
    }

    // ───────────────────────────── the settings ─────────────────────────────

    /// <summary>The field's text box, by its key (for tests and the filling code).</summary>
    public TextBox FieldBox(string key) => _fields[key];

    public ComboBox Printers => _printers;
    public ComboBox Materials => _materials;
    public CheckBox SupportsBox => _supports;
    public CheckBox CombingBox => _combing;
    public ComboBox LayFlatBox => _layFlat;

    private void Fill(PrintSetup setup)
    {
        _filling = true;
        try
        {
            _profiles.ItemsSource = setup.AllProfiles.Select(p => p.Name).ToList();
            _profiles.SelectedIndex = setup.ProfileName == null ? -1 : setup.AllProfiles.ToList().FindIndex(p => p.Name == setup.ProfileName);
            _printers.ItemsSource = setup.AllPrinters.Select(p => p.Name).ToList();
            _materials.ItemsSource = setup.AllMaterials.Select(m => m.Name).ToList();
            _printers.SelectedIndex = Math.Max(0, setup.AllPrinters.ToList().FindIndex(p => p.Name == setup.Printer.Name));
            _materials.SelectedIndex = Math.Max(0, setup.AllMaterials.ToList().FindIndex(m => m.Name == setup.Material.Name));
        }
        finally { _filling = false; }
        FillPrinter(setup.Printer);
        FillMaterial(setup.Material);
        var s = setup.Settings;
        Set("layer", s.LayerHeight); Set("first", s.FirstLayerHeight); Set("width", s.LineWidth); Set("walls", s.Walls);
        Set("top", s.TopLayers); Set("bottom", s.BottomLayers); Set("infill", s.InfillPercent); Set("angle", s.InfillAngle);
        Set("skirt", s.SkirtLoops); Set("brim", s.BrimLines); Set("supportAngle", s.SupportAngle);
        Set("speed", s.PrintSpeed); Set("outer", s.OuterWallSpeed); Set("infillSpeed", s.InfillSpeed); Set("firstSpeed", s.FirstLayerSpeed);
        Set("travel", s.TravelSpeed); Set("zhop", s.ZHop); Set("scale", setup.Placement.ScalePercent);
        Set("bridgeSpeed", s.BridgeSpeed); Set("gapSpeed", s.GapFillSpeed);
        _gapFill.IsChecked = s.GapFill;
        _bridging.IsChecked = s.Bridging;
        _combing.IsChecked = s.Combing;
        _ironing.IsChecked = s.Ironing;
        _adaptive.IsChecked = s.AdaptiveLayers;
        Set("adaptMin", s.AdaptiveMin); Set("adaptMax", s.AdaptiveMax); Set("adaptCusp", s.AdaptiveCusp);
        Set("ironFlow", s.IroningFlowPercent);
        _pattern.SelectedIndex = (int)s.Infill;
        _seam.SelectedIndex = (int)s.Seam;
        _supports.IsChecked = s.Supports;
        _modelExtruder.SelectedIndex = s.ModelExtruder;
        _primeTower.IsChecked = s.PrimeTower;
        Set("tower", s.PrimeTowerSize);
        _supportExtruder.SelectedIndex = s.SupportExtruder + 1;
        _supportMaterial.ItemsSource = setup.AllMaterials.Select(m => m.Name).ToList();
        _supportMaterial.SelectedIndex = Math.Max(0, setup.AllMaterials.ToList().FindIndex(m => m.Name == setup.SupportMaterial.Name));
        _hostKind.SelectedIndex = (int)setup.Host.Kind;
        _fields["host.url"].Text = setup.Host.Url;
        _hostStart.IsChecked = setup.Host.StartPrint;
        _supportOn.SelectedIndex = (int)s.SupportOn;
        Set("interface", s.SupportInterfaceLayers); Set("interfacePercent", s.SupportInterfacePercent);
        _position.SelectedIndex = (int)setup.Placement.Position;
        _layFlat.SelectedIndex = (int)setup.Placement.LayFlat;
        Set("turn", setup.Placement.RotateDegrees); Set("copies", setup.Placement.Copies); Set("gap", setup.Placement.CopyGap);
    }

    private void FillPrinter(PrinterProfile p)
    {
        _fields["printer.name"].Text = p.Name;
        Set("printer.width", p.BedWidth); Set("printer.depth", p.BedDepth); Set("printer.height", p.MaxHeight);
        Set("printer.nozzle", p.NozzleDiameter); Set("printer.filament", p.FilamentDiameter);
        _fields["printer.retract"].Text = p.RetractLength is { } r ? r.ToString("0.###", CultureInfo.InvariantCulture) : "";
        Set("printer.accel", p.Acceleration); Set("printer.corner", p.CornerSpeed); Set("printer.extruders", p.Extruders);
        if (_dualRows != null) _dualRows.IsVisible = p.Extruders > 1;
        _originCentre.IsChecked = p.OriginAtCentre;
        _roundBed.IsChecked = p.RoundBed;
        _notes = p.Notes;
        _x3gMachine = p.X3gMachine;
        SaveButton.Content = p.X3gMachine != null ? "Save .x3g…" : "Save G-code…";
        PrinterNotes.Text = p.Notes;
        PrinterNotes.IsVisible = p.Notes.Length > 0;
        _start.Text = p.StartGCode;
        _end.Text = p.EndGCode;
    }

    private void FillMaterial(MaterialProfile m)
    {
        _fields["material.name"].Text = m.Name;
        Set("material.nozzle", m.NozzleTemp); Set("material.first", m.FirstLayerNozzleTemp); Set("material.bed", m.BedTemp);
        Set("material.retract", m.RetractLength); Set("material.retractSpeed", m.RetractSpeed); Set("material.fan", m.FanPercent);
        Set("material.fanFrom", m.FanFromLayer); Set("material.density", m.Density);
        Set("material.minLayer", m.MinLayerSeconds); Set("material.minSpeed", m.MinSpeed);
        _materialNotes = m.Notes;
        MaterialNotes.Text = m.Notes;
        MaterialNotes.IsVisible = m.Notes.Length > 0;
    }

    private void Set(string key, double v) => _fields[key].Text = v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The setup as the fields have it, checked; an <see cref="ArgumentException"/> says what's wrong.</summary>
    public PrintSetup Read()
    {
        var printer = ReadPrinter();
        var material = ReadMaterial();
        var settings = new SliceSettings
        {
            LayerHeight = Num("layer", "The layer height"), FirstLayerHeight = Num("first", "The first layer"), LineWidth = Num("width", "The line width"),
            Walls = Whole("walls", "Walls"), TopLayers = Whole("top", "Top layers"), BottomLayers = Whole("bottom", "Bottom layers"),
            InfillPercent = Num("infill", "Infill"), Infill = (InfillPattern)Math.Max(0, _pattern.SelectedIndex), InfillAngle = Num("angle", "The infill angle"),
            Seam = (SeamPosition)Math.Max(0, _seam.SelectedIndex), SkirtLoops = Whole("skirt", "Skirt loops"), BrimLines = Whole("brim", "Brim lines"),
            Supports = _supports.IsChecked == true, SupportAngle = Num("supportAngle", "The support angle"),
            SupportOn = (SupportPlacement)Math.Max(0, _supportOn.SelectedIndex), SupportInterfaceLayers = Whole("interface", "Interface layers"),
            SupportInterfacePercent = Num("interfacePercent", "The interface density"),
            ModelExtruder = Math.Max(0, _modelExtruder.SelectedIndex), SupportExtruder = Math.Max(0, _supportExtruder.SelectedIndex) - 1,
            PrimeTower = _primeTower.IsChecked == true, PrimeTowerSize = Num("tower", "The prime tower's size"),
            PrintSpeed = Num("speed", "The print speed"), OuterWallSpeed = Num("outer", "The outer wall speed"), InfillSpeed = Num("infillSpeed", "The infill speed"),
            FirstLayerSpeed = Num("firstSpeed", "The first layer speed"), TravelSpeed = Num("travel", "The travel speed"), ZHop = Num("zhop", "The Z hop"),
            BridgeSpeed = Num("bridgeSpeed", "The bridge speed"), GapFillSpeed = Num("gapSpeed", "The gap fill speed"),
            GapFill = _gapFill.IsChecked == true, Bridging = _bridging.IsChecked == true, Combing = _combing.IsChecked == true,
            Ironing = _ironing.IsChecked == true, IroningFlowPercent = Num("ironFlow", "The ironing flow"),
            AdaptiveLayers = _adaptive.IsChecked == true, AdaptiveMin = Num("adaptMin", "The thinnest layer"), AdaptiveMax = Num("adaptMax", "The thickest layer"),
            AdaptiveCusp = Num("adaptCusp", "The largest step on a slope"),
        };
        var placement = new PlacementOptions
        {
            ScalePercent = Num("scale", "The scale"), Position = (BedPosition)Math.Max(0, _position.SelectedIndex),
            LayFlat = (LayFlat)Math.Max(0, _layFlat.SelectedIndex), RotateDegrees = Num("turn", "The turn"),
            Copies = Whole("copies", "Copies"), CopyGap = Num("gap", "The gap between copies"),
        };
        var host = new PrintHost { Kind = (PrintHostKind)Math.Max(0, _hostKind.SelectedIndex), Url = (_fields["host.url"].Text ?? "").Trim(), StartPrint = _hostStart.IsChecked == true };
        var supportMaterial = _supportMaterial.SelectedIndex >= 0 ? _setup.AllMaterials.ElementAt(_supportMaterial.SelectedIndex) : _setup.SupportMaterial;
        // A printer with one extruder prints everything with it, whatever was chosen for another.
        if (printer.Extruders < 2) settings = settings with { ModelExtruder = 0, SupportExtruder = -1 };
        return (_setup with { Printer = printer, Material = material, SupportMaterial = supportMaterial, Settings = settings, Placement = placement, Host = host }).Checked();
    }

    private PrinterProfile ReadPrinter() => new PrinterProfile
    {
        X3gMachine = _x3gMachine, Name = (_fields["printer.name"].Text ?? "").Trim(), BedWidth = Num("printer.width", "The bed width"), BedDepth = Num("printer.depth", "The bed depth"),
        MaxHeight = Num("printer.height", "The build height"), NozzleDiameter = Num("printer.nozzle", "The nozzle"), FilamentDiameter = Num("printer.filament", "The filament"),
        StartGCode = _start.Text ?? "", EndGCode = _end.Text ?? "",
        RetractLength = string.IsNullOrWhiteSpace(_fields["printer.retract"].Text) ? null : Num("printer.retract", "The printer's retraction"),
        OriginAtCentre = _originCentre.IsChecked == true, RoundBed = _roundBed.IsChecked == true, Notes = _notes,
        Acceleration = Num("printer.accel", "The acceleration"), CornerSpeed = Num("printer.corner", "The corner speed"),
        Extruders = Whole("printer.extruders", "The extruders"),
    }.Checked();

    private MaterialProfile ReadMaterial() => new MaterialProfile
    {
        Name = (_fields["material.name"].Text ?? "").Trim(), NozzleTemp = Whole("material.nozzle", "The nozzle temperature"),
        FirstLayerNozzleTemp = Whole("material.first", "The first layer's temperature"), BedTemp = Whole("material.bed", "The bed temperature"),
        RetractLength = Num("material.retract", "The retraction"), RetractSpeed = Num("material.retractSpeed", "The retraction speed"),
        FanPercent = Whole("material.fan", "The fan"), FanFromLayer = Whole("material.fanFrom", "The fan's first layer"), Density = Num("material.density", "The density"),
        MinLayerSeconds = Num("material.minLayer", "The shortest layer time"), MinSpeed = Num("material.minSpeed", "The slowest cooling speed"), Notes = _materialNotes,
    }.Checked();

    private void SavePrinter()
    {
        try
        {
            _setup = _setup.SavePrinter(ReadPrinter());
            Remember($"Saved the printer \"{_setup.Printer.Name}\".");
        }
        catch (ArgumentException e) { Complain(e.Message); }
    }

    private void SaveMaterial()
    {
        try
        {
            _setup = _setup.SaveMaterial(ReadMaterial());
            Remember($"Saved the material \"{_setup.Material.Name}\".");
        }
        catch (ArgumentException e) { Complain(e.Message); }
    }

    private void Remember(string said)
    {
        var settings = _setup;
        try { settings = Read() with { MyPrinters = _setup.MyPrinters, MyMaterials = _setup.MyMaterials }; }
        catch (ArgumentException) { }
        _setup = settings;
        _session.Preferences.Print = _setup;
        _session.Preferences.Save();
        Fill(_setup);
        Status.Text = said;
        Status.Foreground = Look.Text;
    }

    private void Complain(string message)
    {
        Status.Text = message;
        Status.Foreground = Brushes.Orange;
    }

    private double Num(string key, string what)
    {
        string text = (_fields[key].Text ?? "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : throw new ArgumentException($"{what} must be a number (it's \"{text}\").");
    }

    private int Whole(string key, string what)
    {
        double v = Num(key, what);
        return v == Math.Floor(v) && Math.Abs(v) < int.MaxValue ? (int)v : throw new ArgumentException($"{what} must be a whole number (it's {v.ToString(CultureInfo.InvariantCulture)}).");
    }

    // ───────────────────────────── the layer view ─────────────────────────────

    /// <summary>The layer shown, from 0.</summary>
    public int ShownLayer
    {
        get => (int)Math.Round(_layer.Value);
        set => _layer.Value = Math.Clamp(value, 0, (int)_layer.Maximum);
    }

    private void ShowLayer()
    {
        Preview.Layer = ShownLayer;
        Preview3D.Layer = ShownLayer;
        if (Job is { } job && job.Plan.Layers.Count > 0)
        {
            var l = job.Plan.Layers[ShownLayer].Layer;
            _layerText.Text = string.Create(CultureInfo.InvariantCulture, $"Layer {l.Index + 1} of {job.Plan.Layers.Count} · Z {l.Top:0.00} mm");
        }
        else _layerText.Text = "";
    }

    // ───────────────────────────── building the panel ─────────────────────────────

    private Control Field(string key, string label)
    {
        var box = new TextBox { Width = 110, FontSize = 12, Name = key };
        _fields[key] = box;
        return Row(label, box);
    }

    private static Control Row(string label, Control control)
    {
        if (control is not TextBox) control.Width = 150;
        return new DockPanel
        {
            Children =
            {
                Dock(control, Avalonia.Controls.Dock.Right),
                new TextBlock { Text = label, Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private static Control Section(string title, Control? chooser, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Look.Text, FontWeight = FontWeight.Bold, FontSize = 13, Margin = new Thickness(0, 8, 0, 2) });
        if (chooser != null) panel.Children.Add(chooser);
        foreach (var r in rows) panel.Children.Add(r);
        return panel;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBox Script() => new() { AcceptsReturn = true, Height = 90, FontFamily = new FontFamily("monospace"), FontSize = 11, TextWrapping = TextWrapping.NoWrap };

    private static ComboBox Choice(IEnumerable<string> items) => new() { ItemsSource = items.ToList(), SelectedIndex = 0 };

    private static Control Dock(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }
}

/// <summary>
/// One layer of a sliced print seen from above: the bed with a 10 mm grid, the layer below faintly, and this layer's
/// lines in their kinds' colours at their printed width (at least a pixel). The back of the bed is at the top.
/// </summary>
public sealed class LayerPreview : Control
{
    private PrintJob? _job;
    private int _layer;

    public PrintJob? Job
    {
        get => _job;
        set { _job = value; InvalidateVisual(); }
    }

    public int Layer
    {
        get => _layer;
        set { _layer = value; InvalidateVisual(); }
    }

    private static readonly Dictionary<PathKind, IBrush> Colours = new()
    {
        [PathKind.OuterWall] = new SolidColorBrush(Color.FromRgb(255, 96, 64)),
        [PathKind.InnerWall] = new SolidColorBrush(Color.FromRgb(96, 220, 96)),
        [PathKind.Skin] = new SolidColorBrush(Color.FromRgb(255, 214, 74)),
        [PathKind.Infill] = new SolidColorBrush(Color.FromRgb(232, 150, 60)),
        [PathKind.Support] = new SolidColorBrush(Color.FromRgb(90, 200, 240)),
        [PathKind.Skirt] = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
        [PathKind.Brim] = new SolidColorBrush(Color.FromRgb(170, 140, 255)),
        [PathKind.GapFill] = new SolidColorBrush(Color.FromRgb(240, 120, 200)),
        [PathKind.Bridge] = new SolidColorBrush(Color.FromRgb(70, 130, 255)),
        [PathKind.SupportInterface] = new SolidColorBrush(Color.FromRgb(40, 230, 230)),
        [PathKind.Ironing] = new SolidColorBrush(Color.FromRgb(255, 255, 200)),
        [PathKind.PrimeTower] = new SolidColorBrush(Color.FromRgb(160, 120, 90)),
    };

    public static IBrush BrushFor(PathKind kind) => Colours[kind];

    public static string Describe(PathKind kind) => kind switch
    {
        PathKind.OuterWall => "Outer wall",
        PathKind.InnerWall => "Inner wall",
        PathKind.Skin => "Top/bottom",
        PathKind.Infill => "Infill",
        PathKind.Support => "Support",
        PathKind.Skirt => "Skirt",
        PathKind.Brim => "Brim",
        PathKind.GapFill => "Gap fill",
        PathKind.Bridge => "Bridge",
        PathKind.SupportInterface => "Support top",
        PathKind.Ironing => "Ironing",
        PathKind.PrimeTower => "Prime tower",
        _ => kind.ToString(),
    };

    /// <summary>Where a bed point (mm) is drawn in the control, for the bed's size.</summary>
    public Point ToScreen(Vec2 p, PrinterProfile printer)
    {
        var (scale, ox, oy) = Fit(printer);
        return new Point(ox + p.X * scale, oy + (printer.BedDepth - p.Y) * scale);
    }

    private (double Scale, double X, double Y) Fit(PrinterProfile printer)
    {
        double margin = 12;
        double scale = Math.Max(1e-6, Math.Min((Bounds.Width - 2 * margin) / printer.BedWidth, (Bounds.Height - 2 * margin) / printer.BedDepth));
        double ox = (Bounds.Width - printer.BedWidth * scale) / 2, oy = (Bounds.Height - printer.BedDepth * scale) / 2;
        return (scale, ox, oy);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 44)), new Rect(Bounds.Size));
        var printer = _job?.Setup.Printer ?? new PrinterProfile();
        var (scale, ox, oy) = Fit(printer);
        var bed = new Rect(ox, oy, printer.BedWidth * scale, printer.BedDepth * scale);
        var bedFill = new SolidColorBrush(Color.FromRgb(58, 58, 64));
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(120, 120, 130)), 1);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(74, 74, 82)), 1);
        if (printer.RoundBed)
        {
            // A delta's round bed, its grid inside the circle.
            context.DrawEllipse(bedFill, edge, bed.Center, bed.Width / 2, bed.Height / 2);
            double r = printer.BedWidth / 2;
            for (double x = 10; x < printer.BedWidth; x += 10)
            {
                double half = Math.Sqrt(Math.Max(0, r * r - (x - r) * (x - r)));
                context.DrawLine(grid, ToScreen(new Vec2(x, r - half), printer), ToScreen(new Vec2(x, r + half), printer));
                context.DrawLine(grid, ToScreen(new Vec2(r - half, x), printer), ToScreen(new Vec2(r + half, x), printer));
            }
        }
        else
        {
            context.FillRectangle(bedFill, bed);
            for (double x = 10; x < printer.BedWidth; x += 10) context.DrawLine(grid, ToScreen(new Vec2(x, 0), printer), ToScreen(new Vec2(x, printer.BedDepth), printer));
            for (double y = 10; y < printer.BedDepth; y += 10) context.DrawLine(grid, ToScreen(new Vec2(0, y), printer), ToScreen(new Vec2(printer.BedWidth, y), printer));
            context.DrawRectangle(edge, bed);
        }
        if (_job is not { } job || job.Plan.Layers.Count == 0) return;
        int i = Math.Clamp(_layer, 0, job.Plan.Layers.Count - 1);
        double width = Math.Max(1, job.Setup.Settings.LineWidth * scale);
        if (i > 0) Draw(context, job.Plan.Layers[i - 1], printer, _ => new Pen(new SolidColorBrush(Color.FromArgb(70, 200, 200, 210)), width));
        Draw(context, job.Plan.Layers[i], printer, kind => new Pen(Colours[kind], width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
    }

    private void Draw(DrawingContext context, ToolpathLayer layer, PrinterProfile printer, Func<PathKind, IPen> pen)
    {
        foreach (var group in layer.Paths.GroupBy(p => p.Kind))
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                foreach (var path in group)
                {
                    g.BeginFigure(ToScreen(path.Points[0], printer), false);
                    for (int k = 1; k < path.Points.Count; k++) g.LineTo(ToScreen(path.Points[k], printer));
                    g.EndFigure(path.Closed);
                }
            context.DrawGeometry(null, pen(group.Key), geometry);
        }
    }
}
