using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;

namespace AVAModeler.UI.Tests;

public class ProceduralPanelTests
{
    private static List<string> Labels(Window w) => w.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void The_texture_panel_turns_a_layer_into_a_procedural_and_shows_its_settings()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 1, 1 } });
        var editor = new SurfaceEditor(w.Session);
        editor.Show(w);
        Ui.Pump();
        var panel = editor.OpenTexture(w.Session.Document.Surfaces[0], TextureChannel.Color);
        Ui.Pump();
        panel.AddLayer();
        var type = panel.GetLogicalDescendants().OfType<ComboBox>().First();
        Assert.Equal("Planar Image Map", type.SelectedItem);
        Assert.Contains("Marble", (IEnumerable<string>)type.ItemsSource!);
        // Choosing Marble from the Texture type list makes it a procedural with LightWave's settings.
        type.SelectedItem = "Marble";
        Ui.Pump();
        var layer = Assert.Single(panel.Layers);
        Assert.True(layer.IsProcedural);
        Assert.Equal(0.5, Procedurals.Get(layer, "Vein Spacing"));
        var labels = Labels(panel);
        Assert.Contains("Vein Spacing", labels);
        Assert.Contains("Texture color (R, G, B, 0 to 255)", labels);
        Assert.DoesNotContain("Image", labels);
        // The list says what it is.
        Assert.Contains("1. Marble", panel.GetLogicalDescendants().OfType<ListBox>().First().ItemsSource!.Cast<string>());
        // Back to an image map through the same list.
        panel.SetType("Cubic Image Map");
        Assert.True(layer.IsImage);
        Assert.Contains("Image", Labels(panel));
        panel.SetType("Checkerboard");
        Assert.Equal("Checkerboard", layer.Procedural);
        Assert.NotNull(panel.CaptureRenderedFrame());
        panel.Close();
        editor.Close();
        Ui.Pump();
        // The visit is one undo step.
        Assert.Equal("Surfaces", w.Session.Document.Undo.UndoLabel);
        w.Session.Document.UndoLast();
        Assert.Empty(w.Session.Document.Surfaces[0].Textures);
    }

    [AvaloniaFact]
    public void A_procedural_the_program_doesnt_know_is_shown_as_kept_but_not_drawn()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", null);
        w.Session.Document.Surfaces[0].Textures.Add(new TextureLayer { Procedural = "Brick" });
        var editor = new SurfaceEditor(w.Session);
        editor.Show(w);
        var panel = editor.OpenTexture(w.Session.Document.Surfaces[0], TextureChannel.Color);
        Ui.Pump();
        Assert.Contains(Labels(panel), l => l.Contains("doesn't know") && l.Contains("not drawn"));
        Assert.Contains("1. Brick (not drawn)", panel.GetLogicalDescendants().OfType<ListBox>().First().ItemsSource!.Cast<string>());
        panel.Close();
        editor.Close();
    }
}
