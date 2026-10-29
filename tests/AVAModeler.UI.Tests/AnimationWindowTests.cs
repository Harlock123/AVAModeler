using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;

namespace AVAModeler.UI.Tests;

/// <summary>Render › Render Animation: opening it, its defaults, and a tiny render from the window.</summary>
public class AnimationWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-animui-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private static T Named<T>(Window w, string name) where T : Control =>
        w.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "The render didn't finish in time.");
        Ui.Pump();
    }

    private static (MainWindow Window, LayoutWorkspace Layout) Layout()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Ball");
        w.ShowLayout();
        Ui.Pump();
        return (w, (LayoutWorkspace)w.Content!);
    }

    [AvaloniaFact]
    public void F10_opens_it_with_the_scenes_frames_size_and_rate()
    {
        var (w, layout) = Layout();
        var s = layout.Session.Scene;
        s.FirstFrame = 5;
        s.LastFrame = 20;
        s.FramesPerSecond = 24;
        s.CurrentCamera!.Width = 320;
        s.CurrentCamera.Height = 180;
        Ui.Key(w, Key.F10);
        var a = Assert.Single(w.OwnedWindows.OfType<AnimationWindow>());
        Assert.Equal("5", Named<TextBox>(a, "First").Text);
        Assert.Equal("20", Named<TextBox>(a, "Last").Text);
        Assert.Equal("1", Named<TextBox>(a, "Step").Text);
        Assert.Equal("320", Named<TextBox>(a, "Width").Text);
        Assert.Equal("180", Named<TextBox>(a, "Height").Text);
        Assert.Equal("24", Named<TextBox>(a, "Fps").Text);
        Assert.Equal(AnimationRender.DefaultFolder(null), Named<TextBox>(a, "Folder").Text);
        Assert.NotNull(a.CaptureRenderedFrame());
        // F10 again brings the same window forward.
        Ui.Key(w, Key.F10);
        Assert.Single(w.OwnedWindows.OfType<AnimationWindow>());
        a.Close();
    }

    [AvaloniaFact]
    public void The_render_menu_opens_it()
    {
        var (w, layout) = Layout();
        var render = layout.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == "_Render");
        var item = render.Items.OfType<MenuItem>().First(m => m.Header as string == "Render _Animation…");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Ui.Pump();
        var a = Assert.Single(w.OwnedWindows.OfType<AnimationWindow>());
        a.Close();
    }

    private AnimationWindow Tiny(LayoutWorkspace layout, MovieEncoder? encoder = null)
    {
        var a = new AnimationWindow(layout.Session, encoder);
        a.Show();
        Named<TextBox>(a, "First").Text = "1";
        Named<TextBox>(a, "Last").Text = "3";
        Named<TextBox>(a, "Width").Text = "32";
        Named<TextBox>(a, "Height").Text = "18";
        Named<ComboBox>(a, "Samples").SelectedItem = "1";
        Named<TextBox>(a, "Folder").Text = _dir;
        return a;
    }

    [AvaloniaFact]
    public void A_tiny_render_from_the_window_writes_the_frames_and_movies()
    {
        var (_, layout) = Layout();
        var a = Tiny(layout);
        if (a.Encoder.Available)
        {
            Assert.True(Named<CheckBox>(a, "Mp4").IsChecked);
            Named<CheckBox>(a, "Gif").IsChecked = true;
        }
        Wait(a.Start());
        Assert.Equal(["frame0001.png", "frame0002.png", "frame0003.png"], a.Result!.Files.Select(Path.GetFileName));
        Assert.All(a.Result.Files, f => Assert.True(File.Exists(f)));
        Assert.Contains("Rendered 3 frames", a.Status);
        Assert.Contains(_dir, a.Status);
        if (a.Encoder.Available)
        {
            Assert.Null(a.Movies!.Problem);
            Assert.True(File.Exists(Path.Combine(_dir, "animation.mp4")));
            Assert.True(File.Exists(Path.Combine(_dir, "animation.gif")));
        }

        // Again: the frames are there, so it asks first; saying no leaves them alone.
        var asked = new List<string>();
        a.Confirm = (title, _) => { asked.Add(title); return Task.FromResult(false); };
        var when = File.GetLastWriteTimeUtc(a.Result.Files[0]);
        Wait(a.Start());
        Assert.Equal(["Replace the frames?"], asked);
        Assert.Equal(when, File.GetLastWriteTimeUtc(Path.Combine(_dir, "frame0001.png")));
        a.Close();
    }

    [AvaloniaFact]
    public void Without_ffmpeg_the_movies_are_off_and_it_says_to_install_it()
    {
        var (_, layout) = Layout();
        var a = Tiny(layout, new MovieEncoder(null));
        Assert.False(Named<CheckBox>(a, "Mp4").IsEnabled);
        Assert.False(Named<CheckBox>(a, "Gif").IsEnabled);
        Assert.True(Named<CheckBox>(a, "PNG").IsChecked);
        Wait(a.Start());
        Assert.Equal(3, a.Result!.Files.Count);
        Assert.Contains("Install ffmpeg", a.Status);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
        a.Close();
    }

    [AvaloniaFact]
    public void Bad_settings_are_explained_and_nothing_renders()
    {
        var (_, layout) = Layout();
        var a = Tiny(layout, new MovieEncoder(null));
        Named<TextBox>(a, "Last").Text = "abc";
        Wait(a.Start());
        Assert.Null(a.Result);
        Assert.Contains("whole numbers", a.Status);
        Assert.False(Directory.Exists(_dir));
        a.Close();
    }
}
