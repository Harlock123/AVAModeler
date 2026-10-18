using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>A convex planar polygon being cut up by the boolean code, with the face it came from.</summary>
internal sealed class CsgPolygon(List<Vec3> vertices, Plane plane, int surface, int group)
{
    public List<Vec3> Vertices = vertices;
    public Plane Plane = plane;
    public readonly int Surface = surface;
    /// <summary>Which original face this is a piece of, so the pieces can be joined back up.</summary>
    public readonly int Group = group;

    public void Flip()
    {
        Vertices.Reverse();
        Plane = Plane.Flipped();
    }
}

/// <summary>Cutting convex polygons by planes, shared by the boolean and drill code.</summary>
internal static class PlaneSplit
{
    public const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

    /// <summary>Which side of a plane each vertex is on, and the polygon as a whole.</summary>
    public static int Classify(Plane plane, List<Vec3> vertices, double epsilon, int[] types)
    {
        int all = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            double d = plane.Distance(vertices[i]);
            types[i] = d < -epsilon ? Back : d > epsilon ? Front : Coplanar;
            all |= types[i];
        }
        return all;
    }

    /// <summary>
    /// Cuts a convex polygon that spans a plane into its front and back parts (null when a part has under three points).
    /// Where an edge crosses, the crossing is worked out from the edge's lesser end, so two faces sharing the edge get
    /// the very same new point.
    /// </summary>
    public static (List<Vec3>? Front, List<Vec3>? Back) Split(Plane plane, List<Vec3> vertices, int[] types)
    {
        var f = new List<Vec3>(vertices.Count + 1);
        var b = new List<Vec3>(vertices.Count + 1);
        for (int i = 0; i < vertices.Count; i++)
        {
            int j = (i + 1) % vertices.Count;
            int ti = types[i], tj = types[j];
            var vi = vertices[i];
            if (ti != Back) f.Add(vi);
            if (ti != Front) b.Add(vi);
            if ((ti | tj) == Spanning)
            {
                var vj = vertices[j];
                var (lo, hi) = Less(vi, vj) ? (vi, vj) : (vj, vi);
                double dl = plane.Distance(lo), dh = plane.Distance(hi);
                var v = Vec3.Lerp(lo, hi, dl / (dl - dh));
                f.Add(v);
                b.Add(v);
            }
        }
        return (f.Count >= 3 ? f : null, b.Count >= 3 ? b : null);
    }

    private static bool Less(Vec3 a, Vec3 b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
}

/// <summary>
/// A BSP tree of convex polygons, as in Evan Wallace's csg.js: each node splits space by a polygon's plane, and
/// clipping one tree to another removes the parts of its polygons inside the other's solid. Built and walked with
/// explicit stacks, since a convex solid makes a tree as deep as it has faces.
/// </summary>
internal sealed class BspTree
{
    private sealed class Node
    {
        public Plane Plane;
        public bool HasPlane;
        public Node? Front, Back;
        public List<CsgPolygon> Polygons = [];
    }

    private readonly Node _root = new();
    private readonly double _epsilon;
    private int[] _types = new int[16];

    public BspTree(IEnumerable<CsgPolygon> polygons, double epsilon)
    {
        _epsilon = epsilon;
        Add(polygons.ToList());
    }

    /// <summary>Files polygons into the tree, splitting them as they go down.</summary>
    public void Add(List<CsgPolygon> polygons)
    {
        var stack = new Stack<(Node, List<CsgPolygon>)>();
        stack.Push((_root, polygons));
        while (stack.Count > 0)
        {
            var (node, list) = stack.Pop();
            if (list.Count == 0) continue;
            if (!node.HasPlane)
            {
                node.Plane = list[0].Plane;
                node.HasPlane = true;
            }
            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            foreach (var p in list) SplitPolygon(node.Plane, p, node.Polygons, node.Polygons, front, back);
            if (front.Count > 0) stack.Push((node.Front ??= new Node(), front));
            if (back.Count > 0) stack.Push((node.Back ??= new Node(), back));
        }
    }

    /// <summary>The parts of the polygons outside this tree's solid.</summary>
    public List<CsgPolygon> Clip(List<CsgPolygon> polygons)
    {
        var result = new List<CsgPolygon>();
        var stack = new Stack<(Node, List<CsgPolygon>)>();
        stack.Push((_root, polygons));
        while (stack.Count > 0)
        {
            var (node, list) = stack.Pop();
            if (!node.HasPlane)
            {
                result.AddRange(list);
                continue;
            }
            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            foreach (var p in list) SplitPolygon(node.Plane, p, front, back, front, back);
            if (node.Front != null) stack.Push((node.Front, front));
            else result.AddRange(front);
            if (node.Back != null) stack.Push((node.Back, back));
        }
        return result;
    }

    /// <summary>Removes the parts of this tree's polygons that lie inside another tree's solid.</summary>
    public void ClipTo(BspTree other)
    {
        foreach (var node in Nodes()) node.Polygons = other.Clip(node.Polygons);
    }

    /// <summary>Turns the solid inside out.</summary>
    public void Invert()
    {
        foreach (var node in Nodes())
        {
            foreach (var p in node.Polygons) p.Flip();
            node.Plane = node.Plane.Flipped();
            (node.Front, node.Back) = (node.Back, node.Front);
        }
    }

    public List<CsgPolygon> AllPolygons() => Nodes().SelectMany(n => n.Polygons).ToList();

    private IEnumerable<Node> Nodes()
    {
        var stack = new Stack<Node>([_root]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.Front != null) stack.Push(n.Front);
            if (n.Back != null) stack.Push(n.Back);
        }
    }

    private void SplitPolygon(Plane plane, CsgPolygon p, List<CsgPolygon> coplanarFront, List<CsgPolygon> coplanarBack,
        List<CsgPolygon> front, List<CsgPolygon> back)
    {
        if (_types.Length < p.Vertices.Count) _types = new int[p.Vertices.Count * 2];
        switch (PlaneSplit.Classify(plane, p.Vertices, _epsilon, _types))
        {
            case PlaneSplit.Coplanar:
                (Vec3.Dot(plane.Normal, p.Plane.Normal) > 0 ? coplanarFront : coplanarBack).Add(p);
                break;
            case PlaneSplit.Front:
                front.Add(p);
                break;
            case PlaneSplit.Back:
                back.Add(p);
                break;
            default:
                var (f, b) = PlaneSplit.Split(plane, p.Vertices, _types);
                if (f != null) front.Add(new CsgPolygon(f, p.Plane, p.Surface, p.Group));
                if (b != null) back.Add(new CsgPolygon(b, p.Plane, p.Surface, p.Group));
                break;
        }
    }
}
