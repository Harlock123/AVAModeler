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
    private readonly ComboBox _printers = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _materials = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly ComboBox _pattern = Choice(Enum.GetNames<InfillPattern>());
    private readonly ComboBox _seam = Choice(Enum.GetNames<SeamPosition>());
    private readonly ComboBox _position = Choice(["Centre on the bed", "Origin at the bed's centre"]);
    private readonly ComboBox _layFlat = Choice(["As modelled", "Largest face down", "Selected face down"]);
    private readonly CheckBox _supports = new() { Content = "Supports under overhangs", Foreground = Look.Text };
    private readonly CheckBox _gapFill = new() { Content = "Fill gaps between walls", Foreground = Look.Text };
    private readonly CheckBox _bridging = new() { Content = "Bridge spans over air", Foreground = Look.Text };
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
    /// <summary>The summary, the warnings, or what's wrong with the settings.</summary>
    public TextBlock Status { get; } = new() { Foreground = Look.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    public Button SliceButton { get; }
    public Button SaveButton { get; }
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
        Background = Look.Panel;

        SliceButton = Look.MakeButton("Slice", () => _ = Slice(), "Slice the foreground layers with these settings and show the layers.", 100);
        SaveButton = Look.MakeButton("Save G-code…", () => _ = SaveGCode(), "Slice if need be, then save the G-code for the printer.", 120);
        SliceButton.Name = "Slice";
        SaveButton.Name = "SaveGCode";
        var savePrinter = Look.MakeButton("Save as my printer", SavePrinter, "Keep these printer settings under the name above, beside the presets.");
        var saveMaterial = Look.MakeButton("Save as my material", SaveMaterial, "Keep these material settings under the name above, beside the presets.");

        _printers.SelectionChanged += (_, _) => { if (!_filling && _printers.SelectedIndex >= 0) FillPrinter(_setup.AllPrinters.ElementAt(_printers.SelectedIndex)); };
        _materials.SelectionChanged += (_, _) => { if (!_filling && _materials.SelectedIndex >= 0) FillMaterial(_setup.AllMaterials.ElementAt(_materials.SelectedIndex)); };
        _layer.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) ShowLayer(); };

        var printer = Section("Printer", _printers,
            Field("printer.name", "Name"), Field("printer.width", "Bed width (mm)"), Field("printer.depth", "Bed depth (mm)"), Field("printer.height", "Build height (mm)"),
            Field("printer.nozzle", "Nozzle (mm)"), Field("printer.filament", "Filament (mm)"),
            Label("Start G-code ({bed_temp}, {nozzle_temp}… filled in)"), _start, Label("End G-code"), _end, savePrinter);
        var material = Section("Material", _materials,
            Field("material.name", "Name"), Field("material.nozzle", "Nozzle (°C)"), Field("material.first", "First layer nozzle (°C)"), Field("material.bed", "Bed (°C)"),
            Field("material.retract", "Retraction (mm)"), Field("material.retractSpeed", "Retraction speed (mm/s)"), Field("material.fan", "Fan (%)"),
            Field("material.fanFrom", "Fan from layer"), Field("material.density", "Density (g/cm³)"),
            Field("material.minLayer", "Shortest layer time (s)"), Field("material.minSpeed", "Slowest cooling speed (mm/s)"), saveMaterial);
        var print = Section("Print", null,
            Field("layer", "Layer height (mm)"), Field("first", "First layer (mm)"), Field("width", "Line width (mm)"), Field("walls", "Walls"),
            Field("top", "Top layers"), Field("bottom", "Bottom layers"), Field("infill", "Infill (%)"), Row("Infill pattern", _pattern), Field("angle", "Infill angle (°)"),
            Row("Seam", _seam), _gapFill, _bridging, _combing, Field("skirt", "Skirt loops"), Field("brim", "Brim lines"), _supports, Field("supportAngle", "Support angle (°)"),
            Field("speed", "Print speed (mm/s)"), Field("outer", "Outer wall speed (mm/s)"), Field("infillSpeed", "Infill speed (mm/s)"),
            Field("firstSpeed", "First layer speed (mm/s)"), Field("bridgeSpeed", "Bridge speed (mm/s)"), Field("gapSpeed", "Gap fill speed (mm/s)"),
            Field("travel", "Travel speed (mm/s)"), Field("zhop", "Z hop (mm)"),
            Field("scale", "Scale (%)"), Row("Way up", _layFlat), Field("turn", "Turn on the bed (°)"), Row("Position", _position),
            Field("copies", "Copies"), Field("gap", "Gap between copies (mm)"));

        var left = new ScrollViewer
        {
            Width = 340,
            Content = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 12, 0), Children = { print, material, printer } },
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { SliceButton, SaveButton } };
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
                new Border { Background = Look.PanelDark, Child = Preview, Margin = new Thickness(0, 0, 0, 6) },
            },
        };
        Content = new DockPanel { Margin = new Thickness(12), Children = { Dock(left, Avalonia.Controls.Dock.Left), right } };
        Fill(_setup);
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
        try
        {
            string temp = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, job.GCode, new UTF8Encoding(false));
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Can't save {Path.GetFileName(path)}: {e.Message}";
            Status.Foreground = Brushes.Orange;
            return false;
        }
    }

    public static readonly FilePickerFileType GCodeFile = new("G-code") { Patterns = ["*.gcode", "*.gco", "*.g"] };

    private async Task SaveGCode()
    {
        string name = Path.GetFileNameWithoutExtension(_session.Document.FilePath ?? "untitled") + ".gcode";
        var path = await Dialogs.Save(this, "Save G-code", name, GCodeFile);
        if (path != null) await SaveTo(path);
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
        _pattern.SelectedIndex = (int)s.Infill;
        _seam.SelectedIndex = (int)s.Seam;
        _supports.IsChecked = s.Supports;
        _position.SelectedIndex = (int)setup.Placement.Position;
        _layFlat.SelectedIndex = (int)setup.Placement.LayFlat;
        Set("turn", setup.Placement.RotateDegrees); Set("copies", setup.Placement.Copies); Set("gap", setup.Placement.CopyGap);
    }

    private void FillPrinter(PrinterProfile p)
    {
        _fields["printer.name"].Text = p.Name;
        Set("printer.width", p.BedWidth); Set("printer.depth", p.BedDepth); Set("printer.height", p.MaxHeight);
        Set("printer.nozzle", p.NozzleDiameter); Set("printer.filament", p.FilamentDiameter);
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
            PrintSpeed = Num("speed", "The print speed"), OuterWallSpeed = Num("outer", "The outer wall speed"), InfillSpeed = Num("infillSpeed", "The infill speed"),
            FirstLayerSpeed = Num("firstSpeed", "The first layer speed"), TravelSpeed = Num("travel", "The travel speed"), ZHop = Num("zhop", "The Z hop"),
            BridgeSpeed = Num("bridgeSpeed", "The bridge speed"), GapFillSpeed = Num("gapSpeed", "The gap fill speed"),
            GapFill = _gapFill.IsChecked == true, Bridging = _bridging.IsChecked == true, Combing = _combing.IsChecked == true,
        };
        var placement = new PlacementOptions
        {
            ScalePercent = Num("scale", "The scale"), Position = (BedPosition)Math.Max(0, _position.SelectedIndex),
            LayFlat = (LayFlat)Math.Max(0, _layFlat.SelectedIndex), RotateDegrees = Num("turn", "The turn"),
            Copies = Whole("copies", "Copies"), CopyGap = Num("gap", "The gap between copies"),
        };
        return (_setup with { Printer = printer, Material = material, Settings = settings, Placement = placement }).Checked();
    }

    private PrinterProfile ReadPrinter() => new PrinterProfile
    {
        Name = (_fields["printer.name"].Text ?? "").Trim(), BedWidth = Num("printer.width", "The bed width"), BedDepth = Num("printer.depth", "The bed depth"),
        MaxHeight = Num("printer.height", "The build height"), NozzleDiameter = Num("printer.nozzle", "The nozzle"), FilamentDiameter = Num("printer.filament", "The filament"),
        StartGCode = _start.Text ?? "", EndGCode = _end.Text ?? "",
    }.Checked();

    private MaterialProfile ReadMaterial() => new MaterialProfile
    {
        Name = (_fields["material.name"].Text ?? "").Trim(), NozzleTemp = Whole("material.nozzle", "The nozzle temperature"),
        FirstLayerNozzleTemp = Whole("material.first", "The first layer's temperature"), BedTemp = Whole("material.bed", "The bed temperature"),
        RetractLength = Num("material.retract", "The retraction"), RetractSpeed = Num("material.retractSpeed", "The retraction speed"),
        FanPercent = Whole("material.fan", "The fan"), FanFromLayer = Whole("material.fanFrom", "The fan's first layer"), Density = Num("material.density", "The density"),
        MinLayerSeconds = Num("material.minLayer", "The shortest layer time"), MinSpeed = Num("material.minSpeed", "The slowest cooling speed"),
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
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(58, 58, 64)), bed);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(74, 74, 82)), 1);
        for (double x = 10; x < printer.BedWidth; x += 10) context.DrawLine(grid, ToScreen(new Vec2(x, 0), printer), ToScreen(new Vec2(x, printer.BedDepth), printer));
        for (double y = 10; y < printer.BedDepth; y += 10) context.DrawLine(grid, ToScreen(new Vec2(0, y), printer), ToScreen(new Vec2(printer.BedWidth, y), printer));
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(120, 120, 130)), 1), bed);
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
