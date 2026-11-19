using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Smooth and faceted subdivision, and smoothing. Catmull–Clark drives the MetaNURBS cage preview and Metaform; it
/// takes any mix of triangles, quads and n-gons. Open edges and edges shared by more than two faces are treated as
/// creases (their new points are midpoints, and points along a single crease follow it as a curve). UVs are
/// subdivided flat: a new edge point takes the middle of its edge's UVs and a face point the average of its face's,
/// on each face separately, so seams stay seams.
/// </summary>
public static class Subdivision
{
    /// <summary>
    /// A new mesh, the whole of <paramref name="mesh"/> put through <paramref name="levels"/> rounds of Catmull–Clark
    /// (for the MetaNURBS preview). Each face of n points becomes n quads; points and lines ride along attached to
    /// their (moved) points. The original points keep their indices.
    /// </summary>
    public static Mesh CatmullClark(Mesh mesh, int levels = 1)
    {
        var m = mesh.Clone();
        for (int i = 0; i < levels; i++) CatmullClarkStep(m, null);
        return m;
    }

    /// <summary>
    /// A SubPatch surface: <paramref name="levels"/> rounds of Catmull–Clark on the faces given (or all), with the
    /// mesh's creases (<see cref="SubPatches.Sharpness"/>) as semi-sharp edges (DeRose, Kass and Truong 1998): an edge
    /// of sharpness s is subdivided as a crease for its first ⌊s⌋ rounds, then blended by the fraction left, then
    /// smooth. Faces outside the set keep their shape and are stitched to the new edge points.
    /// </summary>
    public static Mesh CatmullClark(Mesh mesh, int levels, IEnumerable<int>? faces, IReadOnlyDictionary<Edge, double>? sharpness)
    {
        var m = mesh.Clone();
        var only = faces?.ToHashSet();
        var sharp = sharpness == null ? null : new Dictionary<Edge, double>(sharpness);
        for (int i = 0; i < levels; i++)
        {
            int before = m.Polygons.Count;
            sharp = CatmullClarkStep(m, only, sharp);
            // The faces made from the chosen ones are the chosen ones next round: their first pieces keep their
            // indices and the rest come after every face there was.
            if (only != null) only = [.. only, .. Enumerable.Range(before, m.Polygons.Count - before)];
        }
        return m;
    }

    /// <summary>
    /// Metaform: one round of Catmull–Clark baked into the mesh, on the faces given (or all). Points shared with faces
    /// outside the selection stay where they are, so the rest of the mesh doesn't move; the new points on the
    /// selection's edge are put into the neighbouring faces too, so the mesh stays stitched. Each face's first quad
    /// takes its index; the rest are added at the end.
    /// </summary>
    public static void Metaform(Mesh mesh, IEnumerable<int>? polygons = null) =>
        CatmullClarkStep(mesh, polygons?.ToHashSet(), SubPatches.Sharpness(mesh));

