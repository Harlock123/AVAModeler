using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Splits n-gons into triangles by ear clipping, after projecting them onto the plane of their Newell normal, so
/// concave and slightly non-planar faces come out right. Triangles keep the polygon's winding (and so its facing).
/// </summary>
public static class Triangulator
{
    /// <summary>
    /// Triangulates a polygon given its corner positions; returns triangles as corner indices (into the list given).
    /// Fewer than three corners give nothing.
    /// </summary>
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<Vec3> corners, Vec3 normal)
    {
        var result = new List<(int, int, int)>();
        int n = corners.Count;
        if (n < 3) return result;
        if (n == 3)
        {
            result.Add((0, 1, 2));
            return result;
        }
        var nn = normal.Normalized();
        if (nn.LengthSquared == 0) nn = NewellNormal(corners);
        if (nn.LengthSquared == 0)
        {
            Fan(n, result);
            return result;
        }
        var u = nn.AnyPerpendicular();
        var v = Vec3.Cross(nn, u);
        var p = new Vec2[n];
        for (int i = 0; i < n; i++) p[i] = new Vec2(Vec3.Dot(corners[i], u), Vec3.Dot(corners[i], v));

        // Clip in anticlockwise 2D order, then turn each triangle back to the polygon's own winding.
        double area = 0;
        for (int i = 0; i < n; i++) area += Vec2.Cross(p[i], p[(i + 1) % n]);
        bool reversed = area < 0;
        var ring = Enumerable.Range(0, n).ToList();
        if (reversed) ring.Reverse();

        double scale = Math.Max(Math.Abs(area), 1e-300);
        double eps = scale * 1e-12;
        while (ring.Count > 3)
        {
            int ear = FindEar(ring, p, eps, strict: true);
            if (ear < 0) ear = FindEar(ring, p, eps, strict: false);
            if (ear < 0) ear = 0;
            int m = ring.Count;
            int a = ring[(ear + m - 1) % m], b = ring[ear], c = ring[(ear + 1) % m];
            Emit(result, a, b, c, reversed);
            ring.RemoveAt(ear);
        }
        Emit(result, ring[0], ring[1], ring[2], reversed);
        return result;
    }

    private static void Emit(List<(int, int, int)> result, int a, int b, int c, bool reversed) =>
        result.Add(reversed ? (c, b, a) : (a, b, c));

    /// <summary>
    /// A convex corner whose triangle holds no other corner (strict), or failing that any convex corner, or -1.
    /// The degenerate cases (self-crossing or collinear outlines) still finish because the caller falls back.
    /// </summary>
    private static int FindEar(List<int> ring, Vec2[] p, double eps, bool strict)
    {
        int m = ring.Count;
        for (int i = 0; i < m; i++)
        {
            var a = p[ring[(i + m - 1) % m]];
            var b = p[ring[i]];
            var c = p[ring[(i + 1) % m]];
            if (Vec2.Cross(b - a, c - b) <= eps) continue;
            if (!strict) return i;
            bool clear = true;
            for (int j = 0; j < m && clear; j++)
            {
                if (j == i || j == (i + m - 1) % m || j == (i + 1) % m) continue;
                var q = p[ring[j]];
                if (q == a || q == b || q == c) continue;
                if (Inside(q, a, b, c)) clear = false;
            }
            if (clear) return i;
        }
        return -1;
    }

    private static bool Inside(Vec2 q, Vec2 a, Vec2 b, Vec2 c) =>
        Vec2.Cross(b - a, q - a) >= 0 && Vec2.Cross(c - b, q - b) >= 0 && Vec2.Cross(a - c, q - c) >= 0;

    private static void Fan(int n, List<(int, int, int)> result)
    {
        for (int i = 1; i < n - 1; i++) result.Add((0, i, i + 1));
    }

    private static Vec3 NewellNormal(IReadOnlyList<Vec3> c)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < c.Count; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % c.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        return new Vec3(x, y, z).Normalized();
    }

    /// <summary>Triangulates a mesh polygon; returns triangles as the mesh's point indices.</summary>
    public static List<(int A, int B, int C)> Triangulate(Mesh mesh, Polygon polygon)
    {
        var corners = polygon.Points.Select(i => mesh.Points[i]).ToList();
        return Triangulate(corners, mesh.Normal(polygon))
            .Select(t => (polygon.Points[t.A], polygon.Points[t.B], polygon.Points[t.C]))
            .ToList();
    }
}
