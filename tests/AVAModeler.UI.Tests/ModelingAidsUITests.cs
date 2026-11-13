using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

/// <summary>The History panel, symmetry and map shading from the keys, and Photo Match's clicks.</summary>
public class ModelingAidsUITests
{
    private static void Press(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
    }

    [AvaloniaFact]
    public void Ctrl_H_shows_the_history_and_a_click_goes_back_to_a_step()
    {
        var w = Ui.Open();
        var c = w.Session.Commands;
        c.Run("Box");
        c.Run("Move", new Dictionary<string, object?> { ["Offset"] = new[] { 1.0, 0, 0 } });
        c.Run("Size", new Dictionary<string, object?> { ["Factor"] = 200.0 });
        Ui.Fit(w);
        Ui.Key(w, Key.H, RawInputModifiers.Control);
        Ui.Pump();
        var panel = w.Modeler.History;
        Assert.True(panel.IsVisible);
        Assert.Equal(3, panel.Steps.Count);
        // Newest at the top: Size, Move, Box. Clicking Box undoes the two after it.
        Press(panel.Steps[2]);
        Assert.Equal(["Box"], w.Session.Document.Undo.UndoLabels);
        Assert.Equal(3, panel.Steps.Count); // two to redo, the one done
        Press(panel.Steps[0]);
        Assert.Empty(w.Session.Document.Undo.RedoLabels);
        Assert.NotNull(w.CaptureRenderedFrame());
    }

    [AvaloniaFact]
    public void Alt_Y_steps_through_symmetry_and_Alt_W_shows_a_weight_map()
    {
        var w = Ui.Open();
        Ui.Fit(w);
        var doc = w.Session.Document;
        Ui.Key(w, Key.Y, RawInputModifiers.Alt);
        Assert.Equal(Axis.X, doc.Symmetry);
        Ui.Key(w, Key.Y, RawInputModifiers.Alt);
        Assert.Equal(Axis.Y, doc.Symmetry);
        Ui.Key(w, Key.Y, RawInputModifiers.Alt);
        Ui.Key(w, Key.Y, RawInputModifiers.Alt);
        Assert.Null(doc.Symmetry);

        w.Session.Commands.Run("Box");
        w.Session.Commands.SetWeights("Arm", [0, 1], [1]);
        doc.ShownPointMap = null;
        Ui.Key(w, Key.W, RawInputModifiers.Alt);
        Assert.Equal("Arm", doc.ShownPointMap);
        Assert.NotNull(w.CaptureRenderedFrame());
        Ui.Key(w, Key.W, RawInputModifiers.Alt);
        Assert.Null(doc.ShownPointMap);
    }

    [AvaloniaFact]
    public void Photo_match_marks_selected_points_and_solves()
    {
        var w = Ui.Open();
        var c = w.Session.Commands;
        c.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 1, 1.5 } });
        var mesh = w.Session.Document.Current.Mesh;
        string image = Path.Combine(Path.GetTempPath(), $"ava-photo-{Guid.NewGuid():N}.png");
        Png.Save(image, 64, 48, new byte[64 * 48 * 4]);
        try
        {
            var win = new PhotoMatchWindow(w.Session);
            win.AddPhoto(image);
            string name = Path.GetFileNameWithoutExtension(image);
            var eye = new Vec3(3, 2, -5);
            var fw = (-eye).Normalized();
            var rt = Vec3.Cross(Vec3.UnitY, fw).Normalized();
            var up = Vec3.Cross(fw, rt);
            Vec2 See(Vec3 p) { var d = p - eye; double z = Vec3.Dot(d, fw); return new Vec2(32 + 50 * Vec3.Dot(d, rt) / z, 24 - 50 * Vec3.Dot(d, up) / z); }
            win.Click(name, new Vec2(1, 1));
            Assert.Contains("Select one point", win.Status);
            c.SelectMode(SelectionMode.Points);
            for (int i = 0; i < 8; i++)
            {
                c.SelectNone();
                c.SelectPoints([i]);
                win.Click(name, See(mesh.Points[i]));
            }
            Assert.Equal(8, w.Session.Document.Photos[name].Marks.Count);
            win.Solve();
            Assert.Contains("Camera at", win.Status);
            Assert.NotNull(PhotoMatchWindowCamera(w, name));
        }
        finally { File.Delete(image); }
    }

    private static object? PhotoMatchWindowCamera(MainWindow w, string name) => AVAModeler.Geometry.PhotoMatch.CameraOf(w.Session.Document.Photos[name]);
}