    /// <summary>
    /// Faceted subdivide on the faces given (or all): each triangle becomes four (corners and middle), and each face of
    /// four or more points becomes quads round its centre, one per corner. Midpoints are shared between neighbouring
    /// faces and put into unselected neighbours, so nothing moves and the mesh stays stitched. Each face's first piece
    /// takes its index; the rest are added at the end.
    /// </summary>
    public static void Faceted(Mesh mesh, IEnumerable<int>? polygons = null)
    {
        var faces = Faces(mesh, polygons?.ToHashSet());
        if (faces.Count == 0) return;
        int before = mesh.Polygons.Count;
        var mid = new Dictionary<Edge, int>();
        int Mid(int a, int b)
        {
            var e = Edge.Of(a, b);
            if (!mid.TryGetValue(e, out int i)) mid[e] = i = Add(mesh, (mesh.Points[a] + mesh.Points[b]) / 2);
            return i;
        }
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            var v = poly.Points.ToArray();
            int n = v.Length;
            var m = new int[n];
            for (int i = 0; i < n; i++) m[i] = Mid(v[i], v[(i + 1) % n]);
            var pieces = new List<int[]>();
            int c = -1;
            if (n == 3)
                pieces.AddRange([[v[0], m[0], m[2]], [m[0], v[1], m[1]], [m[2], m[1], v[2]], [m[0], m[1], m[2]]]);
            else
            {
                c = Add(mesh, mesh.Centroid(poly));
                for (int i = 0; i < n; i++) pieces.Add([v[i], m[i], c, m[(i + n - 1) % n]]);
            }
            var uvs = FaceUVs(mesh, poly, v, i => m[i], c);
            SetUVs(mesh, Replace(mesh, f, pieces), uvs);
        }
        InsertIntoNeighbours(mesh, faces.ToHashSet(), before, mid);
        mesh.Touch();
    }

    /// <summary>
    /// The Smooth command: moves each point given (or all) a fraction <paramref name="strength"/> of the way toward
    /// the average of its neighbours, <paramref name="iterations"/> times (Laplacian smoothing). With
    /// <paramref name="preserveVolume"/>, each step is followed by a step back out (Taubin's λ|μ smoothing), which
    /// smooths without shrinking. Points with no neighbours stay put.
    /// </summary>
    public static void Smooth(Mesh mesh, IEnumerable<int>? points, double strength = 0.5, int iterations = 1, bool preserveVolume = false)
    {
        var chosen = (points ?? Enumerable.Range(0, mesh.Points.Count)).Distinct().ToArray();
        var neighbours = chosen.Select(i => mesh.Neighbours(i).ToArray()).ToArray();
        // Taubin's pass band of 0.1 sets the inflating step from the shrinking one.
        double mu = strength / (0.1 * strength - 1);
        for (int it = 0; it < iterations; it++)
        {
            Step(strength);
            if (preserveVolume) Step(mu);
        }
        mesh.Touch();

        void Step(double factor)
        {
            var moved = new Vec3[chosen.Length];
            for (int k = 0; k < chosen.Length; k++)
            {
                var p = mesh.Points[chosen[k]];
                var nb = neighbours[k];
                if (nb.Length == 0) { moved[k] = p; continue; }
                var avg = nb.Aggregate(Vec3.Zero, (s, j) => s + mesh.Points[j]) / nb.Length;
                moved[k] = p + (avg - p) * factor;
            }
            for (int k = 0; k < chosen.Length; k++) mesh.Points[chosen[k]] = moved[k];
        }
    }

    private static void CatmullClarkStep(Mesh mesh, HashSet<int>? only) => CatmullClarkStep(mesh, only, null);

    /// <summary>One round, with edges' sharpness (null: none); returns the new edges' sharpness, a round less.</summary>
    private static Dictionary<Edge, double>? CatmullClarkStep(Mesh mesh, HashSet<int>? only, Dictionary<Edge, double>? sharp)
    {
        var faces = Faces(mesh, only);
        if (faces.Count == 0) return sharp;
        if (sharp is { Count: 0 }) sharp = null;
        double SharpOf(Edge e) => sharp != null && sharp.TryGetValue(e, out double v) ? v : 0;
        var selected = faces.ToHashSet();
        var points = mesh.Points;

        // Every face (selected or not) on each edge, and whether a point touches a face outside the selection.
        var edgeFaces = new Dictionary<Edge, List<int>>();
        var pinned = new HashSet<int>();
        for (int f = 0; f < mesh.Polygons.Count; f++)
        {
            var poly = mesh.Polygons[f];
            if (poly.Count < 3) continue;
            if (!selected.Contains(f)) pinned.UnionWith(poly.Points);
            foreach (var (a, b) in poly.Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (!edgeFaces.TryGetValue(e, out var l)) edgeFaces[e] = l = [];
                if (!l.Contains(f)) l.Add(f);
            }
        }

        var facePoint = faces.ToDictionary(f => f, f => mesh.Centroid(mesh.Polygons[f]));
        bool Smooth(Edge e) => edgeFaces[e] is { Count: 2 } l && selected.Contains(l[0]) && selected.Contains(l[1]);

        var edgePos = new Dictionary<Edge, Vec3>();
        var incident = new Dictionary<int, List<Edge>>();
        var touching = new Dictionary<int, List<int>>();
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            foreach (int p in poly.Points.Distinct())
            {
                if (!touching.TryGetValue(p, out var t)) touching[p] = t = [];
                t.Add(f);
            }
            foreach (var (a, b) in poly.Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (edgePos.ContainsKey(e)) continue;
                var mid = (points[a] + points[b]) / 2;
                if (!Smooth(e)) edgePos[e] = mid;
                else
                {
                    var round = (points[a] + points[b] + facePoint[edgeFaces[e][0]] + facePoint[edgeFaces[e][1]]) / 4;
                    double sh = SharpOf(e);
                    edgePos[e] = sh >= 1 ? mid : sh > 0 ? Vec3.Lerp(round, mid, sh) : round;
                }
                foreach (int p in new[] { a, b })
                {
                    if (!incident.TryGetValue(p, out var l)) incident[p] = l = [];
                    l.Add(e);
                }
            }
        }

        var newPos = new Dictionary<int, Vec3>();
        foreach (var (p, edges) in incident)
        {
            if (pinned.Contains(p)) continue;
            var creases = edges.Where(e => !Smooth(e)).ToList();
            var P = points[p];
            Vec3? SmoothRule()
            {
                if (edges.Count < 3) return null;
                int n = edges.Count;
                var F = touching[p].Aggregate(Vec3.Zero, (s, f) => s + facePoint[f]) / touching[p].Count;
                var R = edges.Aggregate(Vec3.Zero, (s, e) => s + (points[e.A] + points[e.B]) / 2) / n;
                return (F + 2 * R + (n - 3) * P) / n;
            }
            if (sharp == null || creases.Count > 0)
            {
                // Open and non-manifold edges are creases for ever: the rules as they always were.
                if (creases.Count == 0 && SmoothRule() is { } sm) newPos[p] = sm;
                else if (creases.Count == 2)
                    newPos[p] = P * 0.75 + (points[creases[0].Other(p)] + points[creases[1].Other(p)]) * 0.125;
                continue;
            }
            var sharpEdges = edges.Where(e => SharpOf(e) > 0).ToList();
            var smooth = SmoothRule();
            if (sharpEdges.Count < 2) { if (smooth is { } sm) newPos[p] = sm; continue; }
            var hard = sharpEdges.Count == 2
                ? P * 0.75 + (points[sharpEdges[0].Other(p)] + points[sharpEdges[1].Other(p)]) * 0.125
                : P;
            double amount = sharpEdges.Average(SharpOf);
            newPos[p] = amount >= 1 || smooth == null ? hard : Vec3.Lerp(smooth.Value, hard, amount);
        }

        int before = mesh.Polygons.Count;
        var edgePoint = new Dictionary<Edge, int>();
        foreach (var (e, pos) in edgePos) edgePoint[e] = Add(mesh, pos);
        foreach (var (p, pos) in newPos) points[p] = pos;
        foreach (int f in faces)
        {
            var v = mesh.Polygons[f].Points.ToArray();
            int n = v.Length;
            int c = Add(mesh, facePoint[f]);
            var pieces = new List<int[]>(n);
            for (int i = 0; i < n; i++)
            {
                int a = v[i], next = v[(i + 1) % n], prev = v[(i + n - 1) % n];
                if (a == next || a == prev) continue;
                pieces.Add([a, edgePoint[Edge.Of(a, next)], c, edgePoint[Edge.Of(prev, a)]]);
            }
            var uvs = FaceUVs(mesh, mesh.Polygons[f], v, i => v[i] == v[(i + 1) % n] ? -1 : edgePoint[Edge.Of(v[i], v[(i + 1) % n])], c);
            SetUVs(mesh, Replace(mesh, f, pieces), uvs);
        }
        InsertIntoNeighbours(mesh, selected, before, edgePoint);
        mesh.Touch();
        if (sharp == null) return null;
        // Each sharp edge's halves are a round less sharp; an infinitely sharp one stays so.
        var child = new Dictionary<Edge, double>();
        foreach (var (e, sh) in sharp)
        {
            if (!edgePoint.TryGetValue(e, out int m)) { if (sh > 0) child[e] = sh; continue; }
            double left = sh >= SubPatches.Infinite ? sh : sh - 1;
            if (left <= 0) continue;
            child[Edge.Of(e.A, m)] = left;
            child[Edge.Of(m, e.B)] = left;
        }
        return child;
    }

    /// <summary>The faces (3 or more points) among those given, or all of them.</summary>
    private static List<int> Faces(Mesh mesh, HashSet<int>? only) =>
        Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Count >= 3 && (only == null || only.Contains(i))).ToList();

    private static int Add(Mesh mesh, Vec3 p)
    {
        mesh.Points.Add(p);
        return mesh.Points.Count - 1;
    }

    /// <summary>Puts the first piece in the face's place and the rest at the end, with the face's surface; returns the pieces.</summary>
    private static List<Polygon> Replace(Mesh mesh, int face, List<int[]> pieces)
    {
        var poly = mesh.Polygons[face];
        if (pieces.Count == 0) return [];
        poly.Points = [.. pieces[0]];
        poly.Details = null;
        poly.CornerUVs = null;
        var made = new List<Polygon> { poly };
        for (int i = 1; i < pieces.Count; i++)
        {
            var p = new Polygon(pieces[i], poly.Surface);
            mesh.Polygons.Add(p);
            made.Add(p);
        }
        return made;
    }

    /// <summary>
    /// The UVs a face's pieces will have, per map (only maps covering the whole face): each corner's own, the middle of
    /// each edge's at its new edge point (<paramref name="edgePoint"/> of corner i is the point on the edge from corner
    /// i to the next, or -1), and the average at the face point (if there is one).
    /// </summary>
    private static Dictionary<string, Dictionary<int, Vec2>>? FaceUVs(Mesh mesh, Polygon poly, int[] v, Func<int, int> edgePoint, int facePoint)
    {
        if (mesh.UVMaps.Count == 0) return null;
        var result = new Dictionary<string, Dictionary<int, Vec2>>();
        foreach (var map in mesh.UVMaps)
        {
            var uvs = mesh.CornerUVsOf(map.Name, poly);
            if (uvs.Length == 0 || uvs.Any(u => u == null)) continue;
            var at = new Dictionary<int, Vec2>();
            var sum = Vec2.Zero;
            for (int i = 0; i < v.Length; i++)
            {
                var uv = uvs[i]!.Value;
                at[v[i]] = uv;
                sum += uv;
                int e = edgePoint(i);
                if (e >= 0) at[e] = (uv + uvs[(i + 1) % v.Length]!.Value) / 2;
            }
            if (facePoint >= 0) at[facePoint] = sum / v.Length;
            result[map.Name] = at;
        }
        return result;
    }

    /// <summary>Gives a face's pieces the UVs worked out for them.</summary>
    private static void SetUVs(Mesh mesh, List<Polygon> pieces, Dictionary<string, Dictionary<int, Vec2>>? uvs)
    {
        if (uvs == null) return;
        foreach (var (name, at) in uvs)
            foreach (var piece in pieces)
                foreach (int q in piece.Points)
                    if (at.TryGetValue(q, out var uv)) mesh.SetCornerUV(name, piece, q, uv);
    }

    /// <summary>Adds the new edge points into the faces outside the selection that share those edges.</summary>
    private static void InsertIntoNeighbours(Mesh mesh, HashSet<int> selected, int polygonsBefore, Dictionary<Edge, int> edgePoint)
    {
        for (int f = 0; f < polygonsBefore; f++)
        {
            if (selected.Contains(f)) continue;
            var poly = mesh.Polygons[f];
            if (poly.Count < 3) continue;
            List<int>? pts = null;
            for (int i = 0; i < poly.Count; i++)
            {
                int a = poly.Points[i], b = poly.Points[(i + 1) % poly.Count];
                if (edgePoint.TryGetValue(Edge.Of(a, b), out int m))
                {
                    pts ??= poly.Points.Take(i).ToList();
                    pts.Add(a);
                    pts.Add(m);
                    // The new point sits halfway along this face's edge in its UVs too.
                    foreach (var map in mesh.UVMaps)
                        if (mesh.TryGetUV(map.Name, poly, a, out var ua) && mesh.TryGetUV(map.Name, poly, b, out var ub))
                            mesh.SetCornerUV(map.Name, poly, m, (ua + ub) / 2);
                }
                else pts?.Add(a);
            }
            if (pts != null) poly.Points = pts;
        }
    }
}

