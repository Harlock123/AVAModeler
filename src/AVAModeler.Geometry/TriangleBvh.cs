using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>Where a ray met a mesh: the face, the distance along the ray (in units of its direction's length) and the point.</summary>
public readonly record struct RayHit(int Polygon, double Distance, Vec3 Point);

/// <summary>The place on a mesh nearest a point: the face, the place itself and how far away it is.</summary>
public readonly record struct NearestHit(int Polygon, Vec3 Point, double Distance);

/// <summary>
/// A bounding volume hierarchy over a mesh's faces (as triangles), for picking: the first face a ray hits, and the
/// face nearest a point. It is a snapshot; build a new one when the mesh changes (compare <see cref="Mesh.Version"/>).
/// </summary>
public sealed class TriangleBvh
{
    private const int LeafSize = 4;

    private readonly struct Node(Box3 bounds, int start, int count, int left, int right)
    {
        public readonly Box3 Bounds = bounds;
        public readonly int Start = start, Count = count, Left = left, Right = right;
        public bool IsLeaf => Left < 0;
    }

    private readonly (Vec3 A, Vec3 B, Vec3 C, int Polygon)[] _tris;
    private readonly List<Node> _nodes = [];

    /// <summary>The version of the mesh this was built from.</summary>
    public int MeshVersion { get; }

    public int TriangleCount => _tris.Length;

    public TriangleBvh(Mesh mesh)
    {
        MeshVersion = mesh.Version;
        _tris = Triangulator.TriangulateMesh(mesh)
            .Select(t => (mesh.Points[t.A], mesh.Points[t.B], mesh.Points[t.C], t.Polygon)).ToArray();
        if (_tris.Length > 0) Build(0, _tris.Length);
    }

    /// <summary>The nearest face the ray hits (either side of a face counts), no further than a distance, or null.</summary>
    public RayHit? Raycast(Ray ray, double maxDistance = double.PositiveInfinity)
    {
        if (_nodes.Count == 0) return null;
        RayHit? best = null;
        double limit = maxDistance;
        var stack = new Stack<int>([0]);
        while (stack.Count > 0)
        {
            var node = _nodes[stack.Pop()];
            var enter = ray.IntersectBox(node.Bounds);
            if (enter == null || enter > limit) continue;
            if (!node.IsLeaf)
            {
                stack.Push(node.Left);
                stack.Push(node.Right);
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var (a, b, c, poly) = _tris[i];
                if (ray.IntersectTriangle(a, b, c, 0) is { } hit && hit.T <= limit)
                {
                    limit = hit.T;
                    best = new RayHit(poly, hit.T, ray.At(hit.T));
                }
            }
        }
        return best;
    }

    /// <summary>The place on any face nearest a point, or null for a mesh with no faces.</summary>
    public NearestHit? Nearest(Vec3 point)
    {
        if (_nodes.Count == 0) return null;
        NearestHit? best = null;
        double bestSq = double.PositiveInfinity;
        var stack = new Stack<int>([0]);
        while (stack.Count > 0)
        {
            var node = _nodes[stack.Pop()];
            if (BoxDistanceSquared(node.Bounds, point) > bestSq) continue;
            if (!node.IsLeaf)
            {
                // Visit the nearer child first so the further one is more often skipped.
                bool leftFirst = BoxDistanceSquared(_nodes[node.Left].Bounds, point) <= BoxDistanceSquared(_nodes[node.Right].Bounds, point);
                stack.Push(leftFirst ? node.Right : node.Left);
                stack.Push(leftFirst ? node.Left : node.Right);
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var (a, b, c, poly) = _tris[i];
                var q = ClosestOnTriangle(point, a, b, c);
                double d = (q - point).LengthSquared;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = new NearestHit(poly, q, Math.Sqrt(d));
                }
            }
        }
        return best;
    }

    /// <summary>Builds the node for triangles [start, start + count), splitting at the median of the longest axis.</summary>
    private int Build(int start, int count)
    {
        var bounds = Box3.Empty;
        var centres = Box3.Empty;
        for (int i = start; i < start + count; i++)
        {
            var (a, b, c, _) = _tris[i];
            bounds = bounds.Include(a).Include(b).Include(c);
            centres = centres.Include((a + b + c) / 3);
        }
        int index = _nodes.Count;
        _nodes.Add(default);
        if (count <= LeafSize || centres.Extent == 0)
        {
            _nodes[index] = new Node(bounds, start, count, -1, -1);
            return index;
        }
        var size = centres.Size;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        Array.Sort(_tris, start, count, Comparer<(Vec3 A, Vec3 B, Vec3 C, int Polygon)>.Create((s, t) =>
            (s.A[axis] + s.B[axis] + s.C[axis]).CompareTo(t.A[axis] + t.B[axis] + t.C[axis])));
        int half = count / 2;
        int left = Build(start, half);
        int right = Build(start + half, count - half);
        _nodes[index] = new Node(bounds, start, count, left, right);
        return index;
    }

    private static double BoxDistanceSquared(Box3 b, Vec3 p)
    {
        double dx = Math.Max(Math.Max(b.Min.X - p.X, 0), p.X - b.Max.X);
        double dy = Math.Max(Math.Max(b.Min.Y - p.Y, 0), p.Y - b.Max.Y);
        double dz = Math.Max(Math.Max(b.Min.Z - p.Z, 0), p.Z - b.Max.Z);
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The nearest point of a triangle to p (Ericson, Real-Time Collision Detection, 5.1.5).</summary>
    private static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        double denom = va + vb + vc;
        if (denom == 0) return a;
        double v = vb / denom, w = vc / denom;
        return a + ab * v + ac * w;
    }
}
