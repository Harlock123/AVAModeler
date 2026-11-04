using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;

namespace AVAModeler.UI.Tests;

/// <summary>The Backdrop panel: reference images behind the flat views, set up by hand and kept for each object.</summary>
public class BackdropUITests
{
    private static string Image(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "images", name));

    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avamodeler-ui-backdrop-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
        }
    }

    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Ui.Pump();
    }

    private static void Press(Control panel, string name)
    {
        panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
    }

    [AvaloniaFact]
    public void D_opens_the_panel_whose_settings_place_the_views_image_live()
    {
        var w = Ui.Open();
        var panel = w.Modeler.Backdrop;
        Assert.False(panel.IsVisible);
        Ui.Key(w, Key.D);
        Assert.True(panel.IsVisible);
        Assert.True(w.Modeler.Views.Views[0].Bounds.Width > 200);
        // The Display tab and the View menu have it too.
        w.Modeler.Tabs.ShowTab("Display");
        Assert.Contains("Backdrop…", w.Modeler.Tabs.Column.Children.OfType<Button>().Select(b => b.Content as string));
        Assert.Contains("Backdrop", w.Modeler.CommandNames);

        // The side view, with the rocket's side drawing: with nothing modelled yet it's 2 m across at the origin.
        Press(panel, "BackdropSide");
        Assert.Equal(BackdropView.Side, panel.View);
        Assert.Contains("Centre Z, Y (m)", panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        panel.UseImage(Image("rocket-side.png"));
        var doc = w.Session.Document;
        var side = doc.Backdrops.Side!;
        Assert.Equal((Image("rocket-side.png"), 2.0, true), (side.Image, side.Width, side.Visible));
        Assert.StartsWith("360 × 690 pixels", panel.Info.Text);
        Assert.Equal(0, doc.Undo.Count);

        // Typed numbers go straight to the backdrop (and the views).
        Type(panel.Fields["Width"], "1.2");
        Type(panel.Fields["CenterY"], "1.05");
        Type(panel.Fields["Opacity"], "0.8");
        Type(panel.Fields["Contrast"], "9"); // kept to its range
        side = doc.Backdrops.Side!;
        Assert.Equal((1.2, new Vec2(0, 1.05), 0.8, 4.0), (side.Width, side.Center, side.Opacity, side.Contrast));
        Assert.Equal("2.3", panel.Fields["Height"].Text);
        Assert.False(panel.Fields["Height"].IsEnabled);
        panel.Checks["KeepShape"].IsChecked = false;
        Ui.Pump();
        Assert.Equal(2.3, doc.Backdrops.Side!.Height!.Value, 9);
        Assert.True(panel.Fields["Height"].IsEnabled);
        Type(panel.Fields["Height"], "3");
        Assert.Equal(3, doc.Backdrops.Side!.Height!.Value);
        panel.Checks["Invert"].IsChecked = true;
        Ui.Pump();
        Assert.True(doc.Backdrops.Side!.Invert);

        // The side view draws it: its middle is the drawing's paper, not the plain grey.
        var view = Ui.View(w, ViewKind.Side);
        view.FitAll();
        Ui.Pump();
        Assert.NotNull(w.CaptureRenderedFrame());
        var pixels = view.Software!.Pixels;
        Assert.Contains(pixels, p => p != Palette.Background && p != Palette.Grid && p != Palette.GridMajor && p != Palette.Axis);

        // Fit to Object and Size from Pixels.
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 1.0, 2, 1 } });
        Press(panel, "BackdropFit");
        Assert.Equal(new Vec2(0, 0), doc.Backdrops.Side!.Center);
        Type(panel.Fields["PixelSize"], "10");
        Press(panel, "BackdropFromPixels");
        Assert.Equal((3.6, (double?)null), (doc.Backdrops.Side!.Width, doc.Backdrops.Side.Height));

        // A missing image shows the checker and says so; Clear takes it away.
        Type(panel.ImageBox, "/nowhere/no-such-blueprint.png");
        Assert.Contains("Can't find the image no-such-blueprint.png", w.Session.Status);
        Assert.Contains("Can't find", panel.Info.Text);
        Press(panel, "BackdropClear");
        Assert.Null(doc.Backdrops.Side);
        Assert.Equal("The Side view has no backdrop.", panel.Info.Text);
        Assert.False(panel.Fields["Width"].IsEnabled);
        Ui.Key(w, Key.D);
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void Setups_save_and_load_and_each_object_gets_back_the_backdrops_it_had()
    {
        using var t = new Temp();
        var w = Ui.Open();
        // The memory in a folder of the test's own, never the real settings.
        w.Session.BackdropMemory = new BackdropMemory(t.File("settings/backdrops.json"));
        var panel = w.Modeler.Backdrop;
        w.Modeler.Execute("Backdrop");
        panel.ShowView(BackdropView.Top);
        panel.UseImage(Image("rocket-top.png"));
        panel.ShowView(BackdropView.Front);
        panel.UseImage(Image("rocket-front.png"));
        var set = w.Session.Document.Backdrops;

        // Save Setup and Load Setup.
        panel.SaveSetup(t.File("rocket.backdrops.json"));
        Assert.Contains("Saved the backdrop setup", w.Session.Status);
        w.Session.Commands.ClearBackdrop();
        panel.LoadSetup(t.File("rocket.backdrops.json"));
        Assert.Equal(set, w.Session.Document.Backdrops);
        File.WriteAllText(t.File("junk.json"), "{");
        panel.LoadSetup(t.File("junk.json"));
        Assert.Contains("Can't load the backdrop setup", w.Session.Status);
        Assert.Equal(set, w.Session.Document.Backdrops);

        // Saved as an object, its backdrops are remembered for its file (not in it).
        w.Session.Commands.Run("Box");
        string box = t.File("box.lwo");
        w.Session.Commands.Save(box);
        w.Session.Document.FilePath = box;
        w.Session.RememberBackdrops();
        Assert.Equal(set, w.Session.BackdropMemory.Recall(box));
        // A new object keeps the blueprints; changing them is remembered only for a saved object.
        w.Session.Commands.New();
        Assert.Equal(set, w.Session.Document.Backdrops);
        w.Session.SetBackdrop(BackdropView.Top, null);
        Assert.Equal(set, w.Session.BackdropMemory.Recall(box));
        Assert.Null(w.Session.Document.Backdrops.Top);
        // Opening the box brings its own back (through the session, as File › Open does, without its recent-files list).
        w.Session.Replace(AVAModeler.Formats.ObjectFiles.Load(box));
        Ui.Pump();
        Assert.Equal(set, w.Session.Document.Backdrops);
        Assert.Equal(box, w.Session.Document.FilePath);
        // Changed while it's open, the change is remembered at once.
        w.Session.SetBackdrop(BackdropView.Front, set.Front! with { Opacity = 0.25 });
        Assert.Equal(0.25, new BackdropMemory(t.File("settings/backdrops.json")).Recall(box)!.Front!.Opacity);
        Assert.False(File.Exists(Preferences.BackdropsPath) && File.ReadAllText(Preferences.BackdropsPath).Contains(t.Path, StringComparison.Ordinal));
    }

    private static class TextureRenderingColours
    {
        public static bool IsMagenta(uint p) => ((p >> 16) & 0xFF) > 200 && ((p >> 8) & 0xFF) < 60 && (p & 0xFF) > 180;
    }

    [AvaloniaFact]
    public void Opening_an_object_whose_backdrop_is_missing_says_so()
    {
        using var t = new Temp();
        var w = Ui.Open();
        w.Session.BackdropMemory = new BackdropMemory(t.File("backdrops.json"));
        string obj = t.File("thing.lwo");
        w.Session.Commands.Run("Box");
        w.Session.Commands.Save(obj);
        w.Session.BackdropMemory.Remember(obj, BackdropSet.Empty.With(BackdropView.Front, new Backdrop { Image = t.File("gone.png") }));
        w.Session.Commands.New();
        w.Session.Replace(AVAModeler.Formats.ObjectFiles.Load(obj));
        Ui.Pump();
        // What File › Open puts in the status bar, and the view shows the checker.
        Assert.Contains("Front backdrop: Can't find the image gone.png; a checker stands in for it.", w.Session.ImageProblems());
        var front = Ui.View(w, ViewKind.Front);
        front.FitAll();
        Ui.Pump();
        Assert.NotNull(w.CaptureRenderedFrame());
        Assert.Contains(front.Software!.Pixels, p => TextureRenderingColours.IsMagenta(p));
    }
}
