using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>Measurements of a mesh: enclosed volume, area, and whether it is closed.</summary>
public static class MeshMeasure
{
    /// <summary>
    /// The volume enclosed by the faces (all, or those given), by the divergence theorem over a fan of each face.
    /// Positive when a closed mesh's normals face out; meaningless for an open mesh. Points and lines add nothing.
    /// </summary>
    public static double Volume(Mesh mesh, IEnumerable<int>? polygons = null)
    {
        double v = 0;
        foreach (int pi in polygons ?? Enumerable.Range(0, mesh.Polygons.Count))
        {
            var p = mesh.Polygons[pi].Points;
            if (p.Count < 3) continue;
            var a = mesh.Points[p[0]];
            for (int i = 1; i + 1 < p.Count; i++)
                v += Vec3.Dot(a, Vec3.Cross(mesh.Points[p[i]], mesh.Points[p[i + 1]]));
        }
        return v / 6;
    }

    /// <summary>The total area of the faces (all, or those given).</summary>
    public static double Area(Mesh mesh, IEnumerable<int>? polygons = null) =>
        (polygons ?? Enumerable.Range(0, mesh.Polygons.Count)).Sum(i => mesh.Area(mesh.Polygons[i]));

    /// <summary>The edges of faces that are not shared by exactly two faces: the holes and the non-manifold seams.</summary>
    public static List<Edge> OpenEdges(Mesh mesh) =>
        Topology.FaceEdgeCounts(mesh, Enumerable.Range(0, mesh.Polygons.Count)).Where(kv => kv.Value != 2).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Whether the faces close up: every face edge is shared by exactly two faces that run along it in opposite
    /// directions (so the windings agree). An empty mesh is not watertight.
    /// </summary>
    public static bool IsWatertight(Mesh mesh)
    {
        var directed = new Dictionary<(int, int), int>();
        bool any = false;
        foreach (var poly in mesh.Polygons)
        {
            if (poly.Count < 3) continue;
            any = true;
            foreach (var e in poly.Edges())
                directed[e] = directed.GetValueOrDefault(e) + 1;
        }
        return any && directed.All(kv => kv.Value == 1 && directed.GetValueOrDefault((kv.Key.Item2, kv.Key.Item1)) == 1);
    }
}
