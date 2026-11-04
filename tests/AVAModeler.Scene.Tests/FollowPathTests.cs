using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Following a path: the spline, where an item is along it at each frame, aligning to it, banking, and keeping it in files.</summary>
public class FollowPathTests
{
    /// <summary>Eight nulls round a circle of radius 3 on the ground, and a null "Car" following them closed, once round from frame 1 to 41.</summary>
    private static (Scene Scene, NullItem Car, List<NullItem> Points) Loop(bool align = true)
    {
        var scene = new Scene { FirstFrame = 1, LastFrame = 41 };
        var points = new List<NullItem>();
        for (int i = 0; i < 8; i++)
        {
            var p = scene.AddNull($"P{i + 1}");
            double a = 2 * Math.PI * i / 8;
            // Round anticlockwise seen from above (+X toward +Z): with heading from +Z toward +X, that's turning left.
            p.SetPosition(0, new Vec3(3 * Math.Cos(a), 0, 3 * Math.Sin(a)));
            points.Add(p);
        }
        var car = scene.AddNull("Car");
        car.Path = new FollowPath { Points = points, Closed = true, AlignToPath = align };
        car.PathPosition.SetConstant(0);
        car.PathPosition.AddKey(new EnvelopeKey(1, 0, Linear: true));
        car.PathPosition.AddKey(new EnvelopeKey(41, 1, Linear: true));
        car.PathPosition.RemoveKeyAt(0);
        scene.Notify();
        return (scene, car, points);
    }

    [Fact]
    public void An_open_path_through_points_in_a_line_is_that_line_measured_evenly()
    {
        var path = new MotionPath([new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(3, 0, 0), new Vec3(4, 0, 0)], closed: false);
        Assert.Equal(4, path.Length, 6);
        AssertNear(new Vec3(0, 0, 0), path.At(0), 1e-9);
        AssertNear(new Vec3(4, 0, 0), path.At(1), 1e-9);
        AssertNear(new Vec3(1, 0, 0), path.At(0.25), 1e-4);
        AssertNear(new Vec3(2, 0, 0), path.At(0.5), 1e-4);
        // Past the ends it stays at them.
        AssertNear(new Vec3(4, 0, 0), path.At(1.7), 1e-9);
        AssertNear(new Vec3(1, 0, 0), path.TangentAt(1.5), 1e-6);
        Assert.Throws<ArgumentException>(() => new MotionPath([Vec3.Zero], closed: false));
    }

    [Fact]
    public void A_closed_path_round_a_circle_stays_on_it_wraps_and_turns_by_one_over_its_radius()
    {
        var points = Enumerable.Range(0, 8).Select(i => new Vec3(3 * Math.Cos(Math.PI * i / 4), 1, 3 * Math.Sin(Math.PI * i / 4))).ToList();
        var path = new MotionPath(points, closed: true);
        Assert.InRange(path.Length, 2 * Math.PI * 3 * 0.99, 2 * Math.PI * 3 * 1.01);
        foreach (var u in new[] { 0.0, 0.1, 0.33, 0.5, 0.77, 0.95 })
        {
            var p = path.At(u);
            Assert.InRange(new Vec3(p.X, 0, p.Z).Length, 2.97, 3.03);
            Assert.Equal(1, p.Y, 9);
        }
        AssertNear(path.At(0.25), path.At(1.25), 1e-9);
        AssertNear(path.At(0.9), path.At(-0.1), 1e-9);
        // Anticlockwise from above is a left turn (heading falling): -1/3 radians a metre.
        Assert.Equal(-1.0 / 3, path.TurnAt(path.Length * 0.3), 1);
        // Evenly by length: equal steps in position are equal distances along it.
        double a = (path.At(0.1) - path.At(0.2)).Length, b = (path.At(0.6) - path.At(0.7)).Length;
        Assert.Equal(a, b, 2);
    }

    [Fact]
    public void An_item_goes_where_its_path_position_says_at_each_frame()
    {
        var (_, car, points) = Loop();
        AssertNear(points[0].WorldPosition(1), car.WorldPosition(1), 1e-9);
        AssertNear(points[0].WorldPosition(1), car.WorldPosition(41), 1e-9);
        var curve = new MotionPath(points.Select(p => p.WorldPosition(1)).ToList(), closed: true);
        foreach (var f in new[] { 6.0, 11, 21.5, 33 })
            AssertNear(curve.At((f - 1) / 40), car.WorldPosition(f), 1e-9);
        // Its own position keys are left alone (and ignored).
        Assert.Equal(Vec3.Zero, car.PositionAt(11));
        AssertNear(car.WorldPosition(11), car.MotionPositionAt(11), 1e-9);
    }

    [Fact]
    public void Aligned_it_faces_along_the_path_and_looks_ahead()
    {
        var (scene, car, points) = Loop();
        var curve = new MotionPath(points.Select(p => p.WorldPosition(1)).ToList(), closed: true);
        foreach (var f in new[] { 1.0, 8, 17, 29 })
        {
            double s = (f - 1) / 40 * curve.Length;
            var forward = car.WorldMatrix(f).TransformVector(Vec3.UnitZ);
            AssertNear(curve.TangentAt(s), forward, 1e-6);
            // Level: its +X stays horizontal.
            Assert.Equal(0, car.WorldMatrix(f).TransformVector(Vec3.UnitX).Y, 9);
        }
        car.Path = car.Path! with { LookAhead = 2 };
        scene.Notify();
        double at = 10.0 / 40 * curve.Length;
        AssertNear((curve.PointAt(at + 2) - curve.PointAt(at)).Normalized(), car.WorldMatrix(11).TransformVector(Vec3.UnitZ), 1e-6);
        // Not aligned, it keeps its keyed rotation.
        car.SetRotation(0, new Vec3(12, 0, 0));
        car.Path = car.Path with { AlignToPath = false };
        scene.Notify();
        Assert.Equal(new Vec3(12, 0, 0), car.MotionRotationAt(11));
    }

