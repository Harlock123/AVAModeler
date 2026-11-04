using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>Following a path from Layout's item panel: choosing the points, Follow a path, Closed, Even speed, Bake path, and undo.</summary>
public class FollowPathUITests
{
    private static T Named<T>(LayoutWorkspace layout, string name) where T : Control =>
        layout.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Press(LayoutWorkspace layout, string text) =>
        layout.GetLogicalDescendants().OfType<Button>().Single(b => (b.Content as TextBlock)?.Text == text || (string?)b.Content == text)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void An_item_follows_a_path_through_the_nulls_chosen_in_its_panel()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var points = new List<NullItem>();
        foreach (var (x, z) in new[] { (0.0, 0.0), (3, 0), (3, 3), (0, 3) })
        {
            var p = s.Add(new NullItem($"Corner {points.Count + 1}"));
            s.SetVector(p, 0, new Vec3(x, 0, z));
            points.Add(p);
        }
        var car = s.Add(new NullItem("Car"));
        Ui.Pump();
        var list = Named<ListBox>(layout, "PathPoints");
        var entries = list.Items.Cast<LayoutWorkspace.ItemChoice>().ToList();
        // The item itself isn't offered; the camera, light and corners are.
        Assert.DoesNotContain(entries, e => e.Item == car);
        foreach (var e in entries.Where(e => points.Contains(e.Item))) list.SelectedItems!.Add(e);
        Named<CheckBox>(layout, "FollowPath").IsChecked = true;
        Ui.Pump();
        Assert.Equal(points, car.Path!.Points);
        Assert.Equal("Follow Path", s.History.UndoLabel);
        Named<CheckBox>(layout, "PathClosed").IsChecked = true;
        Assert.True(car.Path.Closed);
        Press(layout, "Even speed");
        Ui.Pump();
        Assert.Equal([1.0, 60], car.PathPosition.Keys.Select(k => k.Frame));
        Assert.True(Vec3.Near(Vec3.Zero, car.WorldPosition(1), 1e-9));
        // Halfway round the square, near the far corner.
        Assert.True((car.WorldPosition(30.5) - new Vec3(3, 0, 3)).Length < 0.5);
        // Taking a point out of the list reshapes the path.
        list.SelectedItems!.Remove(entries.Single(e => e.Item == points[3]));
        Ui.Pump();
        Assert.Equal(3, car.Path!.Points.Count);
        Press(layout, "Bake path");
        Ui.Pump();
        Assert.Null(car.Path);
        Assert.Equal(60, car[Channel.PositionX].Keys.Count);
        layout.Undo();
        Assert.NotNull(car.Path);
        w.Close();
    }
}
