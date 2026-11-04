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
/// Scene › Scene Settings: the frame range and rate, ambient light and backdrop, and the render effects that belong to
/// the scene (ambient occlusion, and the environment picture with its lighting). A thin view: <see cref="Apply"/> reads
/// the fields, checks them with the settings' own rules and makes the change one undo step ("Scene Settings").
/// </summary>
public sealed class SceneSettingsPanel : StackPanel
{
    private readonly LayoutSession _session;
    private readonly TextBlock _problem = new() { Foreground = Look.ButtonOn, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public TextBox First { get; }
    public TextBox Last { get; }
    public TextBox Fps { get; }
    public TextBox Ambient { get; }
    public CheckBox Gradient { get; }
    public CheckBox Occlusion { get; }
    public TextBox OcclusionSamples { get; }
    public TextBox OcclusionDistance { get; }
    public TextBox OcclusionStrength { get; }
    public TextBox Environment { get; }
    public TextBox EnvironmentIntensity { get; }
    public TextBox EnvironmentHeading { get; }
    public CheckBox EnvironmentLighting { get; }
    public TextBox EnvironmentSamples { get; }

    /// <summary>Raised after the settings are applied (the dialog closes).</summary>
    public event Action? Applied;

    public SceneSettingsPanel(LayoutSession session)
    {
        _session = session;
        Spacing = 4;
        Margin = new Thickness(12);
        var s = session.Scene;
        First = Box(s.FirstFrame.ToString(CultureInfo.InvariantCulture));
        Last = Box(s.LastFrame.ToString(CultureInfo.InvariantCulture));
        Fps = Box(F(s.FramesPerSecond));
        Ambient = Box(F(s.AmbientIntensity));
        Gradient = Check("Gradient backdrop", s.GradientBackground);
        var ao = s.Occlusion;
        Occlusion = Check("Darken creases and contact areas", ao.On, "Darkens creases, corners and contact areas by dimming the ambient light where nearby surfaces hide it");
        OcclusionSamples = Box(ao.Samples.ToString(CultureInfo.InvariantCulture));
        OcclusionDistance = Box(F(ao.Distance));
        OcclusionStrength = Box(F(ao.Strength));
        var env = s.Environment;
        Environment = new TextBox { Text = env.Image, FontSize = 11, PlaceholderText = "none: a file, or builtin:sky / builtin:studio" };
        EnvironmentIntensity = Box(F(env.Intensity));
        EnvironmentHeading = Box(F(env.Heading));
        EnvironmentLighting = Check("Light the scene with it", env.Lighting, "Soft light from every direction of the picture, with shadows");
        EnvironmentSamples = Box(env.Samples.ToString(CultureInfo.InvariantCulture));

        var browse = Look.MakeButton("Browse…", () => _ = Browse(), "Choose a latitude-longitude picture (.hdr, .png, .jpg)");
        var envRow = new DockPanel { Children = { Docked(browse, Dock.Right), Environment } };
        var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { if (Apply()) Applied?.Invoke(); };

        Children.Add(Grid2(("First frame", First), ("Last frame", Last), ("Frames per second", Fps), ("Ambient intensity", Ambient)));
        Children.Add(Gradient);
        Children.Add(Heading("Ambient occlusion"));
        Children.Add(Occlusion);
        Children.Add(Grid2(("Samples a pixel", OcclusionSamples), ("Distance (m)", OcclusionDistance), ("Strength (0–1)", OcclusionStrength)));
        Children.Add(Heading("Environment (replaces the backdrop)"));
        Children.Add(envRow);
        Children.Add(Grid2(("Intensity", EnvironmentIntensity), ("Heading °", EnvironmentHeading)));
        Children.Add(EnvironmentLighting);
        Children.Add(Grid2(("Lighting samples a pixel", EnvironmentSamples)));
        Children.Add(new TextBlock
        {
            Text = "Samples are rays a pixel, shared among its antialiasing rays. Depth of field and motion blur are the camera's; soft shadows the lights'.",
            Foreground = Look.Text, FontSize = 10, TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
        });
        Children.Add(_problem);
        Children.Add(ok);
    }

    /// <summary>What was wrong with the fields the last time they were applied, if anything.</summary>
    public string? Problem => string.IsNullOrEmpty(_problem.Text) ? null : _problem.Text;

    /// <summary>
    /// Checks the fields and, if they're all right, sets them as one undo step. Returns whether it did; if not,
    /// <see cref="Problem"/> says what's wrong and the scene is untouched.
    /// </summary>
    public bool Apply()
    {
        var s = _session.Scene;
        try
        {
            int first = Whole(First, "The first frame"), last = Math.Max(Whole(Last, "The last frame"), first);
            double fps = Number(Fps, "Frames per second");
            if (fps <= 0) throw new ArgumentException("Frames per second must be more than 0.");
            double ambient = Math.Max(0, Number(Ambient, "The ambient intensity"));
            var ao = new AmbientOcclusion
            {
                On = Occlusion.IsChecked == true, Samples = Whole(OcclusionSamples, "Ambient occlusion samples"),
                Distance = Number(OcclusionDistance, "The occlusion distance"), Strength = Number(OcclusionStrength, "The occlusion strength"),
            }.Checked();
            var env = new EnvironmentSettings
            {
                Image = (Environment.Text ?? "").Trim(), Intensity = Number(EnvironmentIntensity, "The environment's intensity"),
                Heading = Number(EnvironmentHeading, "The environment's heading"), Lighting = EnvironmentLighting.IsChecked == true,
                Samples = Whole(EnvironmentSamples, "Environment samples"),
            }.Checked();
            if (env.On && EnvironmentMap.Load(env.Image, null, out var problem) == null) throw new ArgumentException(problem);
            _session.Edit("Scene Settings", () =>
            {
                (s.FirstFrame, s.LastFrame, s.FramesPerSecond, s.AmbientIntensity) = (first, last, fps, ambient);
                s.GradientBackground = Gradient.IsChecked == true;
                (s.Occlusion, s.Environment) = (ao, env);
                s.Notify();
            });
            _problem.Text = "";
            return true;
        }
        catch (ArgumentException e)
        {
            _problem.Text = e.Message;
            return false;
        }
    }

    private async Task Browse()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var path = await Dialogs.Open(top, "Choose an environment picture", new FilePickerFileType("Environment pictures") { Patterns = ["*.hdr", "*.pic", "*.png", "*.jpg", "*.jpeg", "*.bmp"] });
        if (path != null) Environment.Text = path;
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static double Number(TextBox box, string what) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : throw new ArgumentException($"{what} must be a number.");

    private static int Whole(TextBox box, string what) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw new ArgumentException($"{what} must be a whole number.");

    private static TextBox Box(string text) => new() { Text = text, FontSize = 11 };

    private static CheckBox Check(string text, bool on, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Foreground = Look.Text };
        if (tip != null) ToolTip.SetTip(c, tip);
        return c;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) };

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    /// <summary>Labels and fields in two columns.</summary>
    private static Grid Grid2(params (string Label, Control Field)[] rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions(string.Join(",", rows.Select(_ => "Auto"))) };
        for (int i = 0; i < rows.Length; i++)
        {
            var label = new TextBlock { Text = rows[i].Label, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            Grid.SetRow(label, i);
            Grid.SetRow(rows[i].Field, i);
            Grid.SetColumn(rows[i].Field, 1);
            rows[i].Field.Margin = new Thickness(0, 2);
            g.Children.Add(label);
            g.Children.Add(rows[i].Field);
        }
        return g;
    }
}
