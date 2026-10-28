using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

public class UVGeometryTests
{
    /// <summary>A box whose faces each have UVs of their own, the box's own (x, y) for x/y faces and so on: seams everywhere.</summary>
    private static Mesh Seamed(Vec3 min, Vec3 max)
    {
        var m = Box(min, max);
        foreach (var p in m.Polygons)
        {
            var n = m.Normal(p);
            var axis = TextureMapping.Dominant(n);
            var (right, up) = TextureMapping.ViewAxes(axis);
            foreach (int i in p.Points) m.SetCornerUV("UV", p, i, new Vec2(m.Points[i][right] + 10 * (int)axis, m.Points[i][up]));
        }
        return m;
    }

    /// <summary>Each corner has its face's planar UVs: (right, up) of where it is, plus 10 per axis.</summary>
    private static void FollowsFaces(Mesh m)
    {
        foreach (var p in m.Polygons.Where(p => p.Count >= 3))
        {
            var axis = TextureMapping.Dominant(m.Normal(p));
            var (right, up) = TextureMapping.ViewAxes(axis);
            foreach (int i in p.Points)
            {
                Assert.True(m.TryGetUV("UV", p, i, out var uv), $"{p} isn't mapped");
                Assert.Equal(m.Points[i][right] + 10 * (int)axis, uv.X, 9);
                Assert.Equal(m.Points[i][up], uv.Y, 9);
            }
        }
    }

    [Fact]
    public void Seamed_boxes_keep_every_faces_uvs_through_booleans()
    {
        var a = Seamed(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        var b = Seamed(new Vec3(0, -0.5, -0.5), new Vec3(2, 1.5, 1.5));
        FollowsFaces(a);
        foreach (var op in new[] { BooleanOperation.Union, BooleanOperation.Subtract, BooleanOperation.Intersect, BooleanOperation.Add })
            FollowsFaces(Booleans.Apply(op, a, b));
    }

    [Fact]
    public void Seams_survive_faceted_subdivision_and_the_metanurbs_preview_maps_its_faces()
    {
        var m = Seamed(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        Subdivision.Faceted(m);
        FollowsFaces(m);
        var smooth = Subdivision.CatmullClark(Seamed(new Vec3(-1, -1, -1), new Vec3(1, 1, 1)), 2);
        Assert.Equal(96, smooth.Polygons.Count);
        Assert.All(smooth.Polygons, p => Assert.True(smooth.IsMapped("UV", p)));
        // Every piece of a face sits within that face's patch of the map.
        Assert.All(smooth.Polygons, p => Assert.InRange(smooth.CornerUVsOf("UV", p).Select(u => Math.Floor((u!.Value.X + 5) / 10)).Distinct().Count(), 1, 1));
    }

    [Fact]
    public void Merging_points_keeps_the_seams_and_merged_polygons_keep_their_corners()
    {
        // Each face its own four points, as a converter might leave it, then welded.
        var m = Seamed(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        var loose = new Mesh();
        foreach (var p in m.Polygons)
        {
            var uvs = m.CornerUVsOf("UV", p);
            int first = loose.Points.Count;
            loose.Points.AddRange(p.Points.Select(i => m.Points[i]));
            var q = new Polygon(Enumerable.Range(first, p.Count));
            loose.Polygons.Add(q);
            for (int k = 0; k < p.Count; k++) loose.SetPointUV("UV", first + k, uvs[k]!.Value);
        }
        loose.MergePoints(1e-9);
        Assert.Equal(8, loose.Points.Count);
        FollowsFaces(loose);
        // Two triangles of one face merged back into the quad keep both triangles' corners.
        var tri = Seamed(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        Triangulator.Triple(tri);
        FollowsFaces(tri);
        PolygonOps.Merge(tri);
        Assert.Equal(6, tri.Polygons.Count);
        FollowsFaces(tri);
    }
}
