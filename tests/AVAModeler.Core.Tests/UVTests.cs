using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

public class UVTests
{
    /// <summary>Two squares side by side sharing an edge (points 1 and 4), mapped across 0–1 as one strip.</summary>
    private static Mesh Strip()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0));
        m.Points.AddRange([new Vec3(2, 0, 0), new Vec3(2, 1, 0)]);
        m.AddPolygon([1, 4, 5, 2]);
        var uv = m.AddUVMap("UV");
        for (int i = 0; i < m.Points.Count; i++) uv.Points[i] = new Vec2(m.Points[i].X / 2, m.Points[i].Y);
        return m;
    }

    [Fact]
    public void A_corner_reads_its_own_uvs_before_its_points()
    {
        var m = Strip();
        Assert.True(m.TryGetUV("UV", m.Polygons[1], 1, out var uv));
        Assert.Equal(new Vec2(0.5, 0), uv);
        m.SetCornerUV("UV", m.Polygons[1], 1, new Vec2(0.75, 0));
        Assert.True(m.TryGetUV("UV", m.Polygons[1], 1, out uv));
        Assert.Equal(new Vec2(0.75, 0), uv);
        Assert.True(m.TryGetUV("UV", m.Polygons[0], 1, out uv));
        Assert.Equal(new Vec2(0.5, 0), uv);
        Assert.False(m.TryGetUV("Other", m.Polygons[0], 1, out _));
        Assert.True(m.IsMapped("UV", m.Polygons[0]));
    }

    [Fact]
    public void Setting_corner_uvs_stores_them_the_leanest_way()
    {
        var m = new Mesh();
        m.AddFace(0, Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        var p = m.Polygons[0];
        m.SetCornerUV("UV", p, 0, new Vec2(0.1, 0.2));
        // A point with no UVs takes them itself.
        Assert.Equal(new Vec2(0.1, 0.2), m.FindUVMap("UV")!.Points[0]);
        Assert.Null(p.CornerUVs);
        // The same again is nothing new; different ones are the corner's own (a seam).
        m.SetCornerUV("UV", p, 0, new Vec2(0.1, 0.2));
        Assert.Null(p.CornerUVs);
        m.SetCornerUV("UV", p, 0, new Vec2(0.9, 0.2));
        Assert.Equal(new Vec2(0.9, 0.2), p.CornerUVs!["UV"][0]);
        // Back to the point's: the corner's own go.
        m.SetCornerUV("UV", p, 0, new Vec2(0.1, 0.2));
        Assert.Null(p.CornerUVs);
    }

    [Fact]
    public void Removing_points_renumbers_the_maps_and_the_corners()
    {
        var m = Strip();
        m.SetCornerUV("UV", m.Polygons[1], 4, new Vec2(0.99, 0));
        m.RemovePoints([0]);
        // The first square went with its point; the second's points moved down one.
        Assert.Single(m.Polygons);
        var uv = m.FindUVMap("UV")!;
        Assert.Equal(new Vec2(0.5, 0), uv.Points[0]);
        Assert.Equal(new Vec2(1, 1), uv.Points[4]);
        Assert.Equal(new Vec2(0.99, 0), m.Polygons[0].CornerUVs!["UV"][3]);
        Assert.False(uv.Points.ContainsKey(5));
    }

    [Fact]
    public void Merging_points_keeps_each_corners_uvs()
    {
        // Two separate squares whose touching edges are at different UVs (a seam), then welded.
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0));
        m.AddFace(0, new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 1, 0), new Vec3(1, 1, 0));
        var uv = m.AddUVMap("UV");
        Vec2[] a = [new(0, 0), new(0.4, 0), new(0.4, 1), new(0, 1)];
        Vec2[] b = [new(0.6, 0), new(1, 0), new(1, 1), new(0.6, 1)];
        for (int i = 0; i < 4; i++) { uv.Points[i] = a[i]; uv.Points[4 + i] = b[i]; }
        Assert.Equal(2, m.MergePoints());
        Assert.Equal(6, m.Points.Count);
        foreach (var (poly, want) in new[] { (m.Polygons[0], a), (m.Polygons[1], b) })
            Assert.Equal(want.Select(v => (Vec2?)v), m.CornerUVsOf("UV", poly));
    }

    [Fact]
    public void Appending_joins_maps_by_name_and_offsets_corners()
    {
        var a = Strip();
        var b = Strip();
        b.SetCornerUV("UV", b.Polygons[0], 0, new Vec2(0.3, 0.3));
        b.AddUVMap("Second").Points[2] = new Vec2(1, 1);
        int offset = a.Append(b);
        Assert.Equal(6, offset);
        Assert.Equal(["UV", "Second"], a.UVMaps.Select(m => m.Name));
        Assert.Equal(12, a.FindUVMap("UV")!.Points.Count);
        Assert.Equal(new Vec2(1, 1), a.FindUVMap("Second")!.Points[8]);
        Assert.Equal(new Vec2(0.3, 0.3), a.Polygons[2].CornerUVs!["UV"][6]);
    }

    [Fact]
    public void Clones_and_undo_snapshots_keep_the_uvs_apart()
    {
        var doc = new Document();
        doc.Layers[0].Mesh = Strip();
        var before = doc.Snapshot("Edit");
        doc.Current.Mesh.FindUVMap("UV")!.Points[0] = new Vec2(9, 9);
        doc.Current.Mesh.SetCornerUV("UV", doc.Current.Mesh.Polygons[0], 1, new Vec2(7, 7));
        doc.Commit(before);
        Assert.True(doc.UndoLast());
        var m = doc.Current.Mesh;
        Assert.Equal(new Vec2(0, 0), m.FindUVMap("UV")!.Points[0]);
        Assert.Null(m.Polygons[0].CornerUVs);
        Assert.True(doc.RedoLast());
        Assert.Equal(new Vec2(9, 9), doc.Current.Mesh.FindUVMap("UV")!.Points[0]);
        Assert.Equal(new Vec2(7, 7), doc.Current.Mesh.Polygons[0].CornerUVs!["UV"][1]);
    }

    [Fact]
    public void Polygons_take_their_corner_uvs_to_new_points_and_new_polygons_inherit()
    {
        var m = Strip();
        var p = m.Polygons[1];
        m.SetCornerUV("UV", p, 4, new Vec2(0.8, 0.1));
        p.SetPoints([1, 5, 5, 2]);
        Assert.Equal(new Vec2(0.8, 0.1), p.CornerUVs!["UV"][5]);
        var copy = new Polygon([10, 11], 0);
        m.Points.AddRange([Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero]);
        m.InheritUVs(copy, m.Polygons[0], q => q - 10);
        Assert.Equal(new Vec2(0, 0), m.FindUVMap("UV")!.Points[10]);
        Assert.Equal(new Vec2(0.5, 0), m.FindUVMap("UV")!.Points[11]);
    }

    [Fact]
    public void The_document_lists_and_chooses_uv_maps()
    {
        var doc = new Document();
        Assert.Null(doc.ActiveUVMap);
        doc.Layers[0].Mesh = Strip();
        doc.Layers[3].Mesh.AddUVMap("Decal");
        Assert.Equal(["UV", "Decal"], doc.UVMapNames());
        Assert.Equal("UV", doc.ActiveUVMap);
        doc.CurrentUVMap = "Decal";
        Assert.Equal("Decal", doc.ActiveUVMap);
        // A chosen map that's gone (an undo took it away) gives way to the first there is.
        doc.CurrentUVMap = "Gone";
        Assert.Equal("UV", doc.ActiveUVMap);
        Assert.Equal(new Box3(Vec3.Zero, new Vec3(1, 1, 0)), doc.Layers[0].Mesh.UVBounds("UV"));
    }
}
