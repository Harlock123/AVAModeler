using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Following a path in Layout's session: timing it, keying its position, baking it, the dope sheet's row and the drawn path.</summary>
public class FollowPathLayoutTests
{
    private static (LayoutSession Session, NullItem Car, List<NullItem> Points) Road()
    {
        var s = new LayoutSession();
        var points = new List<NullItem>();
        foreach (var (x, z) in new[] { (0.0, 0.0), (4, 0), (6, 3), (6, 7) })
        {
            var p = s.Scene.AddNull($"P{points.Count + 1}");
            p.SetPosition(0, new Vec3(x, 0, z));
            points.Add(p);
        }
        var car = s.Scene.AddNull("Car");
        s.Replace(s.Scene);
        s.Edit("Follow Path", () => { car.Path = new FollowPath { Points = points }; s.Scene.Notify(); });
        return (s, car, points);
    }

    [Fact]
    public void Even_speed_keys_path_position_linearly_as_one_undo_step()
    {
        var (s, car, points) = Road();
        s.TimePath(car, 1, 60);
        Assert.Equal("Time Car's Path", s.History.UndoLabel);
        Assert.Equal([1.0, 60], car.PathPosition.Keys.Select(k => k.Frame));
        Assert.True(car.PathPosition.Keys.All(k => k.Linear));
        Assert.True(Vec3.Near(points[0].WorldPosition(1), car.WorldPosition(1), 1e-9));
        Assert.True(Vec3.Near(points[3].WorldPosition(60), car.WorldPosition(60), 1e-9));
        Assert.Equal(0.5, car.PathPosition.Evaluate(30.5), 9);
        Assert.Throws<ArgumentException>(() => s.TimePath(car, 10, 5));
        s.Undo();
        Assert.Single(car.PathPosition.Keys);
    }

    [Fact]
    public void Path_position_is_keyed_by_the_same_rule_as_the_other_channels()
    {
        var (s, car, _) = Road();
        s.SetFrame(10);
        s.SetPathPosition(car, 0.25);
        s.SetFrame(40);
        s.SetPathPosition(car, 0.75);
        Assert.Equal([10.0, 40], car.PathPosition.Keys.Select(k => k.Frame));
        // With auto-key off, the key at the frame changes.
        s.AutoKey = false;
        s.SetPathPosition(car, 0.8);
        Assert.Equal(0.8, car.PathPosition.Evaluate(40), 9);
        Assert.Equal(2, car.PathPosition.Keys.Count);
        // The channel is the dope sheet's too.
        var sheet = new DopeSheet(s) { ShowChannels = true };
        Assert.Contains(sheet.Rows(), r => r.Item == car && r.Channel == SceneChannels.PathPosition);
        Assert.Equal(car.PathPosition, SceneChannels.Find(car, "path.position"));
    }

    [Fact]
    public void Baking_the_path_is_one_undo_step()
    {
        var (s, car, _) = Road();
        s.TimePath(car, 1, 60);
        var at30 = car.WorldPosition(30);
        var frames = s.BakePath(car, 1, 60, 1);
        Assert.Equal(60, frames.Count);
        Assert.Null(car.Path);
        Assert.True(Vec3.Near(at30, car.WorldPosition(30), 1e-9));
        Assert.Equal(60, car[Channel.PositionX].Keys.Count);
        s.Undo();
        Assert.NotNull(car.Path);
        Assert.Single(car[Channel.PositionX].Keys);
    }

    [Fact]
    public void The_view_draws_the_path_through_its_points()
    {
        var (s, car, points) = Road();
        var drawn = SceneDrawing.Build(s.Scene.Evaluate(1), s.Scene.Items, shaded: false);
        var path = drawn.Overlay.Where(l => l.Color == SceneDrawing.PathColor).ToList();
        Assert.True(path.Count > 100);
        // It starts and ends at the end points, and passes through the others.
        Assert.True(Vec3.Near(points[0].WorldPosition(1), path[0].A, 1e-9));
        Assert.True(Vec3.Near(points[3].WorldPosition(1), path[^1].B, 1e-9));
        Assert.Contains(path, l => (l.A - points[2].WorldPosition(1)).Length < 0.1);
        // The chosen follower's path is drawn in the selection's colour.
        drawn = SceneDrawing.Build(s.Scene.Evaluate(1), s.Scene.Items, shaded: false, car);
        Assert.DoesNotContain(drawn.Overlay, l => l.Color == SceneDrawing.PathColor);
        Assert.Null(SceneDrawing.PathLine(points[0], 1));
    }
}