    [Fact]
    public void Banking_leans_into_turns_and_adds_to_the_bank_key()
    {
        var (scene, car, _) = Loop();
        car.Path = car.Path! with { Banking = 30 };
        scene.Notify();
        // A left turn of radius about 3 (a spline through eight points round a circle of 3 m turns a little faster
        // near its points and slower between them): the left side goes down, bank about +30 / 3 = 10°.
        var banks = Enumerable.Range(0, 40).Select(f => car.MotionRotationAt(1 + f).Z).ToList();
        Assert.All(banks, b => Assert.InRange(b, 7, 14));
        Assert.Equal(10, banks.Average(), 0);
        car.SetRotation(0, new Vec3(0, 0, 5));
        scene.Notify();
        Assert.Equal(banks[10] + 5, car.MotionRotationAt(11).Z, 9);
        Assert.Throws<ArgumentException>(() => (car.Path with { LookAhead = -1 }).Checked());
        Assert.Throws<ArgumentException>(() => (car.Path with { Points = car.Path.Points.Take(2).ToList() }).Checked());
    }

    [Fact]
    public void A_parented_follower_and_moving_points()
    {
        var (scene, car, points) = Loop(align: false);
        var rig = scene.AddNull("Rig");
        rig.SetPosition(0, new Vec3(10, 2, 0));
        rig.SetRotation(0, new Vec3(90, 0, 0));
        car.Parent = rig;
        // Still on the path in the world: its position is worked out in its parent's space.
        AssertNear(points[0].WorldPosition(1), car.WorldPosition(1), 1e-9);
        // A point keyed to move takes the path with it.
        points[0].SetPosition(1, new Vec3(3, 0, 0));
        points[0].SetPosition(41, new Vec3(3, 2, 0));
        AssertNear(new Vec3(3, 2, 0), car.WorldPosition(41), 1e-9);
    }

    [Fact]
    public void Points_must_be_other_items_that_dont_ride_on_the_follower()
    {
        var (scene, car, points) = Loop();
        Assert.Null(FollowPath.Problem(car, points));
        Assert.Contains("its own path", FollowPath.Problem(car, [car, points[0]]));
        Assert.Contains("twice", FollowPath.Problem(car, [points[0], points[0]]));
        var rider = scene.AddNull("Rider");
        rider.Parent = car;
        Assert.Contains("moves with Car", FollowPath.Problem(car, [points[0], rider]));
        // A point that goes leaves the path; too few left and it stops following.
        scene.Remove(points[7]);
        Assert.Equal(7, car.Path!.Points.Count);
        foreach (var p in points.Skip(2).Take(5)) scene.Remove(p);
        Assert.Null(car.Path);
    }

    [Fact]
    public void Snapshots_copies_and_avascene_files_keep_the_path()
    {
        var (scene, car, points) = Loop();
        car.Path = car.Path! with { LookAhead = 0.5, Banking = 20 };
        var snap = scene.Snapshot();
        car.Path = null;
        car.PathPosition.SetConstant(0.3);
        scene.Notify();
        snap.Restore();
        Assert.Equal(8, car.Path!.Points.Count);
        Assert.Equal(1, car.PathPosition.Evaluate(41), 9);

        var copy = scene.Copy();
        var copied = copy.Items.Single(i => i.Name == "Car");
        Assert.Equal(copy.Items.Where(i => i.Name.StartsWith('P')), copied.Path!.Points);
        AssertNear(car.WorldPosition(17), copied.WorldPosition(17), 1e-12);

        var read = SceneJson.Read(SceneJson.Write(scene));
        Assert.Empty(read.Warnings);
        var back = read.Scene.Items.Single(i => i.Name == "Car");
        Assert.Equal(car.Path with { Points = [] }, back.Path! with { Points = [] });
        Assert.Equal(points.Select(p => p.Name), back.Path!.Points.Select(p => p.Name));
        AssertNear(car.WorldPosition(23), back.WorldPosition(23), 1e-9);
        AssertNear(car.MotionRotationAt(23), back.MotionRotationAt(23), 1e-9);

        // A LightWave scene has no place for it, and the writer says so.
        var warnings = new List<string>();
        LwsWriter.Write(scene, warnings);
        Assert.Contains(warnings, w => w.Contains("Car follows a path"));
    }

    [Fact]
    public void Baking_the_path_keys_its_motion_and_stops_it_following()
    {
        var (_, car, _) = Loop();
        var expected = new[] { 1.0, 11, 21, 31, 41 }.Select(f => (car.WorldPosition(f), car.MotionRotationAt(f))).ToList();
        var frames = FollowPath.Bake(car, 1, 41, 10, (e, f, v) => e.AddKey(f, v));
        Assert.Equal([1.0, 11, 21, 31, 41], frames);
        Assert.Null(car.Path);
        for (int i = 0; i < frames.Count; i++)
        {
            AssertNear(expected[i].Item1, car.WorldPosition(frames[i]), 1e-9);
            // Heading unwrapped as it goes round, so the keys turn the short way.
            double d = car.RotationAt(frames[i]).X - expected[i].Item2.X;
            Assert.Equal(0, d - 360 * Math.Round(d / 360), 6);
        }
        var headings = frames.Select(f => car.RotationAt(f).X).ToList();
        for (int i = 1; i < headings.Count; i++) Assert.InRange(Math.Abs(headings[i] - headings[i - 1]), 0, 180);
        Assert.Throws<InvalidOperationException>(() => FollowPath.Bake(car, 1, 41, 1, (_, _, _) => { }));
    }
}
