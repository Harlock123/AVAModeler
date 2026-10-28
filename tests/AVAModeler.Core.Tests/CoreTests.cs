using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

public class MathTests
{
    [Fact]
    public void Vectors_add_dot_and_cross()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(4, 5, 6);
        Assert.Equal(new Vec3(5, 7, 9), a + b);
        Assert.Equal(32, Vec3.Dot(a, b));
        Assert.Equal(new Vec3(-3, 6, -3), Vec3.Cross(a, b));
        Assert.Equal(1, new Vec3(3, 4, 0).Normalized().Length, 12);
        Assert.Equal(Vec3.Zero, Vec3.Zero.Normalized());
    }

    [Fact]
    public void Matrices_compose_right_to_left_and_invert()
    {
        var m = Mat4.Translation(new Vec3(10, 0, 0)) * Mat4.Scaling(2);
        Assert.Equal(new Vec3(12, 2, 2), m.TransformPoint(Vec3.One));
        var r = Mat4.RotationY(Mat4.Deg(90));
        var p = r.TransformPoint(Vec3.UnitZ);
        Assert.True(Vec3.Near(new Vec3(1, 0, 0), p, 1e-12));
        var any = Mat4.Translation(new Vec3(1, 2, 3)) * Mat4.Rotation(new Vec3(1, 1, 0), 0.7) * Mat4.Scaling(new Vec3(2, 3, 4));
        var round = any.Inverted() * any;
        Assert.True(round == Mat4.Identity);
        Assert.Equal(24, Mat4.Scaling(new Vec3(2, 3, 4)).Determinant(), 9);
        Assert.True(default(Mat4) == Mat4.Identity);
    }

    [Fact]
    public void Heading_turns_forward_toward_positive_x()
    {
        var m = Mat4.HeadingPitchBank(90, 0, 0);
        Assert.True(Vec3.Near(Vec3.UnitX, m.TransformVector(Vec3.UnitZ), 1e-12));
    }

    [Fact]
    public void Projections_map_the_view_volume()
    {
        var proj = Mat4.Perspective(Mat4.Deg(90), 1, 1, 100);
        var near = proj.TransformPoint(new Vec3(0, 0, -1));
        var far = proj.TransformPoint(new Vec3(0, 0, -100));
        Assert.Equal(-1, near.Z, 9);
        Assert.Equal(1, far.Z, 9);
        var ortho = Mat4.Orthographic(-2, 2, -1, 1, 0, 10);
        Assert.True(Vec3.Near(new Vec3(1, 1, -1), ortho.TransformPoint(new Vec3(2, 1, 0)), 1e-12));
        var view = Mat4.LookAt(new Vec3(0, 0, 5), Vec3.Zero, Vec3.UnitY);
        Assert.True(Vec3.Near(new Vec3(0, 0, -5), view.TransformPoint(Vec3.Zero), 1e-12));
    }

    [Fact]
    public void Rays_hit_triangles_planes_and_boxes()
    {
        var ray = new Ray(new Vec3(0.2, 0.2, -5), Vec3.UnitZ);
        var hit = ray.IntersectTriangle(Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        Assert.NotNull(hit);
        Assert.Equal(5, hit.Value.T, 12);
        Assert.Null(new Ray(new Vec3(2, 2, -5), Vec3.UnitZ).IntersectTriangle(Vec3.Zero, Vec3.UnitX, Vec3.UnitY));
        Assert.Equal(5, ray.IntersectPlane(new Plane(Vec3.UnitZ, 0))!.Value, 12);
        Assert.Equal(4, ray.IntersectBox(new Box3(-Vec3.One, Vec3.One))!.Value, 12);
        Assert.Null(new Ray(new Vec3(5, 5, -5), Vec3.UnitZ).IntersectBox(new Box3(-Vec3.One, Vec3.One)));
        Assert.Equal(1, new Ray(Vec3.Zero, Vec3.UnitX).DistanceTo(new Vec3(3, 1, 0)), 12);
    }

    [Fact]
    public void Planes_and_boxes()
    {
        var plane = Plane.FromPoints(Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        Assert.Equal(2, Math.Abs(plane.Distance(new Vec3(5, 5, 2))), 12);
        Assert.Equal(0.25, plane.Crossing(new Vec3(0, 0, -1), new Vec3(0, 0, 3)), 12);
        var box = Box3.Of([new Vec3(1, 2, 3), new Vec3(-1, 0, 5)]);
        Assert.Equal(new Vec3(0, 1, 4), box.Center);
        Assert.Equal(2, box.Extent);
        Assert.True(Box3.Empty.IsEmpty);
        Assert.True(box.Contains(new Vec3(0, 1, 4)));
        Assert.False(box.Intersects(new Box3(new Vec3(5, 5, 5), new Vec3(6, 6, 6))));
    }
}

public class MeshTests
{
    /// <summary>A unit cube centred on the origin, every face wound clockwise as seen from outside.</summary>
    public static Mesh Cube()
    {
        var m = new Mesh();
        for (int i = 0; i < 8; i++) m.AddPoint(new Vec3((i & 1) == 0 ? -0.5 : 0.5, (i & 2) == 0 ? -0.5 : 0.5, (i & 4) == 0 ? -0.5 : 0.5));
        // Each face listed clockwise as seen from outside (LightWave's left-handed convention).
        int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        foreach (var f in faces) m.AddPolygon(f);
        return m;
    }

    [Fact]
    public void A_cubes_faces_point_outward()
    {
        var m = Cube();
        foreach (var p in m.Polygons)
        {
            var n = m.Normal(p);
            Assert.True(Vec3.Dot(n, m.Centroid(p)) > 0.49, $"{p} has normal {n}");
            Assert.Equal(1, m.Area(p), 12);
        }
        // The front face (z = -0.5) faces the viewer in the Front view: down -Z.
        Assert.True(Vec3.Near(-Vec3.UnitZ, m.Normal(0), 1e-12));
    }

    [Fact]
    public void Edge_and_adjacency_maps_follow_changes()
    {
        var m = Cube();
        var edges = m.EdgeMap();
        Assert.Equal(12, edges.Count);
        Assert.All(edges.Values, faces => Assert.Equal(2, faces.Count));
        Assert.Equal(3, m.PolygonsOf(0).Count);
        Assert.Equal(3, m.Neighbours(0).Count());
        m.AddPolygon([0, 7]);
        Assert.Equal(13, m.EdgeMap().Count);
        Assert.Equal((0, 1, 0, 6, 0), m.PolygonTypes());
    }

    [Fact]
    public void Removing_points_takes_their_polygons_and_renumbers()
    {
        var m = Cube();
        var map = m.RemovePoints([0]);
        Assert.Equal(7, m.Points.Count);
        Assert.Equal(3, m.Polygons.Count);
        Assert.Equal(-1, map[0]);
        Assert.Equal(0, map[1]);
        Assert.All(m.Polygons, p => Assert.All(p.Points, i => Assert.InRange(i, 0, 6)));
    }

    [Fact]
    public void Merging_points_welds_duplicates_and_shrinks_polygons()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0));
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0));
        Assert.Equal(2, m.MergePoints(1e-6));
        Assert.Equal(4, m.Points.Count);
        Assert.Equal(1, m.EdgeMap()[Edge.Of(0, 2)].Count == 2 ? 1 : 0);
        // A triangle whose two corners meet becomes a line.
        var n = new Mesh();
        n.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1e-9, 0));
        n.MergePoints(1e-6);
        Assert.Equal(2, n.Polygons[0].Count);
    }

    [Fact]
    public void Removing_unused_points_and_appending()
    {
        var m = Cube();
        m.AddPoint(new Vec3(9, 9, 9));
        Assert.Equal(1, m.RemoveUnusedPoints());
        var both = new Mesh();
        both.Append(m);
        int offset = both.Append(m, s => s + 1);
        Assert.Equal(8, offset);
        Assert.Equal(16, both.Points.Count);
        Assert.Equal(1, both.Polygons[^1].Surface);
        Assert.All(both.Polygons.Skip(6), p => Assert.All(p.Points, i => Assert.True(i >= 8)));
    }

    [Fact]
    public void Clone_is_deep_and_transform_moves_points()
    {
        var m = Cube();
        var c = m.Clone();
        c.Polygons[0].Points[0] = 7;
        c.Transform(Mat4.Translation(Vec3.UnitX));
        Assert.Equal(0, m.Polygons[0].Points[0]);
        Assert.Equal(-0.5, m.Points[0].X);
        Assert.Equal(0.5, c.Points[0].X);
        Assert.Equal(0, c.NonPlanarity(c.Polygons[1]), 12);
    }
}