/// <summary>
/// SubPatch surfaces: a mesh marked <see cref="Mesh.SubPatch"/> is a cage shown and rendered as its Catmull–Clark
/// surface. Creases are a weight map (<see cref="CreaseMap"/>, 0 smooth to 1 sharp) on points: an edge is as sharp as
/// the lesser of its two points, so crease a run of edges by giving their points a value. A value under 1 is that
/// share of <see cref="SharpLevels"/> rounds sharp (rounded off after); 1 is sharp for ever.
/// </summary>
public static class SubPatches
{
    /// <summary>The weight map creases live in.</summary>
    public const string CreaseMap = "SubPatch Crease";
    /// <summary>Rounds a crease of 0.99 stays sharp for.</summary>
    public const double SharpLevels = 4;
    /// <summary>Sharpness that never wears off.</summary>
    public const double Infinite = 1e9;
    /// <summary>The level formats without SubPatches get the surface frozen at.</summary>
    public const int ExportLevel = 2;

    /// <summary>A crease value (0 to 1) as sharpness in rounds.</summary>
    public static double ToSharpness(double crease) => crease >= 1 ? Infinite : crease <= 0 ? 0 : crease * SharpLevels;

    /// <summary>Every creased edge's sharpness, from the crease map (null if it has none).</summary>
    public static Dictionary<Edge, double>? Sharpness(Mesh mesh)
    {
        if (mesh.PointMaps.Find(m => m.Name == CreaseMap && !m.IsColor) is not { Values.Count: > 0 } map) return null;
        var result = new Dictionary<Edge, double>();
        foreach (var e in mesh.EdgeMap().Keys)
            if (map.Values.TryGetValue(e.A, out var a) && map.Values.TryGetValue(e.B, out var b))
            {
                double s = ToSharpness(Math.Min(a[0], b[0]));
                if (s > 0) result[e] = s;
            }
        return result.Count == 0 ? null : result;
    }

    /// <summary>The surface of a SubPatch mesh at a level (the mesh itself if it isn't one, or the level is 0).</summary>
    public static Mesh Surface(Mesh mesh, int levels) =>
        !mesh.SubPatch || levels <= 0 || mesh.Polygons.Count == 0 ? mesh : Subdivision.CatmullClark(mesh, Math.Min(levels, 6), null, Sharpness(mesh));

    /// <summary>Bakes a SubPatch mesh's surface into it at a level and turns SubPatch off (nothing if it isn't one).</summary>
    public static void Freeze(Mesh mesh, int levels)
    {
        if (!mesh.SubPatch) return;
        var surface = Subdivision.CatmullClark(mesh, Math.Clamp(levels, 1, 6), null, Sharpness(mesh));
        surface.SubPatch = false;
        mesh.TakeFrom(surface);
        mesh.SubPatch = false;
    }
}
