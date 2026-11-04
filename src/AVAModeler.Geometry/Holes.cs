using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How Fill Hole closes a hole.</summary>
public enum FillMode
{
    /// <summary>One polygon through the rim's points.</summary>
    NGon,
    /// <summary>The polygon cut into triangles (properly, for holes that aren't convex).</summary>
    Triangles,
    /// <summary>A ladder of quads across the hole, with a triangle at the end of an odd one.</summary>
    Quads,
}

/// <summary>
/// Holes and the tools that close or join them: the rims of holes as loops (from <see cref="MeshCheck"/>'s boundary
/// edges), Fill Hole, and Bridge, which joins two loops (or two faces, or two holes) with a band of quads. New faces
/// wind against the faces beside them, LightWave's clockwise way, so a mesh they close is consistent and faces out.
/// </summary>
public static class Holes
{
    /// <summary>
    /// The holes in a mesh: its boundary edges (as the mesh check finds them) joined into loops of points, each running
    /// the way the faces beside it run along it. A face filling a hole runs the other way.
    /// </summary>
    public static List<List<int>> BoundaryLoops(Mesh mesh) => Topology.ChainBoundary(mesh, MeshCheck.Mesh(mesh).BoundaryEdges);

    /// <summary>
    /// Fill Hole: closes each hole (a loop from <see cref="BoundaryLoops"/>) with one polygon, triangles, or a ladder of
    /// quads, wound against its neighbours, on the surface of the face beside the rim. Returns the new polygons' indices.
    /// </summary>
    public static List<int> Fill(Mesh mesh, IEnumerable<IReadOnlyList<int>> loops, FillMode mode = FillMode.NGon)
    {
        var made = new List<int>();
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            int surface = Math.Max(SurfaceBeside(mesh, loop[0], loop[1]), 0);
            var p = loop.Reverse().ToList();
            int n = p.Count;
            if (mode == FillMode.Quads && n > 4)
            {
                int k = 0;
                for (; k + 3 < n - k; k++)
                    made.Add(mesh.AddPolygon([p[k], p[k + 1], p[n - 2 - k], p[n - 1 - k]], surface));
                // What's left in the middle: a quad or a triangle.
                var rest = Enumerable.Range(k, n - 2 * k).Select(i => p[i]).ToList();
                if (rest.Count >= 3) made.Add(mesh.AddPolygon(rest, surface));
                continue;
            }
            int face = mesh.AddPolygon(p, surface);
            if (mode == FillMode.Triangles && n > 3)
            {
                int before = mesh.Polygons.Count;
                Triangulator.Triple(mesh, [face]);
                made.Add(face);
                made.AddRange(Enumerable.Range(before, mesh.Polygons.Count - before));
            }
            else made.Add(face);
        }
        mesh.Touch();
        return made;
    }

    /// <summary>
    /// Bridge between two runs of edges (each a closed loop or an open chain, with the same number of points): a band
    /// of quads <paramref name="segments"/> deep joins them. Edges on a hole's rim set which way the band winds (against
    /// the faces beside them); loose edges (lines) can go either way. The pairing of points (and, for loops, where to
    /// start) is the one that twists least: the smallest summed distance between paired points, each measured from its
    /// loop's centre. Lines along the joined edges are removed, as the band now has those edges. Returns the new faces.
    /// </summary>
    /// <exception cref="ArgumentException">The edges don't make two runs of the same kind and length, or an edge already has faces on both sides.</exception>
    public static List<int> BridgeEdges(Mesh mesh, IEnumerable<Edge> edges, int segments = 1)
    {
        var set = edges.ToHashSet();
        var runs = Runs(set);
        if (runs.Count != 2)
            throw new ArgumentException($"Bridge joins two loops or chains of edges, but the edges selected make {runs.Count}. Select the rims of two holes (Select Loop on one edge of each), or two faces.");
        var map = mesh.EdgeMap();
        var a = Directed(mesh, map, runs[0].Points, runs[0].Closed);
        var b = Directed(mesh, map, runs[1].Points, runs[1].Closed);
        var lines = Enumerable.Range(0, mesh.Polygons.Count)
            .Where(i => mesh.Polygons[i].Count == 2 && set.Contains(Edge.Of(mesh.Polygons[i].Points[0], mesh.Polygons[i].Points[1]))).ToList();
        var made = Join(mesh, a, b, segments);
        if (lines.Count > 0)
        {
            var keep = made.Select(i => mesh.Polygons[i]).ToList();
            mesh.RemovePolygons(lines);
            made = keep.Select(p => mesh.Polygons.IndexOf(p)).ToList();
        }
        return made;
    }

    /// <summary>
    /// Bridge between two pieces of faces (say one face on each of two boxes): the faces are removed and their outlines
    /// joined by a band of quads, so the two become one shape. Each piece must have one outline, and both the same
    /// number of points. Points only the removed faces used go too. Returns the new faces.
    /// </summary>
    public static List<int> BridgeFaces(Mesh mesh, IEnumerable<int> polygons, int segments = 1)
    {
        var pieces = Topology.Components(mesh, polygons);
        if (pieces.Count != 2)
            throw new ArgumentException($"Bridge joins two faces (or two pieces of faces), but the polygons selected make {pieces.Count} piece{(pieces.Count == 1 ? "" : "s")}.");
        var outlines = new List<Run>();
        foreach (var piece in pieces)
        {
            var counts = Topology.FaceEdgeCounts(mesh, piece);
            var rim = new List<(int, int)>();
            foreach (int f in piece)
                foreach (var (x, y) in mesh.Polygons[f].Edges())
                    if (x != y && counts[Edge.Of(x, y)] == 1) rim.Add((x, y));
            var loops = Topology.ChainDirected(rim);
            if (loops.Count != 1)
                throw new ArgumentException($"Each piece Bridge joins needs one outline; one has {loops.Count}.");
            // The band runs along each outline the way the removed faces did.
            outlines.Add(new Run(loops[0], Closed: true, Fixed: true));
        }
        var removed = pieces.SelectMany(p => p).ToList();
        var used = mesh.PointsOf(removed);
        var keepOutline = outlines.SelectMany(o => o.Points).ToHashSet();
        var made = Join(mesh, outlines[0], outlines[1], segments).Select(i => mesh.Polygons[i]).ToList();
        mesh.RemovePolygons(removed);
        var stillUsed = mesh.PointsOf(Enumerable.Range(0, mesh.Polygons.Count));
        mesh.RemovePoints(used.Where(p => !keepOutline.Contains(p) && !stillUsed.Contains(p)));
        return made.Select(p => mesh.Polygons.IndexOf(p)).ToList();
    }

    /// <summary>Bridge between the mesh's two holes (say the open ends of a tube); there must be exactly two.</summary>
    public static List<int> BridgeHoles(Mesh mesh, int segments = 1)
    {
        var loops = BoundaryLoops(mesh);
        if (loops.Count != 2)
            throw new ArgumentException($"With nothing selected, Bridge joins the foreground's two holes, but it has {loops.Count}. Select two loops of edges, or two faces.");
        return Join(mesh, new Run(Reversed(loops[0]), true, true), new Run(Reversed(loops[1]), true, true), segments);
    }

    /// <summary>A run of points: a loop or a chain, in the direction the band must run along it (when that's fixed).</summary>
    private sealed record Run(List<int> Points, bool Closed, bool Fixed);

    private static List<int> Reversed(List<int> loop) => loop.AsEnumerable().Reverse().ToList();

    /// <summary>Joins two runs with a band of quads, choosing the pairing that twists least.</summary>
    private static List<int> Join(Mesh mesh, Run a, Run b, int segments)
    {
        int n = a.Points.Count;
        if (a.Closed != b.Closed)
            throw new ArgumentException("Bridge needs two closed loops or two open chains, not one of each.");
        if (b.Points.Count != n)
            throw new ArgumentException($"Bridge needs the same number of points on both sides, but one has {n} and the other {b.Points.Count}. Loop Cut, Subdivide or Split the side with fewer to match.");
        if (n < (a.Closed ? 3 : 2)) throw new ArgumentException("Bridge needs at least three points round each loop (two along each chain).");
        if (!a.Fixed && b.Fixed) (a, b) = (b, a);
        segments = Math.Max(segments, 1);
        var p = a.Points;
        var ca = Centre(mesh, p);
        var cb = Centre(mesh, b.Points);
        // Paired point of p[i] is q[m(i)], with m running backwards: the band crosses each side the way it must.
        List<int>? best = null;
        double bestCost = double.PositiveInfinity;
        foreach (var q in b.Fixed ? [b.Points] : new[] { b.Points, Reversed(b.Points) })
            for (int s = 0; s < (a.Closed ? n : 1); s++)
            {
                var pairs = new List<int>(n);
                double cost = 0;
                for (int i = 0; i < n; i++)
                {
                    int m = a.Closed ? ((s - i) % n + n) % n : n - 1 - i;
                    pairs.Add(q[m]);
                    cost += ((mesh.Points[p[i]] - ca) - (mesh.Points[q[m]] - cb)).LengthSquared;
                }
                if (cost < bestCost - 1e-12)
                {
                    bestCost = cost;
                    best = pairs;
                }
            }
        var far = best!;
        int surface = SurfaceBeside(mesh, p[0], p[1]);
        if (surface < 0) surface = SurfaceBeside(mesh, far[0], far[^1]);
        if (surface < 0) surface = 0;
        // The rings of points from one side to the other, the in-between ones new.
        var rings = new List<int[]> { p.ToArray() };
        for (int k = 1; k < segments; k++)
        {
            var ring = new int[n];
            for (int i = 0; i < n; i++)
            {
                ring[i] = mesh.AddPoint(Vec3.Lerp(mesh.Points[p[i]], mesh.Points[far[i]], (double)k / segments));
                foreach (var map in mesh.UVMaps)
                    if (map.Points.TryGetValue(p[i], out var u0) && map.Points.TryGetValue(far[i], out var u1))
                        map.Points[ring[i]] = EdgeLoops.Lerp(u0, u1, (double)k / segments);
            }
            rings.Add(ring);
        }
        rings.Add([.. far]);
        var made = new List<int>();
        for (int k = 0; k < segments; k++)
            for (int i = 0; i < (a.Closed ? n : n - 1); i++)
            {
                int j = (i + 1) % n;
                made.Add(mesh.AddPolygon([rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]], surface));
            }
        if (!a.Fixed && !b.Fixed) Topology.Orient(mesh, made);
        mesh.Touch();
        return made;
    }

    /// <summary>Splits edges into connected runs, each a simple loop or chain of points in order, and whether it closes.</summary>
    private static List<(List<int> Points, bool Closed)> Runs(HashSet<Edge> edges)
    {
        var next = new Dictionary<int, List<int>>();
        foreach (var e in edges)
        {
            (next.TryGetValue(e.A, out var a) ? a : next[e.A] = []).Add(e.B);
            (next.TryGetValue(e.B, out var b) ? b : next[e.B] = []).Add(e.A);
        }
        if (next.Values.Any(l => l.Count > 2))
            throw new ArgumentException("Bridge needs simple loops or chains of edges: a point of the selection has more than two selected edges.");
        var seen = new HashSet<int>();
        var runs = new List<(List<int>, bool)>();
        // Chains start at an end; whatever is left after them is loops.
        var starts = next.Keys.Where(k => next[k].Count == 1).Order().Concat(next.Keys.Where(k => next[k].Count != 1).Order());
        foreach (int start in starts)
        {
            if (!seen.Add(start)) continue;
            var run = new List<int> { start };
            int at = start;
            while (next[at].FirstOrDefault(x => !seen.Contains(x), -1) is int to and >= 0)
            {
                run.Add(to);
                seen.Add(to);
                at = to;
            }
            bool closed = run.Count >= 3 && edges.Contains(Edge.Of(run[0], run[^1]));
            runs.Add((run, closed));
        }
        return runs;
    }

    /// <summary>
    /// Gives a run of points its kind and direction: closed if its ends are joined; fixed (running against the face
    /// beside each edge) if its edges are on a hole's rim, free if they're loose.
    /// </summary>
    private static Run Directed(Mesh mesh, IReadOnlyDictionary<Edge, List<int>> map, List<int> points, bool closed)
    {
        int along = 0, against = 0;
        int count = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < count; i++)
        {
            int x = points[i], y = points[(i + 1) % points.Count];
            var faces = map.TryGetValue(Edge.Of(x, y), out var o) ? EdgeLoops.Faces(mesh, o) : [];
            if (faces.Count > 1)
                throw new ArgumentException($"Bridge joins open edges (the rims of holes, or loose lines), but the edge from point {x} to point {y} already has faces on both sides.");
            if (faces.Count == 0) continue;
            var poly = mesh.Polygons[faces[0]];
            int k = poly.Points.IndexOf(x);
            if (poly.Points[(k + 1) % poly.Count] == y) along++;
            else against++;
        }
        bool isFixed = along + against > 0;
        // The band runs against the faces beside the rim.
        var ordered = along > against ? Reversed(points) : points;
        return new Run(ordered, closed, isFixed);
    }

    private static Vec3 Centre(Mesh mesh, List<int> points) => points.Aggregate(Vec3.Zero, (s, i) => s + mesh.Points[i]) / points.Count;

    /// <summary>The surface of a face beside an edge (-1 if none).</summary>
    private static int SurfaceBeside(Mesh mesh, int a, int b) =>
        mesh.EdgeMap().TryGetValue(Edge.Of(a, b), out var owners) && owners.FirstOrDefault(f => mesh.Polygons[f].Count >= 3, -1) is int f and >= 0
            ? mesh.Polygons[f].Surface
            : -1;
}
