using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>
/// Breaks faces into triangles for drawing and picking, with a cache per mesh version so a still model isn't
/// re-tessellated every frame.
/// </summary>
public static class Tessellation
{
    /// <summary>A triangle of a face: the face's index and three point indices, in the face's winding.</summary>
    public readonly record struct Tri(int Polygon, int A, int B, int C);

    private sealed record Entry(int Version, Tri[] Tris);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Entry> Cache = new();

    /// <summary>The triangulator for one face (point indices in, triples out): concave and non-planar n-gons are cut properly.</summary>
    public static Func<Mesh, Polygon, IReadOnlyList<(int A, int B, int C)>> Triangulate { get; set; } = Proper;

    public static Tri[] Of(Mesh mesh)
    {
        if (Cache.TryGetValue(mesh, out var e) && e.Version == mesh.Version) return e.Tris;
        var list = new List<Tri>(mesh.Polygons.Count * 2);
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
        {
            var p = mesh.Polygons[pi];
            if (p.Count < 3) continue;
            if (p.Count == 3) { list.Add(new Tri(pi, p.Points[0], p.Points[1], p.Points[2])); continue; }
            foreach (var (a, b, c) in Triangulate(mesh, p)) list.Add(new Tri(pi, a, b, c));
        }
        var tris = list.ToArray();
        Cache.AddOrUpdate(mesh, new Entry(mesh.Version, tris));
        return tris;
    }

    private static IReadOnlyList<(int, int, int)> Proper(Mesh mesh, Polygon p)
    {
        var local = Geometry.Triangulator.TriangulatePolygon(mesh, p);
        var r = new List<(int, int, int)>(local.Length / 3);
        for (int i = 0; i + 2 < local.Length; i += 3) r.Add((p.Points[local[i]], p.Points[local[i + 1]], p.Points[local[i + 2]]));
        return r;
    }
}
