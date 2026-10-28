using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A world-space triangle ready to trace: its corners, face normal, corner shading normals and material, and (for a
/// textured one) where its texturing is kept, or -1.
/// </summary>
internal readonly struct Triangle(Vec3 a, Vec3 b, Vec3 c, Vec3 normal, Vec3 na, Vec3 nb, Vec3 nc, int material, bool doubleSided, int texture = -1)
{
    public readonly Vec3 A = a, E1 = b - a, E2 = c - a;
    public readonly Vec3 Normal = normal, NA = na, NB = nb, NC = nc;
    public readonly int Material = material;
    public readonly bool DoubleSided = doubleSided;
    public readonly int Texture = texture;
    public Vec3 B => A + E1;
    public Vec3 C => A + E2;
}

/// <summary>Where a ray hit: the triangle, the distance along the ray, and the barycentric u and v.</summary>
internal readonly record struct Hit(int Triangle, double T, double U, double V);

/// <summary>A bounding volume hierarchy over triangles, split at the median along the widest axis.</summary>
internal sealed class Bvh
{
    private struct Node
    {
        public Vec3 Min, Max;
        /// <summary>A leaf (Count &gt; 0) holds Count triangles from <see cref="_order"/>[Left]; an inner node has two children.</summary>
        public int Left, Right, Count;
    }

    private const int LeafSize = 4;
    private readonly Triangle[] _tris;
    private readonly int[] _order;
    private readonly List<Node> _nodes = [];

    public Bvh(Triangle[] triangles)
    {
        _tris = triangles;
        _order = Enumerable.Range(0, triangles.Length).ToArray();
        if (triangles.Length > 0)
        {
            var centroids = triangles.Select(t => (t.A + t.B + t.C) / 3).ToArray();
            Build(0, triangles.Length, centroids);
        }
    }

    public Triangle this[int i] => _tris[i];

    private int Build(int start, int count, Vec3[] centroids)
    {
        var box = Box3.Empty;
        var cbox = Box3.Empty;
        for (int i = start; i < start + count; i++)
        {
            var t = _tris[_order[i]];
            box = box.Include(t.A).Include(t.B).Include(t.C);
            cbox = cbox.Include(centroids[_order[i]]);
        }
        int index = _nodes.Count;
        _nodes.Add(new Node { Min = box.Min, Max = box.Max, Left = start, Count = count });
        var size = cbox.Size;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        if (count <= LeafSize || size[axis] <= 0) return index;

        var keys = new double[count];
        for (int i = 0; i < count; i++) keys[i] = centroids[_order[start + i]][axis];
        keys.AsSpan().Sort(_order.AsSpan(start, count));
        int half = count / 2;
        int left = Build(start, half, centroids);
        int right = Build(start + half, count - half, centroids);
        _nodes[index] = new Node { Min = box.Min, Max = box.Max, Left = left, Right = right, Count = 0 };
        return index;
    }

    /// <summary>
    /// The nearest hit closer than <paramref name="tMax"/> and further than <paramref name="tMin"/>. The back of a
    /// single-sided triangle (facing away from the ray's origin) can't be hit.
    /// </summary>
    public bool Intersect(Vec3 origin, Vec3 dir, double tMin, double tMax, out Hit hit)
    {
        hit = default;
        if (_nodes.Count == 0) return false;
        var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        bool found = false;
        double best = tMax;
        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (!HitsBox(node.Min, node.Max, origin, inv, tMin, best)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Left; i < node.Left + node.Count; i++)
                {
                    int ti = _order[i];
                    if (HitTriangle(_tris[ti], origin, dir, tMin, best, out double t, out double u, out double v))
                    {
                        best = t;
                        hit = new Hit(ti, t, u, v);
                        found = true;
                    }
                }
            }
            else
            {
                // Median splits keep the tree about log2(n) deep, well inside the stack.
                stack[sp++] = node.Right;
                stack[sp++] = node.Left;
            }
        }
        return found;
    }

    private static bool HitsBox(Vec3 min, Vec3 max, Vec3 o, Vec3 inv, double tMin, double tMax)
    {
        double t1 = (min.X - o.X) * inv.X, t2 = (max.X - o.X) * inv.X;
        double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
        t1 = (min.Y - o.Y) * inv.Y;
        t2 = (max.Y - o.Y) * inv.Y;
        lo = Math.Max(lo, Math.Min(t1, t2));
        hi = Math.Min(hi, Math.Max(t1, t2));
        t1 = (min.Z - o.Z) * inv.Z;
        t2 = (max.Z - o.Z) * inv.Z;
        lo = Math.Max(lo, Math.Min(t1, t2));
        hi = Math.Min(hi, Math.Max(t1, t2));
        // NaN (a zero direction component with the origin on a slab face) compares false, which keeps the box.
        return !(hi < Math.Max(lo, tMin)) && !(lo > tMax);
    }

    private static bool HitTriangle(in Triangle tri, Vec3 o, Vec3 d, double tMin, double tMax, out double t, out double u, out double v)
    {
        t = u = v = 0;
        if (!tri.DoubleSided && Vec3.Dot(d, tri.Normal) >= 0) return false;
        var p = Vec3.Cross(d, tri.E2);
        double det = Vec3.Dot(tri.E1, p);
        if (Math.Abs(det) < 1e-300) return false;
        double invDet = 1 / det;
        var s = o - tri.A;
        u = Vec3.Dot(s, p) * invDet;
        if (u < 0 || u > 1) return false;
        var q = Vec3.Cross(s, tri.E1);
        v = Vec3.Dot(d, q) * invDet;
        if (v < 0 || u + v > 1) return false;
        t = Vec3.Dot(tri.E2, q) * invDet;
        return t > tMin && t < tMax;
    }
}
