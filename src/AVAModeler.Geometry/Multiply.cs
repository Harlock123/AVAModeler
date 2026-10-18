using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// The Multiply tab's algorithms: extrude, lathe, path and rail extrude, bevel, smooth shift, mirror, clone and
/// array. Each edits the mesh in place and works on the polygons given. Unless said otherwise, the polygons acted on
/// keep their indices (an extruded face is still the same polygon, now at the far end) and new polygons are added at
/// the end, so a tool can keep the selection. New faces take the surface of the polygon they grew from.
/// </summary>
public static class Multiply
{
    /// <summary>
    /// Extrude: sweeps the polygons along <paramref name="offset"/> in <paramref name="segments"/> steps. Faces move to
    /// the far end, with walls only along the edge of the selected region (edges two selected faces share get none),
    /// facing outward for faces extruded either way along their normal. Lines (2-point polygons) sweep into strips of
    /// faces and points into chains of lines; the first piece of each takes its place. With
    /// <paramref name="capBase"/>, a flipped copy of the faces stays at the start, closing the shape (for flat
    /// outlines, as classic Modeler did), and closed results are turned to face outward.
    /// </summary>
    public static void Extrude(Mesh mesh, IEnumerable<int> polygons, Vec3 offset, int segments = 1, bool capBase = false)
    {
        segments = Math.Max(segments, 1);
        var sections = Enumerable.Range(0, segments + 1).Select(k => Mat4.Translation(offset * ((double)k / segments))).ToList();
        var made = Sweep(mesh, polygons, sections, false, capBase ? SweepFaces.CapBoth : SweepFaces.MoveToEnd);
        if (capBase) Topology.Orient(mesh, IndicesOf(mesh, made));
    }

    /// <summary>
    /// Lathe: sweeps the polygons round an axis through <paramref name="center"/>, from
    /// <paramref name="startDegrees"/> to <paramref name="endDegrees"/> (right-handed about the axis) in
    /// <paramref name="sides"/> steps, rising <paramref name="offsetPerRevolution"/> along the axis per full turn (for
    /// spirals). The profile is first turned to the start angle. Lines and polylines sweep into faces, points into
    /// curves, and each face's edges into walls. A full turn with no offset closes on itself: the seam is shared and
    /// the profile faces are removed (they would sit inside); otherwise the profile faces cap both ends. Points on the
    /// axis are merged, faces that collapse are dropped, and the new faces are turned outward (closed pieces by
    /// volume, open ones away from the axis). Polygon indices may change.
    /// </summary>
    public static void Lathe(Mesh mesh, IEnumerable<int> polygons, Axis axis, Vec3 center, double startDegrees, double endDegrees,
        int sides, double offsetPerRevolution = 0)
    {
        var sel = Selected(mesh, polygons);
        if (sel.Count == 0 || sides < 1) return;
        var u = Vec3.Unit(axis);
        double sweep = endDegrees - startDegrees;
        bool closed = Math.Abs(sweep) >= 360 - 1e-9 && offsetPerRevolution == 0;
        mesh.Transform(Mat4.About(center, Mat4.Rotation(u, Mat4.Deg(startDegrees))), mesh.PointsOf(sel));
        var sections = new List<Mat4>();
        for (int k = 0; k <= (closed ? sides - 1 : sides); k++)
        {
            double deg = sweep * k / sides;
            sections.Add(Mat4.Translation(u * (offsetPerRevolution * deg / 360)) * Mat4.About(center, Mat4.Rotation(u, Mat4.Deg(deg))));
        }
        var made = Sweep(mesh, sel, sections, closed, closed ? SweepFaces.Remove : SweepFaces.CapBoth).ToHashSet();
        var involved = made.SelectMany(p => p.Points).Distinct().ToList();
        MeshCleanup.Weld(mesh, Tolerance.For(mesh), involved);
        var faces = IndicesOf(mesh, made).Where(i => mesh.Polygons[i].Count >= 3).ToList();
        var degenerate = faces.Where(i => MeshCleanup.IsDegenerate(mesh, mesh.Polygons[i].Points, Tolerance.For(mesh))).ToList();
        mesh.RemovePolygons(degenerate);
        Topology.Orient(mesh, IndicesOf(mesh, made), p =>
        {
            var c = mesh.Centroid(p) - center;
            var radial = c - u * Vec3.Dot(c, u);
            return Vec3.Dot(mesh.Normal(p), radial) * mesh.Area(p);
        });
    }