public class DocumentTests
{
    [Fact]
    public void A_new_document_has_ten_layers_with_the_first_in_front()
    {
        var d = new Document();
        Assert.Equal(10, d.Layers.Count);
        Assert.Equal([0], d.ForegroundLayers);
        Assert.Single(d.Surfaces);
        Assert.Equal(1, d.SurfaceIndex("Red"));
        Assert.Equal(1, d.SurfaceIndex("Red"));
        d.SetLayers([2, 3], [5]);
        Assert.Equal([2, 3], d.ForegroundLayers);
        Assert.Equal([5], d.BackgroundLayers);
        d.SetLayers([], [1]);
        Assert.Equal([0], d.ForegroundLayers);
    }

    [Fact]
    public void Edits_undo_and_redo_with_the_selection()
    {
        var d = new Document();
        d.Edit("Box", () => d.Current.Mesh = MeshTests.Cube());
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(2);
        d.Edit("Remove", () => d.Current.Mesh.RemovePolygons([0, 1]));
        Assert.Equal(4, d.Current.Mesh.Polygons.Count);
        Assert.Equal("Remove", d.Undo.UndoLabel);
        Assert.True(d.UndoLast());
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        Assert.Contains(2, d.Selection.PolygonsIn(0));
        Assert.True(d.RedoLast());
        Assert.Equal(4, d.Current.Mesh.Polygons.Count);
        Assert.True(d.UndoLast());
        Assert.True(d.UndoLast());
        Assert.True(d.Current.Mesh.IsEmpty);
        Assert.False(d.UndoLast());
        Assert.True(d.Modified);
    }

