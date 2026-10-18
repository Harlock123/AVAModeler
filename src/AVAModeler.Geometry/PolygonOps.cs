using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// The Polygon tab's commands that change how faces are put together: Merge Polygons, Split, Unify, Align and Flip.
/// Each edits the mesh in place, on the polygons given (or all of them when given null).
/// </summary>
public static class PolygonOps
{
    /// <summary>
    /// Merge Polygons: joins faces that share an edge, have the same surface and lie in the same plane (normals within
    /// <paramref name="maxAngleDegrees"/>) into single n-gons without the shared edges. Faces are joined two at a time
    /// while the outline stays one simple loop, so a ring of faces round a hole ends up in two or more pieces. The
    /// shared points stay in the mesh. Returns how many polygons went.
    /// </summary>
    public static int Merge(Mesh mesh, IEnumerable<int>? polygons = null, double maxAngleDegrees = 1)
    {
        var faces = Chosen(mesh, polygons).Where(i => mesh.Polygons[i].Count >= 3).ToList();
        var faceSet = faces.ToHashSet();
        var normals = faces.ToDictionary(f => f, mesh.Normal);
        double cos = Math.Cos(Mat4.Deg(maxAngleDegrees));
        var parent = faces.ToDictionary(f => f, f => f);
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var owner = new Dictionary<(int, int), int>();
        foreach (int f in faces)
            foreach (var e in mesh.Polygons[f].Edges()) owner.TryAdd(e, f);
        foreach (int f in faces)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
                if (owner.TryGetValue((b, a), out int g) && g != f && faceSet.Contains(g)
                    && mesh.Polygons[f].Surface == mesh.Polygons[g].Surface && Vec3.Dot(normals[f], normals[g]) >= cos)
                    parent[Find(f)] = Find(g);
        var groups = faces.ToDictionary(f => mesh.Polygons[f], Find);
        int before = mesh.Polygons.Count;
        MeshCleanup.Rejoin(mesh, groups);
        return before - mesh.Polygons.Count;
    }

    /// <summary>
    /// Split: cuts a face in two along a line between two of its points (mesh point indices) that aren't neighbours.
    /// The face keeps the part from <paramref name="pointA"/> round to <paramref name="pointB"/>; the other part is added
    /// at the end with the same surface. Returns its index, or -1 when the face can't be split there.
    /// </summary>
    public static int Split(Mesh mesh, int polygon, int pointA, int pointB)
    {
        var poly = mesh.Polygons[polygon];
        int n = poly.Count, i = poly.Points.IndexOf(pointA), j = poly.Points.IndexOf(pointB);
        if (n < 4 || i < 0 || j < 0 || i == j) return -1;
        int gap = (j - i + n) % n;
        if (gap == 1 || gap == n - 1) return -1;
        var first = new List<int>();
        for (int k = i; ; k = (k + 1) % n)
        {
            first.Add(poly.Points[k]);
            if (k == j) break;
        }
        var second = new List<int>();
        for (int k = j; ; k = (k + 1) % n)
        {
            second.Add(poly.Points[k]);
            if (k == i) break;
        }
        poly.Points = first;
        poly.Details = null;
        mesh.Polygons.Add(new Polygon(second, poly.Surface));
        mesh.Touch();
        return mesh.Polygons.Count - 1;
    }

    /// <summary>
    /// Split by points: every face (of those given, or all) holding exactly two of the points, not side by side, is
    /// split between them. Returns how many faces were split.
    /// </summary>
    public static int SplitAtPoints(Mesh mesh, IEnumerable<int> points, IEnumerable<int>? polygons = null)
    {
        var pts = points.ToHashSet();
        int split = 0;
        foreach (int f in Chosen(mesh, polygons))
        {
            var hits = mesh.Polygons[f].Points.Where(pts.Contains).Distinct().ToList();
            if (hits.Count == 2 && Split(mesh, f, hits[0], hits[1]) >= 0) split++;
        }
        return split;
    }

    /// <summary>
    /// Unify: removes polygons that use the same set of points as an earlier one (whatever their order or direction),
    /// keeping the first. Returns how many went.
    /// </summary>
    public static int Unify(Mesh mesh, IEnumerable<int>? polygons = null)
    {
        var seen = new HashSet<string>();
        var gone = new List<int>();
        foreach (int f in Chosen(mesh, polygons).Order())
        {
            var key = string.Join(",", mesh.Polygons[f].Points.Order());
            if (!seen.Add(key)) gone.Add(f);
        }
        mesh.RemovePolygons(gone);
        return gone.Count;
    }

    /// <summary>
    /// Align: makes the faces of each connected piece wind the same way as their neighbours. A closed piece is turned
    /// to face outward; an open one follows the direction most of its faces already had. Returns how many faces were
    /// flipped.
    /// </summary>
    public static int Align(Mesh mesh, IEnumerable<int>? polygons = null) => Topology.Orient(mesh, Chosen(mesh, polygons));

    /// <summary>Flip: reverses the winding of the faces, so they face the other way. Points and lines are left alone.</summary>
    public static void Flip(Mesh mesh, IEnumerable<int>? polygons = null)
    {
        foreach (int f in Chosen(mesh, polygons))
            if (mesh.Polygons[f].Count >= 3) mesh.Polygons[f].Points.Reverse();
        mesh.Touch();
    }

    private static List<int> Chosen(Mesh mesh, IEnumerable<int>? polygons) =>
        polygons?.Where(i => i >= 0 && i < mesh.Polygons.Count).Distinct().ToList() ?? Enumerable.Range(0, mesh.Polygons.Count).ToList();
}