    /// <summary>
    /// Path Extrude: sweeps the polygons along a path of points. The profile is taken to sit at the path's first point
    /// and is carried to each later point, turned (by the smallest rotation, step by step) to follow the path's
    /// direction; with <paramref name="orient"/> off it is only moved. Otherwise as <see cref="Extrude"/>.
    /// </summary>
    public static void PathExtrude(Mesh mesh, IEnumerable<int> polygons, IReadOnlyList<Vec3> path, bool orient = true, bool capBase = false)
    {
        var pts = new List<Vec3>();
        foreach (var p in path)
            if (pts.Count == 0 || !Vec3.Near(pts[^1], p, 1e-12)) pts.Add(p);
        if (pts.Count < 2) return;
        var sections = new List<Mat4> { Mat4.Identity };
        var turn = Mat4.Identity;
        var previous = Tangent(pts, 0);
        for (int k = 1; k < pts.Count; k++)
        {
            var t = Tangent(pts, k);
            if (orient) turn = Turn(previous, t) * turn;
            previous = t;
            sections.Add(Mat4.Translation(pts[k]) * turn * Mat4.Translation(-pts[0]));
        }
        var made = Sweep(mesh, polygons, sections, false, capBase ? SweepFaces.CapBoth : SweepFaces.MoveToEnd);
        if (capBase) Topology.Orient(mesh, IndicesOf(mesh, made));
    }

    /// <summary>
    /// Rail Extrude: sweeps the polygons along a rail (a polyline of points), resampled into
    /// <paramref name="segments"/> pieces of equal length along it; each section is the profile moved by the rail's
    /// travel from its first point, and with <paramref name="orient"/> also turned to follow it.
    /// </summary>
    public static void RailExtrude(Mesh mesh, IEnumerable<int> polygons, IReadOnlyList<Vec3> rail, int segments, bool orient = false, bool capBase = false)
    {
        if (rail.Count < 2) return;
        PathExtrude(mesh, polygons, Resample(rail, Math.Max(segments, 1)), orient, capBase);
    }

    /// <summary>The points at equal distances along a polyline, from its start to its end (pieces + 1 of them).</summary>
    public static List<Vec3> Resample(IReadOnlyList<Vec3> polyline, int pieces)
    {
        var lengths = new double[polyline.Count];
        for (int i = 1; i < polyline.Count; i++) lengths[i] = lengths[i - 1] + Vec3.Distance(polyline[i - 1], polyline[i]);
        double total = lengths[^1];
        var result = new List<Vec3>(pieces + 1);
        int seg = 1;
        for (int k = 0; k <= pieces; k++)
        {
            double s = total * k / pieces;
            while (seg < polyline.Count - 1 && lengths[seg] < s) seg++;
            double len = lengths[seg] - lengths[seg - 1];
            result.Add(len > 0 ? Vec3.Lerp(polyline[seg - 1], polyline[seg], (s - lengths[seg - 1]) / len) : polyline[seg]);
        }
        return result;
    }

    /// <summary>
    /// Bevel: each face given is shrunk inward by <paramref name="inset"/> (edges move in parallel, so the border is an
    /// even width) and moved <paramref name="shift"/> along its normal, with a wall face along each edge joining it to
    /// where it was. Each face is bevelled on its own (as in classic Modeler); its original points stay for its
    /// neighbours. Points and lines are ignored.
    /// </summary>
    public static void Bevel(Mesh mesh, IEnumerable<int> polygons, double inset, double shift)
    {
        foreach (int f in Selected(mesh, polygons))
        {
            var poly = mesh.Polygons[f];
            var n = mesh.Normal(poly);
            if (poly.Count < 3 || n.LengthSquared == 0) continue;
            var v = poly.Points.ToArray();
            int count = v.Length;
            var inner = new int[count];
            for (int i = 0; i < count; i++)
            {
                var p = mesh.Points[v[i]];
                var m1 = Vec3.Cross(n, (p - mesh.Points[v[(i + count - 1) % count]]).Normalized()).Normalized();
                var m2 = Vec3.Cross(n, (mesh.Points[v[(i + 1) % count]] - p).Normalized()).Normalized();
                // The point that is `inset` in from both edges; a hairpin turn falls back to one edge's normal.
                double denom = 1 + Vec3.Dot(m1, m2);
                var d = denom > 1e-6 ? (m1 + m2) * (inset / denom) : m1 * inset;
                inner[i] = mesh.Points.Count;
                mesh.Points.Add(p + d + n * shift);
            }
            for (int i = 0; i < count; i++)
                mesh.Polygons.Add(new Polygon([v[i], v[(i + 1) % count], inner[(i + 1) % count], inner[i]], poly.Surface));
            poly.Points = [.. inner];
        }
        mesh.Touch();
    }

