using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>
/// Finding what's under the mouse in a view: the nearest point or edge on screen within a radius, the front-most
/// polygon by a ray cast, and everything inside a box or a lasso.
/// </summary>
public static class Picking
{
    public readonly record struct Hit(int Layer, int Index);
    public readonly record struct EdgeHit(int Layer, Edge Edge);

    /// <summary>The nearest point within a radius (pixels) of a screen point, among the given layers' meshes.</summary>
    public static Hit? NearestPoint(ViewCamera cam, IReadOnlyList<(int Layer, Mesh Mesh)> layers, double sx, double sy, double radius = 8)
    {
        Hit? best = null;
        double bestD = radius * radius, bestDepth = double.PositiveInfinity;
        foreach (var (layer, mesh) in layers)
            for (int i = 0; i < mesh.Points.Count; i++)
            {
                var p = cam.Project(mesh.Points[i]);
                if (!p.Visible) continue;
                double d = (p.X - sx) * (p.X - sx) + (p.Y - sy) * (p.Y - sy);
                // Ties (points on top of each other in an orthographic view) go to the nearer one.
                if (d < bestD - 1e-9 || Math.Abs(d - bestD) <= 1e-9 && p.Depth < bestDepth)
                {
                    bestD = d;
                    bestDepth = p.Depth;
                    best = new Hit(layer, i);
                }
            }
        return best;
    }

    /// <summary>The nearest edge within a radius of a screen point.</summary>
    public static EdgeHit? NearestEdge(ViewCamera cam, IReadOnlyList<(int Layer, Mesh Mesh)> layers, double sx, double sy, double radius = 6)
    {
        EdgeHit? best = null;
        double bestD = radius;
        foreach (var (layer, mesh) in layers)
        {
            var proj = mesh.Points.Select(cam.Project).ToArray();
            foreach (var e in mesh.EdgeMap().Keys)
            {
                ScreenPoint a = proj[e.A], b = proj[e.B];
                if (!a.Visible || !b.Visible) continue;
                double d = SegmentDistance(sx, sy, a.X, a.Y, b.X, b.Y);
                if (d < bestD) { bestD = d; best = new EdgeHit(layer, e); }
            }
        }
        return best;
    }

    /// <summary>
    /// The polygon under a screen point: the front-most face the view's ray passes through, or failing that a
    /// 2-point polygon (line) or 1-point polygon within a few pixels.
    /// </summary>
    public static Hit? PolygonAt(ViewCamera cam, IReadOnlyList<(int Layer, Mesh Mesh)> layers, double sx, double sy, double radius = 5)
    {
        var ray = cam.RayAt(sx, sy);
        Hit? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var (layer, mesh) in layers)
            foreach (var t in Tessellation.Of(mesh))
            {
                var hit = ray.IntersectTriangle(mesh.Points[t.A], mesh.Points[t.B], mesh.Points[t.C]);
                if (hit is { } h && h.T < bestT) { bestT = h.T; best = new Hit(layer, t.Polygon); }
            }
        if (best != null) return best;
        double bestD = radius;
        foreach (var (layer, mesh) in layers)
            for (int pi = 0; pi < mesh.Polygons.Count; pi++)
            {
                var p = mesh.Polygons[pi];
                if (p.Count > 2) continue;
                var a = cam.Project(mesh.Points[p.Points[0]]);
                var b = p.Count == 2 ? cam.Project(mesh.Points[p.Points[1]]) : a;
                if (!a.Visible || !b.Visible) continue;
                double d = SegmentDistance(sx, sy, a.X, a.Y, b.X, b.Y);
                if (d < bestD) { bestD = d; best = new Hit(layer, pi); }
            }
        return best;
    }

    /// <summary>The points of a mesh whose screen positions fall inside a shape (a box or lasso, as a screen polygon).</summary>
    public static IEnumerable<int> PointsInside(ViewCamera cam, Mesh mesh, IReadOnlyList<(double X, double Y)> shape)
    {
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            var p = cam.Project(mesh.Points[i]);
            if (p.Visible && Inside(shape, p.X, p.Y)) yield return i;
        }
    }

    /// <summary>The polygons of a mesh whose centres fall inside a shape on screen.</summary>
    public static IEnumerable<int> PolygonsInside(ViewCamera cam, Mesh mesh, IReadOnlyList<(double X, double Y)> shape)
    {
        for (int i = 0; i < mesh.Polygons.Count; i++)
        {
            var p = cam.Project(mesh.Centroid(mesh.Polygons[i]));
            if (p.Visible && Inside(shape, p.X, p.Y)) yield return i;
        }
    }

    /// <summary>The edges of a mesh with both ends inside a shape on screen.</summary>
    public static IEnumerable<Edge> EdgesInside(ViewCamera cam, Mesh mesh, IReadOnlyList<(double X, double Y)> shape)
    {
        var inside = new HashSet<int>(PointsInside(cam, mesh, shape));
        return mesh.EdgeMap().Keys.Where(e => inside.Contains(e.A) && inside.Contains(e.B));
    }

    /// <summary>A rectangle as a screen polygon.</summary>
    public static (double X, double Y)[] Box(double x0, double y0, double x1, double y1) =>
        [(Math.Min(x0, x1), Math.Min(y0, y1)), (Math.Max(x0, x1), Math.Min(y0, y1)), (Math.Max(x0, x1), Math.Max(y0, y1)), (Math.Min(x0, x1), Math.Max(y0, y1))];

    /// <summary>Even–odd point-in-polygon on screen.</summary>
    public static bool Inside(IReadOnlyList<(double X, double Y)> shape, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = shape.Count - 1; i < shape.Count; j = i++)
        {
            var (xi, yi) = shape[i];
            var (xj, yj) = shape[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    public static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double cx = ax + dx * t - px, cy = ay + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>
    /// A volume from a box dragged in an orthographic view: the view's two axes from the drag, the third kept from
    /// the current volume (or unbounded when there's none yet), as classic Modeler builds a volume across views.
    /// </summary>
    public static Box3 VolumeFromDrag(ViewCamera cam, double x0, double y0, double x1, double y1, Box3 current)
    {
        var a = cam.Unproject(x0, y0, cam.Center);
        var b = cam.Unproject(x1, y1, cam.Center);
        var (r, u, _) = cam.Basis();
        var box = Box3.FromCorners(a, b);
        Vec3 min = box.Min, max = box.Max;
        foreach (Axis ax in new[] { Axis.X, Axis.Y, Axis.Z })
        {
            var unit = Vec3.Unit(ax);
            bool viewAxis = Math.Abs(Vec3.Dot(unit, r)) > 0.5 || Math.Abs(Vec3.Dot(unit, u)) > 0.5;
            if (viewAxis) continue;
            min = min.With(ax, current.IsEmpty ? -1e9 : current.Min[ax]);
            max = max.With(ax, current.IsEmpty ? 1e9 : current.Max[ax]);
        }
        return new Box3(min, max);
    }
}
