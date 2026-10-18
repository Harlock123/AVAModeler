using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>Connectivity questions about a set of faces, for the algorithms that need them.</summary>
internal static class Topology
{
    /// <summary>How many of the given faces (3 or more points) use each edge.</summary>
    public static Dictionary<Edge, int> FaceEdgeCounts(Mesh mesh, IEnumerable<int> polygons)
    {
        var counts = new Dictionary<Edge, int>();
        foreach (int pi in polygons)
        {
            var poly = mesh.Polygons[pi];
            if (poly.Count < 3) continue;
            foreach (var (a, b) in poly.Edges())
                if (a != b) counts[Edge.Of(a, b)] = counts.GetValueOrDefault(Edge.Of(a, b)) + 1;
        }
        return counts;
    }

    /// <summary>The given faces split into pieces connected through shared edges.</summary>
    public static List<List<int>> Components(Mesh mesh, IEnumerable<int> polygons)
    {
        var faces = polygons.Where(i => mesh.Polygons[i].Count >= 3).Distinct().ToList();
        var byEdge = new Dictionary<Edge, List<int>>();
        foreach (int f in faces)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = [];
                l.Add(f);
            }
        var seen = new HashSet<int>();
        var result = new List<List<int>>();
        foreach (int start in faces)
        {
            if (!seen.Add(start)) continue;
            var piece = new List<int>();
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                piece.Add(f);
                foreach (var (a, b) in mesh.Polygons[f].Edges())
                    if (a != b)
                        foreach (int g in byEdge[Edge.Of(a, b)])
                            if (seen.Add(g)) queue.Enqueue(g);
            }
            result.Add(piece);
        }
        return result;
    }

    /// <summary>Whether every edge of the faces is shared by exactly two of them.</summary>
    public static bool IsClosed(Mesh mesh, IEnumerable<int> polygons)
    {
        var counts = FaceEdgeCounts(mesh, polygons);
        return counts.Count > 0 && counts.Values.All(c => c == 2);
    }

    /// <summary>
    /// Makes the faces of each connected piece wind the same way as their neighbours (flipping as few as it can), then
    /// turns closed pieces outward. Open pieces keep the majority's direction, unless <paramref name="openOutward"/>
    /// says which way each face should rather face (a positive score keeps the piece, a negative one flips it).
    /// Returns how many faces changed direction.
    /// </summary>
    public static int Orient(Mesh mesh, IEnumerable<int> polygons, Func<Polygon, double>? openOutward = null)
    {
        int changed = 0;
        foreach (var piece in Components(mesh, polygons))
        {
            var inPiece = piece.ToHashSet();
            var owners = new Dictionary<(int, int), List<int>>();
            foreach (int f in piece)
                foreach (var e in mesh.Polygons[f].Edges())
                {
                    if (!owners.TryGetValue(e, out var l)) owners[e] = l = [];
                    l.Add(f);
                }
            var flipped = new HashSet<int>();
            var done = new HashSet<int> { piece[0] };
            var queue = new Queue<int>([piece[0]]);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                foreach (var (a, b) in mesh.Polygons[f].Edges())
                {
                    // A neighbour that agrees runs (b, a); one that runs (a, b) as well needs flipping. Only faces not yet
                    // reached are looked at, and those still have the winding the owner map was built from.
                    foreach (var key in new[] { (a, b), (b, a) })
                    {
                        if (!owners.TryGetValue(key, out var list)) continue;
                        foreach (int g in list)
                        {
                            if (g == f || !inPiece.Contains(g) || !done.Add(g)) continue;
                            if (key == (a, b))
                            {
                                mesh.Polygons[g].Points.Reverse();
                                flipped.Add(g);
                            }
                            queue.Enqueue(g);
                        }
                    }
                }
            }
            mesh.Touch();
            bool flipAll;
            if (IsClosed(mesh, piece)) flipAll = MeshMeasure.Volume(mesh, piece) < 0;
            else if (openOutward != null) flipAll = piece.Sum(f => openOutward(mesh.Polygons[f])) < 0;
            else flipAll = flipped.Count * 2 > piece.Count;
            if (flipAll)
            {
                foreach (int f in piece) mesh.Polygons[f].Points.Reverse();
                changed += piece.Count - flipped.Count;
            }
            else changed += flipped.Count;
        }
        mesh.Touch();
        return changed;
    }
}