    [Fact]
    public void A_group_of_edits_is_one_undo_step()
    {
        var d = new Document();
        d.Edit("Box", () => d.Current.Mesh = MeshTests.Cube());
        d.SetLayers([0, 1]);
        int n = d.Group("Build", () =>
        {
            d.Edit("Move", () => d.Current.Mesh.Transform(Mat4.Translation(new Vec3(1, 0, 0))));
            d.Edit("Second", () => d.Layers[1].Mesh = MeshTests.Cube(), [1]);
            d.Surfaces[0].Color = new Rgb(1, 2, 3);
            d.Edit("Remove", () => d.Current.Mesh.RemovePolygons([0]));
            return d.Current.Mesh.Polygons.Count;
        });
        Assert.Equal(5, n);
        Assert.Equal(2, d.Undo.Count);
        Assert.Equal("Build", d.Undo.UndoLabel);
        Assert.False(d.InGroup);
        Assert.True(d.UndoLast());
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        Assert.Equal(-0.5, d.Current.Mesh.Points[0].X);
        Assert.True(d.Layers[1].Mesh.IsEmpty);
        Assert.Equal(Rgb.Grey, d.Surfaces[0].Color);
        Assert.True(d.RedoLast());
        Assert.Equal(5, d.Current.Mesh.Polygons.Count);
        // A group that edits nothing adds no step; one inside another is part of it.
        d.Group("Nothing", () => { });
        Assert.Equal(2, d.Undo.Count);
        d.Group("Outer", () => d.Group("Inner", () => d.Edit("Move", () => d.Current.Mesh.Transform(Mat4.Translation(Vec3.UnitY)))));
        Assert.Equal(3, d.Undo.Count);
        Assert.Equal("Outer", d.Undo.UndoLabel);
    }

