using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Edge loops and rings, and the Loop Cut that inserts new loops. A loop runs straight on through points where four
/// quads meet (four edges, four quads), so it follows a row of a grid or a ring round a cylinder and stops wherever
/// that isn't so (a triangle or n-gon, a pole, the rim of a hole); an edge on a hole's rim loops round the rim instead.
/// A ring is the run of parallel edges across a strip of quads: from an edge, across the quad to the edge opposite,
/// and on, stopping at anything that isn't a quad or at the rim.
/// </summary>
public static class EdgeLoops
{
    /// <summary>The edge loop through an edge, the edge included (in walking order, not closed up).</summary>
    public static List<Edge> Loop(Mesh mesh, Edge start)
    {
        var map = mesh.EdgeMap();
        if (!map.TryGetValue(start, out var owners)) return [];
        bool rim = Faces(mesh, owners).Count == 1;
        var result = new List<Edge> { start };
        var seen = new HashSet<Edge> { start };
        foreach (var (from, to) in new[] { (start.A, start.B), (start.B, start.A) })
        {
            var forward = new List<Edge>();
            int prev = from, cur = to;
            while (true)
            {
                int next = rim ? NextOnRim(mesh, prev, cur) : NextAcross(mesh, prev, cur);
                if (next < 0) break;
                var e = Edge.Of(cur, next);
                if (!seen.Add(e)) break;
                forward.Add(e);
                (prev, cur) = (cur, next);
            }
            if (from == start.A) result.AddRange(forward);
            else result.InsertRange(0, forward.AsEnumerable().Reverse());
        }
        return result;
    }

    /// <summary>The edge ring through an edge, the edge included, in order across the strip of quads.</summary>
    public static List<Edge> Ring(Mesh mesh, Edge start) => Strip(mesh, start).Edges.Select(e => Edge.Of(e.A, e.B)).ToList();

    /// <summary>
    /// The strip of quads an edge's ring crosses: the ring's edges in order, each turned so that its first point is on
    /// the same side as the one before's (the quads' sides join first points to first points); the quads between
    /// neighbouring edges (quad i lies between edge i and edge i + 1, and for a closed ring the last between the last
    /// edge and the first); and whether the ring closes on itself.
    /// </summary>
    public static (List<(int A, int B)> Edges, List<int> Quads, bool Closed) Strip(Mesh mesh, Edge start)
    {
        var map = mesh.EdgeMap();
        if (!map.TryGetValue(start, out var owners)) return ([], [], false);
        var faces = Faces(mesh, owners);
        var seenEdges = new HashSet<Edge> { start };
        var seenQuads = new HashSet<int>();
        var sides = new List<(List<(int, int)> Edges, List<int> Quads)>();
        bool closed = false;
        foreach (int first in faces.Take(2))
        {
            var edges = new List<(int, int)>();
            var quads = new List<int>();
            (int A, int B) cur = (start.A, start.B);
            int face = first;
            while (face >= 0 && mesh.Polygons[face].Count == 4 && seenQuads.Add(face))
            {
                var opposite = Opposite(mesh.Polygons[face], cur.A, cur.B);
                quads.Add(face);
                var e = Edge.Of(opposite.A, opposite.B);
                if (e == start) { closed = true; break; }
                if (!seenEdges.Add(e)) break;
                edges.Add(opposite);
                cur = opposite;
                var others = Faces(mesh, map[e]).Where(f => f != face).ToList();
                face = others.Count == 1 ? others[0] : -1;
            }
            sides.Add((edges, quads));
            if (closed) break;
        }
        if (closed)
        {
            var (edges, quads) = sides[^1];
            return ([(start.A, start.B), .. edges], quads, true);
        }
        // Open: the far side reversed, the start, then the near side; the quads between them in the same order.
        var near = sides.Count > 0 ? sides[0] : ([], []);
        var far = sides.Count > 1 ? sides[1] : ([], []);
        var allEdges = new List<(int A, int B)>();
        var allQuads = new List<int>();
        // Quad i of a side lies between the edge before (the start, for the first) and that side's edge i.
        for (int i = far.Edges.Count - 1; i >= 0; i--)
        {
            allEdges.Add(far.Edges[i]);
            allQuads.Add(far.Quads[i]);
        }
        allEdges.Add((start.A, start.B));
        for (int i = 0; i < near.Edges.Count; i++)
        {
            allQuads.Add(near.Quads[i]);
            allEdges.Add(near.Edges[i]);
        }
        return (allEdges, allQuads, false);
    }

