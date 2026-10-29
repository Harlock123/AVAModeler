using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// Render › Render Animation: the settings (frames and step, size, antialiasing, shadows, reflections, transparency,
/// the folder, the formats and frame rate), then each frame as it finishes with a progress bar and Cancel, and at the
/// end where the files are, with Open Folder. The work is <see cref="AnimationRender"/> and <see cref="MovieEncoder"/>
/// in AVAModeler.Layout; this window only gathers the settings and shows how it's going.
/// </summary>
public sealed class AnimationWindow : Window
{
    private readonly LayoutSession _session;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 10 };
    private readonly TextBlock _status = new() { Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _first, _last, _step, _width, _height, _fps, _folder;
    private readonly ComboBox _samples = new() { ItemsSource = new[] { "1", "4", "9", "16" }, SelectedItem = "4" };
    private readonly CheckBox _shadows = Check("Shadows", true), _reflections = Check("Reflections", true), _transparency = Check("Transparency", true);
    private readonly CheckBox _png = Check("PNG frames", true), _mp4 = Check("MP4", false), _gif = Check("GIF", false);
    private readonly Button _render = new() { Content = "Render" }, _cancelButton = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _openFolder = new() { Content = "Open Folder", IsEnabled = false };
    private CancellationTokenSource? _cancel;
    private string? _doneFolder;

    /// <summary>What makes the movies (the ffmpeg on the PATH, unless a test gives another).</summary>
    public MovieEncoder Encoder { get; }

    /// <summary>Asks the person a yes/no question (a dialog box; tests answer it themselves).</summary>
    public Func<string, string, Task<bool>> Confirm { get; set; }

    /// <summary>The render under way, if one is.</summary>
    public Task? Running { get; private set; }

    /// <summary>The last render's frames and movies.</summary>
    public AnimationResult? Result { get; private set; }
    public MovieResult? Movies { get; private set; }

    public string Status => _status.Text ?? "";

    public AnimationWindow(LayoutSession session, MovieEncoder? encoder = null)
    {
        _session = session;
        Encoder = encoder ?? MovieEncoder.FromPath();
        Confirm = (title, text) => Dialogs.Ask(this, title, text, "Go ahead", "Cancel");
        Title = "Render Animation";
        Width = 860;
        Height = 760;
        Background = Look.Panel;
        var s = session.Scene;
        var cam = s.CurrentCamera;
        _first = Box(s.FirstFrame, "First");
        _last = Box(s.LastFrame, "Last");
        _step = Box(1, "Step");
        _width = Box(cam?.Width ?? 640, "Width");
        _height = Box(cam?.Height ?? 480, "Height");
        _fps = new TextBox { Text = s.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture), Width = 60, Name = "Fps" };
        _folder = new TextBox { Text = AnimationRender.DefaultFolder(session.ScenePath), MinWidth = 420, Name = "Folder" };
        _samples.Name = "Samples";
        _png.IsEnabled = false;
        _mp4.Name = "Mp4";
        _gif.Name = "Gif";
        var formatNote = new TextBlock { Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        if (!Encoder.Available)
        {
            _mp4.IsEnabled = _gif.IsEnabled = false;
            formatNote.Text = "MP4 and GIF need ffmpeg, which isn't installed (or isn't on the PATH).";
        }
        else _mp4.IsChecked = true;
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await Browse();
        _render.Click += async (_, _) => await Start();
        _cancelButton.Click += (_, _) => _cancel?.Cancel();
        _openFolder.Click += (_, _) => { if (_doneFolder != null) OpenFolder(_doneFolder); };
        Closed += (_, _) => _cancel?.Cancel();

        var settings = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Row(Label("Frames"), _first, Label("to"), _last, Label("step"), _step, Label("Frames per second"), _fps),
                Row(Label("Size"), _width, Label("×"), _height, Label("Antialiasing"), _samples, _shadows, _reflections, _transparency),
                Row(Label("Folder"), _folder, browse),
                Row(Label("Save"), _png, _mp4, _gif, formatNote),
                Row(_render, _cancelButton, _openFolder),
            },
        };
        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children = { Docked(settings, Dock.Top), Docked(_progress, Dock.Bottom), Docked(_status, Dock.Bottom), new Border { Background = Brushes.Black, Child = _image, Margin = new Thickness(0, 8) } },
        };
    }

    private static CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, Foreground = Look.Text, Name = text.Split(' ')[0] };

    private static TextBox Box(int value, string name) => new() { Text = value.ToString(CultureInfo.InvariantCulture), Width = 70, Name = name };

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

    private static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var c in controls) { c.Margin = c.Margin == default ? new Thickness(2, 0) : c.Margin; row.Children.Add(c); }
        return row;
    }

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    private async Task Browse()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The folder for the frames", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) _folder.Text = path;
    }

    /// <summary>The settings as filled in, or null (with the reason in the status line) if they won't do.</summary>
    public AnimationSettings? ReadSettings()
    {
        static bool Int(TextBox b, out int v) => int.TryParse(b.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (!Int(_first, out int first) || !Int(_last, out int last) || !Int(_step, out int step) || !Int(_width, out int w) || !Int(_height, out int h))
        {
            _status.Text = "The frames, step and size must be whole numbers.";
            return null;
        }
        var settings = new AnimationSettings
        {
            First = first, Last = last, Step = step, Width = w, Height = h,
            Samples = int.Parse((string)_samples.SelectedItem!, CultureInfo.InvariantCulture),
            Shadows = _shadows.IsChecked == true, Reflections = _reflections.IsChecked == true, Transparency = _transparency.IsChecked == true,
            Folder = (_folder.Text ?? "").Trim(),
        };
        if (settings.Problem() is { } problem)
        {
            _status.Text = problem;
            return null;
        }
        return settings;
    }

    private double? ReadFps() =>
        double.TryParse(_fps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0 && fps <= 1000 ? fps : null;

    /// <summary>Starts rendering with the settings shown (after any confirmations); <see cref="Running"/> is the work.</summary>
    public Task Start()
    {
        if (Running is { IsCompleted: false }) return Running;
        Running = Go();
        return Running;
    }

    private async Task Go()
    {
        if (ReadSettings() is not { } settings) return;
        if (ReadFps() is not { } fps)
        {
            _status.Text = "The frames per second must be a number above 0.";
            return;
        }
        var existing = AnimationRender.Existing(settings);
        if (existing.Count > 0 && !await Confirm("Replace the frames?",
                $"{existing.Count} of the frames are already in {settings.Folder}. Rendering replaces them."))
            return;
        long bytes = AnimationRender.EstimatedBytes(settings);
        if (bytes > AnimationRender.LargeOutput && !await Confirm("A very large render",
                string.Create(CultureInfo.InvariantCulture, $"{settings.Frames.Count} frames at {settings.Width} × {settings.Height} come to about {bytes / 1e9:0.#} GB of PNGs. Render them anyway?")))
            return;

        AnimationRender job;
        try { job = new AnimationRender(_session.Scene, settings); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _status.Text = e.Message;
            return;
        }
        var formats = new List<MovieFormat>();
        if (_mp4.IsChecked == true && Encoder.Available) formats.Add(MovieFormat.Mp4);
        if (_gif.IsChecked == true && Encoder.Available) formats.Add(MovieFormat.Gif);

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _render.IsEnabled = _openFolder.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _doneFolder = null;
        Result = null;
        Movies = null;
        _progress.Value = 0;
        _status.Text = $"Rendering {settings.Frames.Count} frames…";
        bool asked = false, finished = false;
        var progress = new Progress<AnimationProgress>(p =>
        {
            if (finished || token.IsCancellationRequested) return;
            _progress.Value = p.Fraction;
            _status.Text = p.Text;
            if (p.Image != null)
            {
                _image.Source = RenderWindow.ToBitmap(p.Image);
                if (p.Index == 0 && !asked && p.Count > 1)
                {
                    asked = true;
                    var total = AnimationRender.Estimate(p.Elapsed, 1, p.Count);
                    if (total > AnimationRender.LongRender) _ = AskToCarryOn(total);
                }
            }
        });
        try
        {
            var result = Result = await job.RunAsync(progress, token);
            finished = true;
            string where = settings.Folder;
            if (result.Cancelled)
                _status.Text = $"Cancelled: {result.Files.Count} of {settings.Frames.Count} frames are saved in {where}.";
            else
            {
                _status.Text = $"Rendered {result.Files.Count} frames in {AnimationRender.Duration(result.Elapsed)}.";
                if (formats.Count > 0)
                {
                    _status.Text += " Making the movie…";
                    var name = string.IsNullOrEmpty(_session.ScenePath) ? "animation" : Path.GetFileNameWithoutExtension(_session.ScenePath);
                    var movies = Movies = await Encoder.EncodeAsync(settings.Folder, settings.Name, fps, result.Files.Count, settings.Width, settings.Height, formats, name, token);
                    _status.Text = $"Rendered {result.Files.Count} frames in {AnimationRender.Duration(result.Elapsed)}";
                    _status.Text += movies.Files.Count > 0 ? $" and made {string.Join(" and ", movies.Files.Select(Path.GetFileName))}." : ".";
                    if (movies.Problem != null) _status.Text += " " + movies.Problem;
                }
                _status.Text += $" They're in {where}.";
                var missing = _session.ImageProblems();
                if (missing.Count > 0) _status.Text += " " + string.Join(" ", missing);
            }
            if (!Encoder.Available && !result.Cancelled) _status.Text += " " + MovieEncoder.NotInstalled;
            _progress.Value = result.Cancelled ? _progress.Value : 1;
            if (result.Files.Count > 0) _doneFolder = where;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Couldn't save the frames: " + e.Message;
        }
        finally
        {
            _render.IsEnabled = true;
            _cancelButton.IsEnabled = false;
            _openFolder.IsEnabled = _doneFolder != null;
        }
    }

    /// <summary>After the first frame: this will take a long time; carry on? (It goes on rendering while asking.)</summary>
    private async Task AskToCarryOn(TimeSpan total)
    {
        var cancel = _cancel;
        if (!await Confirm("A long render", $"At the speed of the first frame, the whole animation will take about {AnimationRender.Duration(total)}. Carry on?"))
            cancel?.Cancel();
    }

    /// <summary>Shows a folder in the system's file manager; if that fails, nothing happens.</summary>
    public static void OpenFolder(string folder)
    {
        string opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        try
        {
            var start = new ProcessStartInfo(opener) { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }
}
