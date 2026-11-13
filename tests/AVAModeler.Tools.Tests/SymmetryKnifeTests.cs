using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>Modeling with symmetry (partners, widened selections, mirrored moves) and the Knife.</summary>
public class SymmetryKnifeTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    private static (Document Doc, Commands Cmd) Box(int segX = 2, int segYZ = 2)
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Size", new[] { 2.0, 2, 2 }), ("SegmentsX", segX), ("SegmentsY", segYZ), ("SegmentsZ", segYZ)));
        return (d, cmd);
    }

    private static int Find(Mesh m, Vec3 v) => Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], v, 1e-6));

    private static void AssertMirrored(Mesh m, Axis axis)
    {
        var pairs = Symmetry.Pairs(m, axis);
        Assert.All(Enumerable.Range(0, m.Points.Count), i => Assert.True(pairs[i] >= 0, $"point {i} at {m.Points[i]} lost its partner"));
    }

    [Fact]
    public void Partners_are_found_by_position_and_points_on_the_plane_pair_with_themselves()
    {
        var (d, _) = Box();
        var m = d.Current.Mesh;
        var pairs = Symmetry.Pairs(m, Axis.X);
        int right = Find(m, new Vec3(1, 1, 1)), left = Find(m, new Vec3(-1, 1, 1)), middle = Find(m, new Vec3(0, 1, 1));
        Assert.Equal(left, pairs[right]);
        Assert.Equal(right, pairs[left]);
        Assert.Equal(middle, pairs[middle]);
        Assert.Equal(m.Polygons.Count, Symmetry.PolygonPairs(m, Axis.X).Count);
        Assert.Null(Symmetry.Parse("off"));
        Assert.Equal(Axis.Y, Symmetry.Parse(" Y "));
        Assert.Throws<ArgumentException>(() => Symmetry.Parse("w"));
        // A lone point has none.
        m.AddPoint(new Vec3(0.3, 5, 0));
        Assert.Equal(-1, Symmetry.Pairs(m, Axis.X)[^1]);
    }

    [Fact]
    public void The_active_selection_takes_in_partners_and_picks_mirror_too()
    {
        var (d, cmd) = Box();
        var m = d.Current.Mesh;
        int right = Find(m, new Vec3(1, 1, 1)), left = Find(m, new Vec3(-1, 1, 1));
        cmd.SelectMode(SelectionMode.Points);
        cmd.SelectPoints([right]);
        Assert.Single(d.Selection.ActivePoints(0, m));
        d.Symmetry = Axis.X;
        Assert.Equal(new[] { left, right }.Order(), d.Selection.ActivePoints(0, m).Order());
        Assert.Equal([right], d.Selection.ActivePointsAsSelected(0, m));
        // The setting survives a new selection (undo puts selections back) and a copy of the document.
        d.Selection = new Selection();
        Assert.Equal(Axis.X, d.Selection.Symmetry);
        Assert.Equal(Axis.X, d.Copy().Symmetry);

        // A pick mirrors: adding a polygon adds its partner, taking it away takes the partner too.
        cmd.SelectMode(SelectionMode.Polygons);
        var before = d.Selection.Clone();
        int face = Enumerable.Range(0, m.Polygons.Count).First(i => m.Centroid(m.Polygons[i]).X > 0.9);
        d.Selection.PolygonsIn(0).Add(face);
        Symmetry.MirrorPick(d, before);
        Assert.Equal(2, d.Selection.PolygonsIn(0).Count);
        before = d.Selection.Clone();
        d.Selection.PolygonsIn(0).Remove(face);
        Symmetry.MirrorPick(d, before);
        Assert.Empty(d.Selection.PolygonsIn(0));
    }

    [Fact]
    public void Moves_keep_the_sides_matched_and_the_middle_on_the_plane()
    {
        var (d, cmd) = Box();
        var m = d.Current.Mesh;
        int right = Find(m, new Vec3(1, 1, 1)), left = Find(m, new Vec3(-1, 1, 1)), middle = Find(m, new Vec3(0, 1, 1));
        d.Symmetry = Axis.X;
        cmd.SelectMode(SelectionMode.Points);
        cmd.SelectPoints([right, middle]);
        cmd.Run("Move", Args(("Offset", new[] { 0.5, 0.25, 0 })));
        Assert.True(Vec3.Near(new Vec3(1.5, 1.25, 1), m.Points[right], 1e-9));
        Assert.True(Vec3.Near(new Vec3(-1.5, 1.25, 1), m.Points[left], 1e-9));
        Assert.True(Vec3.Near(new Vec3(0, 1.25, 1), m.Points[middle], 1e-9), "a point on the plane stays on it");

        // Choosing the left point instead: it moves as asked and the right one mirrors it.
        cmd.SelectNone();
        cmd.SelectPoints([left]);
        cmd.Run("Move", Args(("Offset", new[] { 0.5, 0, 0 })));
        Assert.Equal(-1.0, m.Points[left].X, 9);
        Assert.Equal(1.0, m.Points[right].X, 9);
        AssertMirrored(m, Axis.X);
    }

    [Fact]
    public void A_magnet_pulled_on_one_side_pulls_the_other_and_jitter_stays_symmetric()
    {
        var (d, cmd) = Box(4);
        var m = d.Current.Mesh;
        d.Symmetry = Axis.X;
        cmd.Run("Magnet", Args(("Center", new[] { -1.0, 1, 1 }), ("Radius", new[] { 0.8, 0.8, 0.8 }), ("Offset", new[] { 0, 0.5, 0 })));
        int left = Find(m, new Vec3(-1, 1.5, 1));
        Assert.True(Vec3.Near(m.Points[Symmetry.Pairs(m, Axis.X)[left]], new Vec3(1, 1.5, 1), 1e-9));
        cmd.Run("Jitter", Args(("Amount", new[] { 0.1, 0.1, 0.1 }), ("Seed", 7)));
        AssertMirrored(m, Axis.X);
        // Undo puts both sides back.
        d.UndoLast();
        d.UndoLast();
        Assert.Contains(d.Current.Mesh.Points, p => Vec3.Near(p, new Vec3(1, 1, 1), 1e-9));
        Assert.Contains(d.Current.Mesh.Points, p => Vec3.Near(p, new Vec3(-1, 1, 1), 1e-9));
    }

    [Fact]
    public void Extrude_on_one_side_extrudes_its_mirror_and_the_mesh_stays_closed()
    {
        var (d, cmd) = Box();
        var m = d.Current.Mesh;
        d.Symmetry = Axis.X;
        cmd.SelectMode(SelectionMode.Polygons);
        int face = Enumerable.Range(0, m.Polygons.Count).First(i => m.Normal(i).X > 0.9);
        cmd.SelectPolygons([face]);
        int before = m.Polygons.Count;
        cmd.Run("Smooth Shift", Args(("Offset", 0.3)));
        Assert.Equal(before + 8, m.Polygons.Count);
        Assert.Contains(m.Points, p => p.X < -1.2);
        Assert.True(MeshCheck.Document(d).Printable);
        AssertMirrored(m, Axis.X);
    }

    [Fact]
    public void Lua_turns_symmetry_on_and_off()
    {
        var (d, cmd) = Box();
        Macros.Run("print(symmetry('z')) print(symmetry()) symmetry('off') print(symmetry())", cmd);
        Assert.Equal(["z", "z", "off"], cmd.Output);
        Assert.Null(d.Symmetry);
    }

    [Fact]
    public void The_knife_cuts_a_box_across_and_leaves_it_closed_with_uvs()
    {
        var (d, cmd) = Box(1);
        cmd.MakeUVs("Wrap", UVProjection.Cubic, Axis.Y);
        var m = d.Current.Mesh;
        int faces = m.Polygons.Count;
        // A line across the Front view at x = 0.25, cutting through the box along Z.
        cmd.Run("Knife", Args(("Start", new[] { 0.25, -2, 0 }), ("End", new[] { 0.25, 2, 0 }), ("Axis", "Z")));
        var r = MeshCheck.Document(d);
        Assert.True(r.Printable, string.Join("; ", r.Issues()));
        // The top, bottom, front and back faces at the cut (two quads each) are each split in two.
        Assert.Equal(faces + 8, m.Polygons.Count);
        Assert.Equal(8, m.Points.Count(p => Math.Abs(p.X - 0.25) < 1e-9));
        Assert.All(m.Polygons, p => Assert.True(m.IsMapped("Wrap", p), $"{p} has corners without UVs"));
        Assert.Equal(8.0, MeshMeasure.Volume(m), 6);
    }

    [Fact]
    public void The_knife_cuts_only_between_its_ends_and_puts_points_into_neighbours()
    {
        var (d, cmd) = Box(1, 1);
        var m = d.Current.Mesh;
        // A short line on the front face (z = -1) only: from y = -0.5 to past the top edge, across x = 0.
        cmd.SelectMode(SelectionMode.Polygons);
        int front = Enumerable.Range(0, m.Polygons.Count).First(i => m.Normal(i).Z < -0.9);
        cmd.SelectPolygons([front]);
        cmd.Run("Knife", Args(("Start", new[] { 0.0, -0.5, 0 }), ("End", new[] { 0.0, 1.5, 0 }), ("Axis", "Z")));
        // One new point, on the front face's top edge: the front face isn't split (the cut stops inside it), but the
        // top face takes the point into its edge, so there's no crack.
        Assert.Equal(9, m.Points.Count);
        Assert.Equal(6, m.Polygons.Count);
        Assert.True(MeshCheck.Document(d).Printable);
        Assert.Equal(5, m.Polygons.First(p => m.Normal(p).Y > 0.9).Count);
        // Through the whole face it splits it.
        cmd.Run("Knife", Args(("Start", new[] { 0.0, -1.5, 0 }), ("End", new[] { 0.0, 1.5, 0 }), ("Axis", "Z")));
        Assert.Equal(7, m.Polygons.Count);
        Assert.True(MeshCheck.Document(d).Printable);
    }

    [Fact]
    public void The_knife_with_symmetry_cuts_the_mirror_too()
    {
        var (d, cmd) = Box(1, 1);
        var m = d.Current.Mesh;
        d.Symmetry = Axis.X;
        cmd.Run("Knife", Args(("Start", new[] { 0.5, -2, 0 }), ("End", new[] { 0.5, 2, 0 }), ("Axis", "Z")));
        Assert.Contains(m.Points, p => Math.Abs(p.X + 0.5) < 1e-9);
        Assert.Equal(14, m.Polygons.Count);
        AssertMirrored(m, Axis.X);
        Assert.True(MeshCheck.Document(d).Printable);
    }

    [Fact]
    public void The_knife_is_one_undo_step_and_a_click_does_nothing()
    {
        var (d, cmd) = Box(1, 1);
        var m = d.Current.Mesh;
        cmd.Run("Knife", Args(("Start", new[] { 0.3, 0.3, 0 }), ("End", new[] { 0.3, 0.3, 5 }), ("Axis", "Z")));
        Assert.Equal(6, m.Polygons.Count);
        cmd.Run("Knife", Args(("Start", new[] { -2.0, 0.2, 0 }), ("End", new[] { 2.0, 0.2, 0 }), ("Axis", "Z")));
        Assert.Equal(10, m.Polygons.Count);
        d.UndoLast();
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        Assert.Equal(8, d.Current.Mesh.Points.Count);
        Assert.Throws<ArgumentException>(() => Knife.Cut(new Mesh(), [], Vec3.Zero, Vec3.Zero, Vec3.UnitZ));
    }
}
