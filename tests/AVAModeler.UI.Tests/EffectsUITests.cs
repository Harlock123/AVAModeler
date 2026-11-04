using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>The render effects' controls: Scene Settings' panel, and the camera's and lights' settings in Layout.</summary>
public class EffectsUITests
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } d ? d : null;

    [AvaloniaFact]
    public void Scene_settings_set_ambient_occlusion_and_the_environment_as_one_undo_step()
    {
        var session = new LayoutSession();
        var panel = new SceneSettingsPanel(session);
        var w = new Window { Width = 380, Height = 700, Content = panel, Background = Look.Panel };
        w.Show();
        Ui.Pump();
        Assert.False(panel.Occlusion.IsChecked);
        Assert.Equal("", panel.Environment.Text);

        panel.Occlusion.IsChecked = true;
        panel.OcclusionSamples.Text = "32";
        panel.OcclusionDistance.Text = "0.5";
        panel.Environment.Text = "builtin:studio";
        panel.EnvironmentLighting.IsChecked = true;
        panel.EnvironmentIntensity.Text = "0.6";
        panel.EnvironmentHeading.Text = "45";
        panel.Ambient.Text = "0.1";
        Assert.True(panel.Apply());
        var s = session.Scene;
        Assert.Equal(new AmbientOcclusion { On = true, Samples = 32, Distance = 0.5 }, s.Occlusion);
        Assert.Equal(new EnvironmentSettings { Image = "builtin:studio", Lighting = true, Intensity = 0.6, Heading = 45 }, s.Environment);
        Assert.Equal(0.1, s.AmbientIntensity);
        Assert.Equal("Scene Settings", session.History.UndoLabel);

        if (Folder is { } dir)
        {
            Ui.Pump();
            var frame = w.CaptureRenderedFrame();
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "AVAModeler-render-settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        session.Undo();
        Assert.False(s.Occlusion.On);
        Assert.False(s.Environment.On);

        // A bad value or a missing picture is said, and changes nothing.
        var again = new SceneSettingsPanel(session);
        again.OcclusionDistance.Text = "-1";
        Assert.False(again.Apply());
        Assert.Contains("distance", again.Problem);
        again.OcclusionDistance.Text = "1";
        again.Environment.Text = "/nowhere/sky.hdr";
        Assert.False(again.Apply());
        Assert.Contains("Can't find the environment image", again.Problem);
        Assert.False(s.Environment.On);
        w.Close();
    }

    private static LayoutWorkspace Layout(out MainWindow w)
    {
        w = Ui.Open();
        w.Session.Commands.Run("Box");
        w.ShowLayout();
        Ui.Pump();
        return (LayoutWorkspace)w.Content!;
    }

    private static TextBox FieldAfter(LayoutWorkspace layout, string label)
    {
        var all = layout.GetLogicalDescendants().ToList();
        int i = all.FindIndex(c => c is TextBlock t && t.Text == label);
        Assert.True(i >= 0, $"No field labelled {label}.");
        return all.Skip(i).OfType<TextBox>().First();
    }

    private static void Enter(MainWindow w, TextBox box, string text)
    {
        box.Focus();
        box.Text = text;
        Ui.Key(w, Avalonia.Input.Key.Enter);
    }

    [AvaloniaFact]
    public void The_camera_panel_sets_depth_of_field_focuses_on_an_item_and_sets_motion_blur()
    {
        var layout = Layout(out var w);
        var s = layout.Session;
        var cam = s.Scene.CurrentCamera!;
        s.Select(cam);
        Ui.Pump();
        var dof = layout.GetLogicalDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Depth of field");
        dof.IsChecked = true;
        Assert.True(cam.DepthOfField.On);
        Enter(w, FieldAfter(layout, "Lens f-stop"), "0.5");
        Assert.Equal(0.5, cam.DepthOfField.FStop);
        // Focus on: the box, 5 m in front.
        var focus = layout.GetLogicalDescendants().OfType<Button>().Single(b => (b.Content as TextBlock)?.Text == "Focus on" || (string?)b.Content == "Focus on");
        focus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        Assert.Equal(5, cam.DepthOfField.FocalDistance, 6);
        Assert.Equal("Focus Camera on Modeler object", s.History.UndoLabel);

        var blur = layout.GetLogicalDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Motion blur");
        blur.IsChecked = true;
        Enter(w, FieldAfter(layout, "Shutter (frames, 0–1)"), "0.25");
        Assert.Equal(new MotionBlur { On = true, Shutter = 0.25 }, cam.MotionBlur);
        s.Undo();
        Assert.Equal(0.5, cam.MotionBlur.Shutter);
        w.Close();
    }

    [AvaloniaFact]
    public void The_light_panel_sets_an_area_lights_size_and_a_soft_lights_radius()
    {
        var layout = Layout(out var w);
        var s = layout.Session;
        var light = s.Scene.Lights.First();
        s.Select(light);
        Ui.Pump();
        // A distant light has neither.
        Assert.DoesNotContain(layout.GetLogicalDescendants(), c => c is TextBlock { Text: "Area width (m)" or "Radius (m, 0: hard shadows)" });
        var type = layout.GetLogicalDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains("Area"));
        type.SelectedItem = "Area";
        Ui.Pump();
        Assert.Equal(LightType.Area, light.Type);
        Enter(w, FieldAfter(layout, "Area width (m)"), "2.5");
        Enter(w, FieldAfter(layout, "Shadow samples"), "48");
        Assert.Equal((2.5, 48), (light.AreaWidth, light.ShadowSamples));
        type.SelectedItem = "Point";
        Ui.Pump();
        Enter(w, FieldAfter(layout, "Radius (m, 0: hard shadows)"), "0.3");
        Assert.Equal(0.3, light.Radius);
        Assert.True(light.IsSoft);
        w.Close();
    }
}