    /// <summary>
    /// Smooth Shift: moves a region of faces <paramref name="offset"/> along the averaged normals of its points,
    /// building walls only along the region's border. Where neighbouring faces in the region meet at more than
    /// <paramref name="maxSmoothingDegrees"/>, they part company: each side moves along its own normals and a wall
    /// joins them, with a small cap face where three or more parted pieces meet at a corner. Points inside the region
    /// that nothing else uses move rather than being copied. Points and lines are ignored.
    /// </summary>
    public static void SmoothShift(Mesh mesh, IEnumerable<int> polygons, double offset, double maxSmoothingDegrees = 89.5)
    {
        var faces = Selected(mesh, polygons).Where(i => mesh.Polygons[i].Count >= 3).ToList();
        if (faces.Count == 0) return;
        var faceSet = faces.ToHashSet();
        var normals = faces.ToDictionary(f => f, mesh.Normal);
        double cosMax = Math.Cos(Mat4.Deg(maxSmoothingDegrees));

        // Group each (point, face) corner with its neighbours across smooth edges.
        var parent = new Dictionary<(int P, int F), (int P, int F)>();
        (int, int) Find((int, int) x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (int f in faces)
            foreach (int p in mesh.Polygons[f].Points) parent[(p, f)] = (p, f);
        var edgeFaces = new Dictionary<Edge, List<int>>();
        foreach (int f in faces)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (!edgeFaces.TryGetValue(e, out var l)) edgeFaces[e] = l = [];
                if (!l.Contains(f)) l.Add(f);
            }
        bool IsSmooth(List<int> l) => l.Count == 2 && Vec3.Dot(normals[l[0]], normals[l[1]]) >= cosMax - 1e-12;
        foreach (var (e, l) in edgeFaces)
            if (IsSmooth(l))
                foreach (int p in new[] { e.A, e.B })
                {
                    var (x, y) = (Find((p, l[0])), Find((p, l[1])));
                    if (x != y) parent[x] = y;
                }

        var border = edgeFaces.Where(kv => kv.Value.Count != 2).SelectMany(kv => new[] { kv.Key.A, kv.Key.B }).ToHashSet();
        var sum = new Dictionary<(int, int), Vec3>();
        foreach (var key in parent.Keys.ToList())
        {
            var r = Find(key);
            sum[r] = sum.GetValueOrDefault(r) + normals[key.F];
        }
        var index = new Dictionary<(int, int), int>();
        var moves = new Dictionary<int, Vec3>();
        foreach (var key in parent.Keys.OrderBy(k => k.P).ThenBy(k => k.F))
        {
            var r = Find(key);
            if (index.ContainsKey(r)) continue;
            var to = mesh.Points[key.P] + sum[r].Normalized() * offset;
            if (!border.Contains(key.P) && !moves.ContainsKey(key.P) && mesh.PolygonsOf(key.P).All(faceSet.Contains))
            {
                index[r] = key.P;
                moves[key.P] = to;
            }
            else
            {
                index[r] = mesh.Points.Count;
                mesh.Points.Add(to);
            }
        }
        int Copy(int p, int f) => index[Find((p, f))];

        var walls = new List<Polygon>();
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            foreach (var (a, b) in poly.Edges())
            {
                if (a == b) continue;
                var l = edgeFaces[Edge.Of(a, b)];
                if (l.Count != 2)
                    walls.Add(new Polygon([a, b, Copy(b, f), Copy(a, f)], poly.Surface));
                else if (!IsSmooth(l) && l[0] == f)
                {
                    int g = l[1];
                    walls.Add(new Polygon([Copy(a, g), Copy(b, g), Copy(b, f), Copy(a, f)], poly.Surface));
                }
            }
        }
        var caps = Caps(mesh, faces, border, Copy, walls);
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            poly.Points = poly.Points.Select(p => Copy(p, f)).ToList();
        }
        foreach (var (p, to) in moves) mesh.Points[p] = to;
        mesh.Polygons.AddRange(walls);
        mesh.Polygons.AddRange(caps);
        mesh.Touch();
    }

    /// <summary>
    /// Smooth Shift's corner caps: round each point, the copies its faces moved to, in order round the point (with the
    /// original point first on the region's border); three or more distinct ones leave a hole that a face fills.
    /// </summary>
    private static List<Polygon> Caps(Mesh mesh, List<int> faces, HashSet<int> border, Func<int, int, int> copy, List<Polygon> walls)
    {
        var wallEdges = walls.SelectMany(w => w.Edges()).ToHashSet();
        var around = new Dictionary<int, List<int>>();
        foreach (int f in faces)
            foreach (int p in mesh.Polygons[f].Points.Distinct())
            {
                if (!around.TryGetValue(p, out var l)) around[p] = l = [];
                l.Add(f);
            }
        var caps = new List<Polygon>();
        foreach (var (p, fs) in around)
        {
            if (fs.Count < 2) continue;
            // Going round p: the face after f is the one whose edge into p is f's edge out of p.
            var byPrev = new Dictionary<int, int>();
            bool manifold = true;
            foreach (int f in fs)
            {
                var pts = mesh.Polygons[f].Points;
                int i = pts.IndexOf(p);
                if (pts.LastIndexOf(p) != i || !byPrev.TryAdd(pts[(i + pts.Count - 1) % pts.Count], f)) manifold = false;
            }
            if (!manifold) continue;
            int Next(int f)
            {
                var pts = mesh.Polygons[f].Points;
                return byPrev.GetValueOrDefault(pts[(pts.IndexOf(p) + 1) % pts.Count], -1);
            }
            bool onBorder = border.Contains(p);
            int start = fs[0];
            if (onBorder)
            {
                // Start from the face with no face before it.
                var nexts = fs.Select(Next).ToHashSet();
                start = fs.FirstOrDefault(f => !nexts.Contains(f), -1);
                if (start < 0) continue;
            }
            var order = new List<int>();
            for (int f = start; f >= 0 && !order.Contains(f); f = Next(f)) order.Add(f);
            if (order.Count != fs.Count) continue;
            var loop = new List<int>();
            if (onBorder) loop.Add(p);
            foreach (int f in order)
            {
                int c = copy(p, f);
                if (loop.Count == 0 || loop[^1] != c) loop.Add(c);
            }
            while (loop.Count > 1 && loop[0] == loop[^1]) loop.RemoveAt(loop.Count - 1);
            if (loop.Count < 3) continue;
            var cap = new Polygon(loop, mesh.Polygons[order[0]].Surface);
            if (cap.Edges().Any(wallEdges.Contains)) cap.Points.Reverse();
            caps.Add(cap);
        }
        return caps;
    }

    /// <summary>
    /// Mirror: adds a mirror image of the polygons, reflected in the plane at right angles to <paramref name="axis"/>
    /// through <paramref name="center"/> on it, wound the other way so the copies face outward too. Points within
    /// <paramref name="tolerance"/> of the plane are shared rather than copied, so the halves join at the seam;
    /// polygons lying wholly in the plane are not copied. Returns the indices of the new polygons.
    /// </summary>
    public static List<int> Mirror(Mesh mesh, IEnumerable<int> polygons, Axis axis, double center = 0, double? tolerance = null)
    {
        double tol = tolerance ?? Tolerance.For(mesh);
        var map = new Dictionary<int, int>();
        int Map(int p)
        {
            if (map.TryGetValue(p, out int m)) return m;
            var v = mesh.Points[p];
            if (Math.Abs(v[axis] - center) <= tol) return map[p] = p;
            mesh.Points.Add(v.With(axis, 2 * center - v[axis]));
            return map[p] = mesh.Points.Count - 1;
        }
        var added = new List<int>();
        foreach (int pi in Selected(mesh, polygons))
        {
            var poly = mesh.Polygons[pi];
            if (poly.Points.All(p => Math.Abs(mesh.Points[p][axis] - center) <= tol)) continue;
            var pts = poly.Points.Select(Map).ToList();
            if (pts.Count >= 3) pts.Reverse();
            mesh.Polygons.Add(new Polygon(pts, poly.Surface));
            added.Add(mesh.Polygons.Count - 1);
        }
        mesh.Touch();
        return added;
    }

    /// <summary>
    /// Clone: adds <paramref name="copies"/> copies of the polygons (with their own points), each one step further on
    /// than the last; a step moves by <paramref name="offset"/> after turning by heading, pitch and bank
    /// <paramref name="rotationHpb"/> (degrees) and scaling by <paramref name="scale"/> about <paramref name="center"/>.
    /// A step that mirrors (a negative scale) reverses the copies' winding. Returns the indices of the new polygons.
    /// </summary>
    public static List<int> Clone(Mesh mesh, IEnumerable<int> polygons, int copies, Vec3 offset, Vec3 rotationHpb, Vec3 scale, Vec3 center)
    {
        var step = Mat4.Translation(offset) * Mat4.About(center, Mat4.HeadingPitchBank(rotationHpb.X, rotationHpb.Y, rotationHpb.Z) * Mat4.Scaling(scale));
        var sel = Selected(mesh, polygons);
        var added = new List<int>();
        var m = Mat4.Identity;
        for (int k = 1; k <= copies; k++)
        {
            m = step * m;
            added.AddRange(Copy(mesh, sel, m));
        }
        return added;
    }

    /// <summary>
    /// Array (rectangular): copies of the polygons on a grid of counts × spacing, the original being the first cell.
    /// Returns the indices of the new polygons.
    /// </summary>
    public static List<int> Array(Mesh mesh, IEnumerable<int> polygons, int countX, int countY, int countZ, Vec3 spacing)
    {
        var sel = Selected(mesh, polygons);
        var added = new List<int>();
        for (int x = 0; x < countX; x++)
            for (int y = 0; y < countY; y++)
                for (int z = 0; z < countZ; z++)
                    if (x != 0 || y != 0 || z != 0)
                        added.AddRange(Copy(mesh, sel, Mat4.Translation(Vec3.Scale(spacing, new Vec3(x, y, z)))));
        return added;
    }

    /// <summary>
    /// Array (radial): <paramref name="count"/> evenly spaced copies round a full turn about an axis through a centre,
    /// the original being the first. Returns the indices of the new polygons.
    /// </summary>
    public static List<int> RadialArray(Mesh mesh, IEnumerable<int> polygons, int count, Axis axis, Vec3 center)
    {
        var sel = Selected(mesh, polygons);
        var added = new List<int>();
        for (int k = 1; k < count; k++)
            added.AddRange(Copy(mesh, sel, Mat4.About(center, Mat4.Rotation(Vec3.Unit(axis), 2 * Math.PI * k / count))));
        return added;
    }

    private static List<int> Copy(Mesh mesh, List<int> polygons, Mat4 m)
    {
        bool mirrors = m.Determinant() < 0;
        var map = new Dictionary<int, int>();
        var added = new List<int>();
        foreach (int pi in polygons)
        {
            var poly = mesh.Polygons[pi];
            var pts = poly.Points.Select(p =>
            {
                if (!map.TryGetValue(p, out int c))
                {
                    map[p] = c = mesh.Points.Count;
                    mesh.Points.Add(m.TransformPoint(mesh.Points[p]));
                }
                return c;
            }).ToList();
            if (mirrors && pts.Count >= 3) pts.Reverse();
            mesh.Polygons.Add(new Polygon(pts, poly.Surface));
            added.Add(mesh.Polygons.Count - 1);
        }
        mesh.Touch();
        return added;
    }

    private enum SweepFaces
    {
        /// <summary>Faces move to the last section.</summary>
        MoveToEnd,
        /// <summary>Faces stay at the first section, flipped, and a copy goes to the last.</summary>
        CapBoth,
        /// <summary>Faces are removed once their edges have been swept (a closed lathe).</summary>
        Remove,
    }

    /// <summary>
    /// The engine behind extrude, lathe and path extrude: the polygons' points carried through a list of section
    /// transforms (the first must leave them where they are), with walls between successive sections along the
    /// region's border edges, lines swept into faces and points into lines. A closed sweep wraps the last section back
    /// to the first. Returns every polygon made or moved.
    /// </summary>
    private static List<Polygon> Sweep(Mesh mesh, IEnumerable<int> polygons, IReadOnlyList<Mat4> sections, bool closed, SweepFaces mode)
    {
        var sel = Selected(mesh, polygons);
        int segments = closed ? sections.Count : sections.Count - 1;
        if (sel.Count == 0 || segments < 1) return [];
        var faces = sel.Where(i => mesh.Polygons[i].Count >= 3).ToList();
        var faceSet = faces.ToHashSet();
        var counts = Topology.FaceEdgeCounts(mesh, faces);
        var borderPoints = counts.Where(kv => kv.Value != 2).SelectMany(kv => new[] { kv.Key.A, kv.Key.B }).ToHashSet();
        var others = sel.Where(i => mesh.Polygons[i].Count < 3).SelectMany(i => mesh.Polygons[i].Points).ToHashSet();
        // A face point nothing else uses, and that no wall starts from, can simply move to the far end.
        var movable = mode == SweepFaces.MoveToEnd
            ? mesh.PointsOf(faces).Where(p => !borderPoints.Contains(p) && !others.Contains(p) && mesh.PolygonsOf(p).All(faceSet.Contains)).ToHashSet()
            : [];

        var copies = new Dictionary<(int, int), int>();
        int At(int p, int k)
        {
            if (k == 0 || closed && k == segments) return p;
            if (k == segments && movable.Contains(p)) return p;
            if (!copies.TryGetValue((p, k), out int c))
            {
                copies[(p, k)] = c = mesh.Points.Count;
                mesh.Points.Add(sections[k].TransformPoint(mesh.Points[p]));
            }
            return c;
        }
        List<int> Strip(int a, int b, int k) => [At(a, k), At(b, k), At(b, k + 1), At(a, k + 1)];

        var made = new List<Polygon>();
        void AddNew(List<int> pts, int surface)
        {
            var p = new Polygon(pts, surface);
            mesh.Polygons.Add(p);
            made.Add(p);
        }
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            foreach (var (a, b) in poly.Edges())
                if (a != b && counts[Edge.Of(a, b)] != 2)
                    for (int k = 0; k < segments; k++) AddNew(Strip(a, b, k), poly.Surface);
        }
        foreach (int i in sel.Where(i => mesh.Polygons[i].Count < 3))
        {
            var poly = mesh.Polygons[i];
            var pts = poly.Points.ToList();
            for (int k = 0; k < segments; k++)
            {
                var piece = pts.Count == 2 ? Strip(pts[0], pts[1], k) : [At(pts[0], k), At(pts[0], k + 1)];
                if (k > 0)
                {
                    AddNew(piece, poly.Surface);
                    continue;
                }
                poly.Points = piece;
                made.Add(poly);
            }
        }
        var finals = movable.ToDictionary(p => p, p => sections[segments].TransformPoint(mesh.Points[p]));
        foreach (int f in faces)
        {
            var poly = mesh.Polygons[f];
            switch (mode)
            {
                case SweepFaces.MoveToEnd:
                    poly.Points = poly.Points.Select(p => At(p, segments)).ToList();
                    made.Add(poly);
                    break;
                case SweepFaces.CapBoth:
                    AddNew(poly.Points.Select(p => At(p, segments)).ToList(), poly.Surface);
                    poly.Points.Reverse();
                    made.Add(poly);
                    break;
            }
        }
        foreach (var (p, to) in finals) mesh.Points[p] = to;
        if (mode == SweepFaces.Remove)
        {
            var gone = faces.Select(f => mesh.Polygons[f]).ToHashSet();
            mesh.Polygons.RemoveAll(gone.Contains);
        }
        mesh.Touch();
        return made;
    }

    /// <summary>The valid, distinct polygon indices given.</summary>
    private static List<int> Selected(Mesh mesh, IEnumerable<int> polygons) =>
        polygons.Where(i => i >= 0 && i < mesh.Polygons.Count).Distinct().ToList();

    /// <summary>Where some polygons (by reference) now sit in the mesh's list.</summary>
    private static List<int> IndicesOf(Mesh mesh, IEnumerable<Polygon> polygons)
    {
        var set = polygons as HashSet<Polygon> ?? polygons.ToHashSet();
        return Enumerable.Range(0, mesh.Polygons.Count).Where(i => set.Contains(mesh.Polygons[i])).ToList();
    }

    /// <summary>The direction of a path at one of its points (the average of the segments either side).</summary>
    private static Vec3 Tangent(List<Vec3> pts, int k)
    {
        if (k == 0) return (pts[1] - pts[0]).Normalized();
        if (k == pts.Count - 1) return (pts[k] - pts[k - 1]).Normalized();
        var t = (pts[k + 1] - pts[k]).Normalized() + (pts[k] - pts[k - 1]).Normalized();
        return t.LengthSquared > 1e-24 ? t.Normalized() : (pts[k + 1] - pts[k]).Normalized();
    }

    /// <summary>The smallest rotation that turns direction a to direction b.</summary>
    private static Mat4 Turn(Vec3 a, Vec3 b)
    {
        var axis = Vec3.Cross(a, b);
        double sin = axis.Length, cos = Vec3.Dot(a, b);
        if (sin < 1e-12) return cos > 0 ? Mat4.Identity : Mat4.Rotation(a.AnyPerpendicular(), Math.PI);
        return Mat4.Rotation(axis, Math.Atan2(sin, cos));
    }
}