    /// <summary>
    /// Loop Cut: inserts <paramref name="count"/> new edge loops across the strip of quads the ring through
    /// <paramref name="start"/> crosses, splitting each quad into count + 1. One cut sits at <paramref name="position"/>
    /// along each ring edge (from 0 at its first point to 1 at its last, measured the same way round the whole ring);
    /// several are spread evenly. Faces beyond the ends of an open ring (a triangle, an n-gon) take the new points into
    /// their edges, so the mesh stays joined. New corners take UVs interpolated along their edges, face by face, so
    /// seams stay seams. Each quad's first piece keeps its index. Returns the new edges along the cuts.
    /// </summary>
    public static List<Edge> LoopCut(Mesh mesh, Edge start, double position = 0.5, int count = 1)
    {
        var (edges, quads, closed) = Strip(mesh, start);
        if (edges.Count == 0 || quads.Count == 0) return [];
        count = Math.Max(count, 1);
        double[] ts = count == 1
            ? [Math.Clamp(position, 1e-3, 1 - 1e-3)]
            : Enumerable.Range(1, count).Select(k => (double)k / (count + 1)).ToArray();

        // The new points along each ring edge, from its first point to its last.
        var along = new List<int[]>();
        foreach (var (a, b) in edges)
        {
            var pts = new int[ts.Length];
            for (int k = 0; k < ts.Length; k++) pts[k] = mesh.AddPoint(Vec3.Lerp(mesh.Points[a], mesh.Points[b], ts[k]));
            along.Add(pts);
        }
        var quadSet = quads.ToHashSet();
        var byEdge = new Dictionary<Edge, int>();
        for (int i = 0; i < edges.Count; i++) byEdge[Edge.Of(edges[i].A, edges[i].B)] = i;

        // Faces off the ends of the strip take the new points into their edges.
        var map = mesh.EdgeMap();
        foreach (var (e, i) in byEdge)
            foreach (int f in map[e].ToList())
                if (!quadSet.Contains(f) && mesh.Polygons[f].Count >= 3)
                    InsertAlong(mesh, f, edges[i], along[i], ts);

        var made = new List<Edge>();
        for (int q = 0; q < quads.Count; q++)
        {
            int i0 = q, i1 = (q + 1) % edges.Count;
            if (!closed && q + 1 >= edges.Count) break;
            var poly = mesh.Polygons[quads[q]];
            var (x0, y0) = edges[i0];
            var (x1, y1) = edges[i1];
            var uvs = FaceUVs.Of(mesh, poly);
            // Lines across the quad, from the x side to the y side: its own side first, the cuts, then the far side.
            var lines = new List<(int A, int B)> { (x0, x1) };
            for (int k = 0; k < ts.Length; k++)
            {
                int p = along[i0][k], r = along[i1][k];
                lines.Add((p, r));
                made.Add(Edge.Of(p, r));
                foreach (var at in uvs.Values)
                {
                    at[p] = Lerp(at[x0], at[y0], ts[k]);
                    at[r] = Lerp(at[x1], at[y1], ts[k]);
                }
            }
            lines.Add((y0, y1));
            int ix = poly.Points.IndexOf(x0);
            bool xToY = poly.Points[(ix + 1) % 4] == y0;
            var pieces = new List<List<int>>();
            for (int j = 0; j + 1 < lines.Count; j++)
            {
                var (a0, b0) = lines[j];
                var (a1, b1) = lines[j + 1];
                pieces.Add(xToY ? [a0, a1, b1, b0] : [a0, b0, b1, a1]);
            }
            poly.Points = pieces[0];
            poly.CornerUVs = null;
            poly.Details = null;
            FaceUVs.Set(mesh, poly, uvs);
            for (int j = 1; j < pieces.Count; j++)
            {
                var piece = new Polygon(pieces[j], poly.Surface);
                mesh.Polygons.Add(piece);
                FaceUVs.Set(mesh, piece, uvs);
            }
        }
        mesh.Touch();
        return made;
    }