    [Fact]
    public void A_group_that_fails_puts_everything_back_and_leaves_the_history_alone()
    {
        var d = new Document();
        d.Edit("Box", () => d.Current.Mesh = MeshTests.Cube());
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(3);
        Assert.Throws<InvalidOperationException>(() => d.Group("Doomed", () =>
        {
            d.Edit("Remove", () => d.Current.Mesh.RemovePolygons([0, 1, 2, 3]));
            d.Selection.Clear(allModes: true);
            d.SurfaceIndex("New");
            throw new InvalidOperationException("no");
        }));
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        Assert.Equal([3], d.Selection.PolygonsIn(0));
        Assert.Single(d.Surfaces);
        Assert.Equal(1, d.Undo.Count);
        Assert.False(d.InGroup);
        // Undo and redo can't run in the middle of a group (the group would lose its footing).
        Assert.Throws<InvalidOperationException>(() => d.Group("Undoing", () => d.UndoLast()));
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        Assert.Equal(1, d.Undo.Count);
    }

    [Fact]
    public void A_copy_stands_apart_from_its_document()
    {
        var d = new Document { FilePath = "/x/cube.lwo" };
        d.Edit("Box", () => d.Current.Mesh = MeshTests.Cube());
        d.Selection.SelectPoint(0, 4);
        var c = d.Copy();
        Assert.Equal("/x/cube.lwo", c.FilePath);
        Assert.Equal(0, c.Undo.Count);
        Assert.Equal([4], c.Selection.PointsIn(0));
        c.Current.Mesh.Points[0] = new Vec3(9, 9, 9);
        c.Surfaces[0].Color = new Rgb(9, 9, 9);
        c.SetLayers([3]);
        Assert.Equal(-0.5, d.Current.Mesh.Points[0].X);
        Assert.Equal(Rgb.Grey, d.Surfaces[0].Color);
        Assert.Equal([0], d.ForegroundLayers);
    }

    [Fact]
    public void A_cancelled_edit_reverts_without_history()
    {
        var d = new Document();
        d.Edit("Box", () => d.Current.Mesh = MeshTests.Cube());
        var before = d.Snapshot("Move");
        d.Current.Mesh.Transform(Mat4.Translation(new Vec3(5, 0, 0)));
        d.Revert(before);
        Assert.Equal(-0.5, d.Current.Mesh.Points[0].X);
        Assert.Equal(1, d.Undo.Count);
    }

    [Fact]
    public void The_active_points_and_polygons_follow_the_selection_mode()
    {
        var d = new Document();
        var m = d.Current.Mesh = MeshTests.Cube();
        var s = d.Selection;
        Assert.Equal(8, s.ActivePoints(0, m).Count);
        Assert.Equal(6, s.ActivePolygons(0, m).Count);
        s.Mode = SelectionMode.Polygons;
        s.PolygonsIn(0).Add(0);
        Assert.Equal(4, s.ActivePoints(0, m).Count);
        Assert.Equal([0], s.ActivePolygons(0, m));
        s.Mode = SelectionMode.Points;
        s.PointsIn(0).UnionWith([0, 1, 2, 3]);
        Assert.Equal([0], s.ActivePolygons(0, m));
        s.Mode = SelectionMode.Edges;
        s.EdgesIn(0).Add(Edge.Of(0, 1));
        Assert.Equal(2, s.ActivePolygons(0, m).Count);
        s.Mode = SelectionMode.Volume;
        s.Volume = new Box3(new Vec3(-1, -1, -1), new Vec3(1, 1, 0));
        Assert.Equal(4, s.ActivePoints(0, m).Count);
        Assert.Equal(5, s.ActivePolygons(0, m).Count);
        s.VolumeExclusive = true;
        Assert.Equal([0], s.ActivePolygons(0, m));
    }

    [Fact]
    public void Surfaces_know_when_they_have_changed()
    {
        var s = new Surface("Hull") { Raw = [new RawChunk("COLR", [1, 2, 3, 0])] };
        s.RawSignature = s.Signature();
        var c = s.Clone();
        c.Raw![0] = new RawChunk("X", []);
        Assert.Equal("COLR", s.Raw[0].Id);
        c.Diffuse = 0.5;
        Assert.NotEqual(c.RawSignature, c.Signature());
    }
}
