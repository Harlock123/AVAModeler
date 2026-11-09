using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Slicing;

namespace AVAModeler.UI.Tests;

/// <summary>File › 3D Print: the settings, slicing on a worker, the layer view and saving G-code.</summary>
public sealed class PrintWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-print-" + Guid.NewGuid().ToString("N"));

    public PrintWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static T Wait<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "Slicing didn't finish in time.");
        Ui.Pump();
        return task.Result;
    }

    /// <summary>The window with a 1 m box in the Modeler, printed at 2%: a 20 mm cube.</summary>
    private static (MainWindow Main, PrintWindow Print) Open()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box");
        w.Modeler.Execute("3D Print");
        Ui.Pump();
        var p = Assert.Single(w.OwnedWindows.OfType<PrintWindow>());
        p.FieldBox("scale").Text = "2";
        return (w, p);
    }

    [AvaloniaFact]
    public void The_file_menu_and_the_objects_tab_open_one_print_window_with_the_presets()
    {
        var (w, p) = Open();
        Assert.Equal("3D Print", p.Title);
        Assert.Equal(Presets.Printers.Select(x => x.Name), p.Printers.ItemsSource!.Cast<string>());
        Assert.Equal(["PLA", "PETG", "ABS", "TPU"], p.Materials.ItemsSource!.Cast<string>());
        Assert.Equal("0.2", p.FieldBox("layer").Text);
        Assert.Equal("220", p.FieldBox("printer.width").Text);
        // Asking again brings the same window forward; the Objects tab has the button too.
        w.Modeler.Execute("3D Print");
        Assert.Single(w.OwnedWindows.OfType<PrintWindow>());
        w.Modeler.Tabs.ShowTab("Objects");
        Ui.Pump();
        Assert.Contains(w.Modeler.Tabs.Column.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == "3D Print…");
        Assert.NotNull(p.CaptureRenderedFrame());
        // Its own Close button (a tiling window manager may draw no close box), and Escape.
        Press(p, "Close");
        Ui.Pump();
        Assert.Null(w.Modeler.Print);
        Assert.Empty(w.OwnedWindows.OfType<PrintWindow>());
        w.Modeler.Execute("3D Print");
        Ui.Pump();
        var again = Assert.Single(w.OwnedWindows.OfType<PrintWindow>());
        again.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        Ui.Pump();
        Assert.Null(w.Modeler.Print);
    }

    [AvaloniaFact]
    public void Slicing_shows_the_layers_and_what_the_print_takes()
    {
        var (w, p) = Open();
        Assert.True(Wait(p.Slice()));
        var job = p.Job!;
        Assert.Equal(100, job.Plan.Layers.Count);
        Assert.Contains("100 layers · ", p.Status.Text);
        Assert.Contains("20 × 20 × 20 mm", p.Status.Text);
        Assert.True(p.UpToDate);
        Assert.True(p.SliceButton.IsEnabled && p.SaveButton.IsEnabled);
        // The view starts halfway up and the slider moves it.
        Assert.Equal(50, p.ShownLayer);
        Assert.Equal(50, p.Preview.Layer);
        p.ShownLayer = 0;
        Assert.Equal(0, p.Preview.Layer);
        Assert.NotNull(p.CaptureRenderedFrame());
        // The setup is kept in the preferences.
        Assert.Equal(2, w.Session.Preferences.Print.Placement.ScalePercent);
        Assert.True(w.Session.Preferences.Print.Settings.Combing);
        p.CombingBox.IsChecked = false;
        Assert.False(p.UpToDate);
        p.CombingBox.IsChecked = true;
        // Four copies: two rows of two.
        p.FieldBox("copies").Text = "4";
        Assert.True(Wait(p.Slice()));
        Assert.Contains("45 × 45 × 20 mm", p.Status.Text);
        Assert.Equal(4, p.Job!.Sliced.Layers[0].Islands.Count);
        // Selected face down with nothing selected says what to do.
        p.LayFlatBox.SelectedIndex = 2;
        Assert.False(Wait(p.Slice()));
        Assert.Contains("Select a polygon", p.Status.Text);
        p.LayFlatBox.SelectedIndex = 0;
        p.FieldBox("copies").Text = "1";
        Assert.True(Wait(p.Slice()));
        // Changing the object or a setting makes it out of date.
        p.FieldBox("infill").Text = "40";
        Assert.False(p.UpToDate);
        p.FieldBox("infill").Text = "20";
        Assert.True(p.UpToDate);
        w.Session.Commands.Run("Box");
        Assert.False(p.UpToDate);
        p.Close();
    }

    [AvaloniaFact]
    public void The_3D_view_stacks_the_layers_up_to_the_chosen_one()
    {
        var (_, p) = Open();
        Assert.False(p.Show3D);
        Assert.True(Wait(p.Slice()));
        Press(p, "3D");
        Assert.True(p.Show3D);
        Assert.True(p.Preview3D.IsVisible);
        Assert.False(p.Preview.IsVisible);
        p.ShownLayer = 0;
        Assert.NotNull(p.CaptureRenderedFrame());
        int low = p.Preview3D.Segments;
        p.ShownLayer = 30;
        Assert.NotNull(p.CaptureRenderedFrame());
        int mid = p.Preview3D.Segments;
        Assert.True(mid > low * 3, $"{mid} against {low}");
        // Far below the chosen layer only the outer walls are drawn, so a tall print stays quick.
        var job = p.Job!;
        var lines = PrintPreview3D.Lines(job, 99, job.Setup.Printer);
        var all = PrintPreview3D.Lines(job, 99, job.Setup.Printer).Count;
        int everything = job.Plan.Layers.Sum(l => l.Paths.Sum(e => e.Points.Count));
        Assert.True(all < everything, $"{all} lines, of {everything} points");
        // The chosen layer's lines are at its height, in metres in the renderer's space (Y up).
        double top = job.Plan.Layers[99].Layer.Top / 1000;
        Assert.Contains(lines, l => Math.Abs(l.A.Y - top) < 1e-12);
        Assert.Equal(PrintPreview3D.ToScene(1, 2, 3), new Vec3(0.001, 0.003, 0.002));
        // Dragging turns it; Layer goes back.
        double heading = p.Preview3D.Camera.Heading;
        p.Preview3D.Camera.Orbit(20, 0);
        Assert.NotEqual(heading, p.Preview3D.Camera.Heading);
        Press(p, "Layer");
        Assert.False(p.Show3D);
        p.Close();
    }

    [AvaloniaFact]
    public void Choosing_a_printer_on_the_market_fills_in_its_bed_and_says_what_to_know()
    {
        var (w, p) = Open();
        var names = p.Printers.ItemsSource!.Cast<string>().ToList();
        Assert.True(names.Count > 40);
        p.Printers.SelectedIndex = names.IndexOf("Flashforge Creator Pro");
        Assert.Equal("227", p.FieldBox("printer.width").Text);
        Assert.Equal("148", p.FieldBox("printer.depth").Text);
        Assert.True(p.PrinterNotes.IsVisible);
        Assert.Contains("GPX", p.PrinterNotes.Text);
        Assert.True(Wait(p.Slice()));
        Assert.Equal("Flashforge Creator Pro", w.Session.Preferences.Print.Printer.Name);
        Assert.True(w.Session.Preferences.Print.Printer.OriginAtCentre);
        Assert.Contains("G162 X Y", p.Job!.GCode);
        // It saves .x3g, which its Sailfish firmware reads.
        Assert.Equal("Save .x3g…", p.SaveButton.Content);
        string x3g = Path.Combine(_dir, "cube.x3g");
        Assert.True(Wait(p.SaveTo(x3g)));
        Assert.Equal(153, File.ReadAllBytes(x3g)[0]);
        // A delta: round, and its round bed drawn.
        p.Printers.SelectedIndex = names.IndexOf("FLSUN Q5");
        Assert.Equal("200", p.FieldBox("printer.width").Text);
        Assert.True(Wait(p.Slice()));
        Assert.True(p.Job!.Setup.Printer.RoundBed);
        Assert.NotNull(p.CaptureRenderedFrame());
        p.Show3D = true;
        Assert.NotNull(p.CaptureRenderedFrame());
        // A generic one has no notes; a blank retraction keeps the material's.
        p.Printers.SelectedIndex = 0;
        Assert.False(p.PrinterNotes.IsVisible);
        Assert.Equal("Save G-code…", p.SaveButton.Content);
        Assert.False(Wait(p.SaveTo(Path.Combine(_dir, "generic.x3g"))));
        Assert.Contains("doesn't read .x3g", p.Status.Text);
        Assert.Equal("", p.FieldBox("printer.retract").Text);
        p.Close();
    }

    [AvaloniaFact]
    public void Send_to_printer_uploads_the_gcode_and_keeps_the_key_apart()
    {
        var (w, p) = Open();
        var server = new FakeServer();
        p.HttpFactory = () => new HttpClient(server);
        // No server chosen: it says what to do.
        Assert.False(Wait(p.Send()));
        Assert.Contains("Choose OctoPrint or Moonraker", p.Status.Text);
        p.HostKindBox.SelectedIndex = 1;
        p.FieldBox("host.url").Text = "http://octopi.local";
        p.HostKeyBox.Text = "SECRETKEY1234";
        p.HostStartBox.IsChecked = true;
        Assert.True(Wait(p.Send()));
        Assert.Equal("Sent untitled.gcode to octopi.local, and it's printing.", p.Status.Text);
        Assert.Equal("http://octopi.local/api/files/local", server.Uri);
        Assert.Equal("SECRETKEY1234", server.Key);
        Assert.Contains(";FLAVOR:Marlin", server.Form);
        // The key moved out of the box into the window's store (no file for a window made for tests), never into the preferences.
        Assert.Equal("", p.HostKeyBox.Text);
        Assert.Equal("SECRETKEY1234", p.HostKey.Get());
        Assert.Equal(PrintHostKind.OctoPrint, w.Session.Preferences.Print.Host.Kind);
        Assert.DoesNotContain("SECRETKEY", System.Text.Json.JsonSerializer.Serialize(w.Session.Preferences));
        p.Close();
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string Uri = "", Key = "", Form = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Uri = request.RequestUri!.ToString();
            Key = request.Headers.TryGetValues("X-Api-Key", out var k) ? k.Single() : "";
            Form = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
        }
    }

    [AvaloniaFact]
    public void Bad_settings_say_what_is_wrong_and_slice_nothing()
    {
        var (_, p) = Open();
        p.FieldBox("layer").Text = "thick";
        Assert.False(Wait(p.Slice()));
        Assert.Equal("The layer height must be a number (it's \"thick\").", p.Status.Text);
        p.FieldBox("layer").Text = "0.6";
        Assert.False(Wait(p.Slice()));
        Assert.Contains("0.32 mm", p.Status.Text);
        p.FieldBox("layer").Text = "0.2";
        p.FieldBox("walls").Text = "2.5";
        Assert.False(Wait(p.Slice()));
        Assert.Contains("whole number", p.Status.Text);
        Assert.Null(p.Job);
        p.Close();
    }

    [AvaloniaFact]
    public void Saving_gcode_slices_first_and_writes_the_file()
    {
        var (w, p) = Open();
        p.SupportsBox.IsChecked = true;
        string path = Path.Combine(_dir, "cube.gcode");
        Assert.True(Wait(p.SaveTo(path)));
        string text = File.ReadAllText(path);
        Assert.StartsWith(";FLAVOR:Marlin", text);
        Assert.Contains(", supports", text);
        Assert.Contains("Saved cube.gcode", p.Status.Text);
        Assert.Contains("Saved the G-code cube.gcode", w.Session.Status);
        Assert.Equal(["cube.gcode"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        p.Close();
    }

    [AvaloniaFact]
    public void A_printer_and_a_material_of_your_own_are_kept_beside_the_presets()
    {
        var (w, p) = Open();
        p.FieldBox("printer.name").Text = "Generic 220 × 220";
        p.FieldBox("printer.width").Text = "180";
        Press(p, "Save as my printer");
        Assert.Contains("preset's name", p.Status.Text);
        p.FieldBox("printer.name").Text = "Workshop printer";
        Press(p, "Save as my printer");
        Assert.Equal("Saved the printer \"Workshop printer\".", p.Status.Text);
        Assert.Equal("Workshop printer", p.Printers.SelectedItem);
        Assert.Equal(180, Assert.Single(w.Session.Preferences.Print.MyPrinters).BedWidth);
        p.FieldBox("material.name").Text = "Silk PLA";
        p.FieldBox("material.nozzle").Text = "215";
        Press(p, "Save as my material");
        Assert.Equal("Silk PLA", p.Materials.SelectedItem);
        // Choosing a preset fills its numbers in; the new window starts from what was kept.
        p.Printers.SelectedIndex = 0;
        Assert.Equal("220", p.FieldBox("printer.width").Text);
        p.Close();
        w.Modeler.Execute("3D Print");
        Ui.Pump();
        var again = Assert.Single(w.OwnedWindows.OfType<PrintWindow>());
        Assert.Contains("Workshop printer", again.Printers.ItemsSource!.Cast<string>());
        Assert.Equal("Silk PLA", again.Materials.SelectedItem);
        Assert.Equal("215", again.FieldBox("material.nozzle").Text);
        again.Close();
    }

    [AvaloniaFact]
    public void An_open_mesh_and_an_empty_object_are_reported()
    {
        var (w, p) = Open();
        w.Session.Commands.SelectPolygons([5]);
        w.Session.Commands.Run("Remove");
        Assert.True(Wait(p.Slice()));
        Assert.Contains("The mesh check says: Open", p.Status.Text);
        w.Session.Commands.SelectPolygons([0, 1, 2, 3, 4]);
        w.Session.Commands.Run("Remove");
        Assert.False(Wait(p.SaveTo(Path.Combine(_dir, "none.gcode"))));
        Assert.Contains("nothing to print", p.Status.Text);
        Assert.Empty(Directory.GetFiles(_dir));
        p.Close();
    }

    private static void Press(Window w, string label)
    {
        var b = w.GetLogicalDescendants().OfType<Button>().Single(x => x.Content as string == label);
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
    }
}