    /// <summary>Puts points along an edge (listed from its first point to its last) into a face that uses the edge.</summary>
    private static void InsertAlong(Mesh mesh, int face, (int A, int B) edge, int[] points, double[] ts)
    {
        var poly = mesh.Polygons[face];
        var uvs = FaceUVs.Of(mesh, poly);
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            int a = poly.Points[i], b = poly.Points[(i + 1) % n];
            if (Edge.Of(a, b) != Edge.Of(edge.A, edge.B)) continue;
            bool forward = a == edge.A;
            var insert = forward ? points : points.Reverse().ToArray();
            foreach (var at in uvs.Values)
                for (int k = 0; k < points.Length; k++) at[points[k]] = Lerp(at[edge.A], at[edge.B], ts[k]);
            var pts = poly.Points.ToList();
            pts.InsertRange(i + 1, insert);
            poly.Points = pts;
            FaceUVs.Set(mesh, poly, uvs);
            mesh.Touch();
            return;
        }
    }

    /// <summary>The edge of a quad opposite one of its edges, turned so its first point is next to <paramref name="a"/>.</summary>
    private static (int A, int B) Opposite(Polygon quad, int a, int b)
    {
        var p = quad.Points;
        int i = p.IndexOf(a);
        int after = p[(i + 1) % 4], before = p[(i + 3) % 4];
        int nextToA = after == b ? before : after;
        int j = p.IndexOf(b);
        int bAfter = p[(j + 1) % 4], bBefore = p[(j + 3) % 4];
        int nextToB = bAfter == a ? bBefore : bAfter;
        return (nextToA, nextToB);
    }

    /// <summary>Where a loop goes on across a point where four quads meet: the edge that shares no face with the one it came in by.</summary>
    private static int NextAcross(Mesh mesh, int prev, int cur)
    {
        var faces = Faces(mesh, mesh.PolygonsOf(cur));
        if (faces.Count != 4 || faces.Any(f => mesh.Polygons[f].Count != 4)) return -1;
        var neighbours = new HashSet<int>();
        foreach (int f in faces)
        {
            var p = mesh.Polygons[f].Points;
            int i = p.IndexOf(cur);
            neighbours.Add(p[(i + 1) % 4]);
            neighbours.Add(p[(i + 3) % 4]);
        }
        if (neighbours.Count != 4 || !neighbours.Contains(prev)) return -1;
        // The neighbours sharing a face with the incoming edge are beside it; the one left is straight on.
        foreach (int f in faces)
        {
            var p = mesh.Polygons[f].Points;
            if (!p.Contains(prev)) continue;
            int i = p.IndexOf(cur);
            neighbours.Remove(p[(i + 1) % 4]);
            neighbours.Remove(p[(i + 3) % 4]);
        }
        return neighbours.Count == 1 ? neighbours.First() : -1;
    }

    /// <summary>Where a loop along a hole's rim goes on: the point's one other rim edge.</summary>
    private static int NextOnRim(Mesh mesh, int prev, int cur)
    {
        var map = mesh.EdgeMap();
        int found = -1;
        foreach (int n in mesh.Neighbours(cur))
        {
            if (n == prev) continue;
            if (map.TryGetValue(Edge.Of(cur, n), out var owners) && Faces(mesh, owners).Count == 1)
            {
                if (found >= 0) return -1;
                found = n;
            }
        }
        return found;
    }

    /// <summary>The faces (three points or more) among some polygons.</summary>
    internal static List<int> Faces(Mesh mesh, IEnumerable<int> polygons) => polygons.Where(f => mesh.Polygons[f].Count >= 3).ToList();

    internal static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;
}

/// <summary>A face's UVs by point, map by map, for tools that rebuild faces and give the pieces their UVs.</summary>
internal static class FaceUVs
{
    /// <summary>Each map's UVs at a face's corners, by point (only maps that cover the whole face).</summary>
    public static Dictionary<string, Dictionary<int, Vec2>> Of(Mesh mesh, Polygon poly)
    {
        var result = new Dictionary<string, Dictionary<int, Vec2>>();
        foreach (var map in mesh.UVMaps)
        {
            var uvs = mesh.CornerUVsOf(map.Name, poly);
            if (uvs.Length == 0 || uvs.Any(u => u == null)) continue;
            var at = new Dictionary<int, Vec2>();
            for (int i = 0; i < uvs.Length; i++) at[poly.Points[i]] = uvs[i]!.Value;
            result[map.Name] = at;
        }
        return result;
    }

    /// <summary>Gives a polygon's corners the UVs listed for their points, map by map.</summary>
    public static void Set(Mesh mesh, Polygon poly, Dictionary<string, Dictionary<int, Vec2>> uvs)
    {
        foreach (var (name, at) in uvs)
            foreach (int q in poly.Points.Distinct())
                if (at.TryGetValue(q, out var uv)) mesh.SetCornerUV(name, poly, q, uv);
    }
}
