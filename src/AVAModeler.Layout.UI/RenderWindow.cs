using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// Rendering a frame with the ray tracer: the settings (size, antialiasing, shadows, reflections), a progress bar
/// with Stop, the picture as it finishes, and Save as PNG.
/// </summary>
public sealed class RenderWindow : Window
{
    private readonly LayoutSession _session;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 10 };
    private readonly TextBlock _status = new() { Foreground = Look.Text, FontSize = 11 };
    private CancellationTokenSource? _cancel;
    private RenderImage? _result;

    public RenderWindow(LayoutSession session)
    {
        _session = session;
        Title = "Render";
        Width = 820;
        Height = 700;
        Background = Look.Panel;
        var cam = session.Scene.CurrentCamera;
        var width = new TextBox { Text = (cam?.Width ?? 640).ToString(CultureInfo.InvariantCulture), Width = 70 };
        var height = new TextBox { Text = (cam?.Height ?? 480).ToString(CultureInfo.InvariantCulture), Width = 70 };
        var samples = new ComboBox { ItemsSource = new[] { "1", "4", "9", "16" }, SelectedItem = "4" };
        var shadows = new CheckBox { Content = "Shadows", IsChecked = true, Foreground = Look.Text };
        var reflections = new CheckBox { Content = "Reflections", IsChecked = true, Foreground = Look.Text };
        var transparency = new CheckBox { Content = "Transparency", IsChecked = true, Foreground = Look.Text };
        var render = new Button { Content = "Render" };
        var stop = new Button { Content = "Stop", IsEnabled = false };
        var save = new Button { Content = "Save PNG…", IsEnabled = false };
        var withAlpha = new CheckBox { Content = "Transparent backdrop", IsChecked = false, Foreground = Look.Text };
        render.Click += async (_, _) =>
        {
            if (!int.TryParse(width.Text, out int w) || !int.TryParse(height.Text, out int h) || w < 1 || h < 1 || w > 8192 || h > 8192)
            {
                _status.Text = "The size must be between 1 and 8192.";
                return;
            }
            var settings = new RenderSettings
            {
                Width = w, Height = h, SamplesPerPixel = int.Parse((string)samples.SelectedItem!, CultureInfo.InvariantCulture),
                Shadows = shadows.IsChecked == true, Reflections = reflections.IsChecked == true, Transparency = transparency.IsChecked == true,
            };
            render.IsEnabled = save.IsEnabled = false;
            stop.IsEnabled = true;
            await Render(settings);
            render.IsEnabled = true;
            stop.IsEnabled = false;
            save.IsEnabled = _result != null;
        };
        stop.Click += (_, _) => _cancel?.Cancel();
        save.Click += async (_, _) =>
        {
            if (_result == null) return;
            var path = await Dialogs.Save(this, "Save the picture", $"frame{_session.Frame:0000}.png", new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] });
            if (path == null) return;
            try { _result.SavePng(path, withAlpha.IsChecked == true); _status.Text = $"Saved {Path.GetFileName(path)}."; }
            catch (IOException e) { _status.Text = e.Message; }
        };
        Closed += (_, _) => _cancel?.Cancel();
        var bar = new WrapPanel
        {
            Children =
            {
                Label("Size"), width, Label("×"), height, Label("Antialiasing"), samples, shadows, reflections, transparency, render, stop, save, withAlpha,
            },
        };
        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children = { Docked(bar, Dock.Top), Docked(_progress, Dock.Bottom), Docked(_status, Dock.Bottom), new Border { Background = Brushes.Black, Child = _image, Margin = new Thickness(0, 8) } },
        };
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    /// <summary>Renders the current frame on a worker thread, showing progress; Stop cancels.</summary>
    public async Task Render(RenderSettings settings)
    {
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var scene = _session.Scene;
        double frame = _session.Frame;
        var started = DateTime.Now;
        _status.Text = "Rendering…";
        try
        {
            var progress = new Progress<double>(v => _progress.Value = v);
            IProgress<double> report = progress;
            _result = await Task.Run(() => RayTracer.Render(scene, frame, settings, report.Report, token), token);
            _image.Source = ToBitmap(_result);
            _status.Text = $"Frame {frame:0}: {_result.Width} × {_result.Height} in {(DateTime.Now - started).TotalSeconds:0.0} s.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped.";
        }
    }

    /// <summary>A rendered image as a bitmap to show.</summary>
    public static WriteableBitmap ToBitmap(RenderImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        var pixels = img.Opaque();
        using var fb = bmp.Lock();
        for (int y = 0; y < img.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(pixels, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return bmp;
    }
}
