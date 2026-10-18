using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>
/// The primitive shapes, built into a mesh with shared points and every face wound to face outward. A box with no
/// depth along an axis is a flat grid; a disc with no height is a flat polygon.
/// </summary>
public static class Primitives
{
    /// <summary>A box (segmented on each axis) centred on a point.</summary>
    public static void Box(Mesh mesh, Vec3 center, Vec3 size, int segX, int segY, int segZ, int surface)
    {
        segX = Math.Max(1, segX);
        segY = Math.Max(1, segY);
        segZ = Math.Max(1, segZ);
        var half = new Vec3(Math.Abs(size.X), Math.Abs(size.Y), Math.Abs(size.Z)) / 2;
        var min = center - half;
        bool flatX = half.X < 1e-12, flatY = half.Y < 1e-12, flatZ = half.Z < 1e-12;
        int flats = (flatX ? 1 : 0) + (flatY ? 1 : 0) + (flatZ ? 1 : 0);
        if (flats >= 2)
        {
            // A line or a point.
            int a = mesh.AddPoint(min), b = mesh.AddPoint(center + half);
            mesh.AddPolygon(a == b || flats == 3 ? [a] : [a, b], surface);
            return;
        }
        var points = new Dictionary<(int, int, int), int>();
        int P(int i, int j, int k)
        {
            if (points.TryGetValue((i, j, k), out int idx)) return idx;
            var p = new Vec3(min.X + 2 * half.X * i / segX, min.Y + 2 * half.Y * j / segY, min.Z + 2 * half.Z * k / segZ);
            return points[(i, j, k)] = mesh.AddPoint(p);
        }
        var faces = new List<int>();
        void Grid(Axis normalAxis, int fixedIndex)
        {
            // The two other axes span the face.
            (int na, int nb) = normalAxis switch { Axis.X => (segY, segZ), Axis.Y => (segX, segZ), _ => (segX, segY) };
            for (int a = 0; a < na; a++)
            for (int b = 0; b < nb; b++)
            {
                (int, int, int) At(int u, int v) => normalAxis switch
                {
                    Axis.X => (fixedIndex, u, v),
                    Axis.Y => (u, fixedIndex, v),
                    _ => (u, v, fixedIndex),
                };
                var c = new[] { At(a, b), At(a + 1, b), At(a + 1, b + 1), At(a, b + 1) };
                faces.Add(mesh.AddPolygon(c.Select(t => P(t.Item1, t.Item2, t.Item3)), surface));
            }
        }
        if (flatX) Grid(Axis.X, 0);
        else if (flatY) Grid(Axis.Y, 0);
        else if (flatZ) Grid(Axis.Z, 0);
        else
        {
            Grid(Axis.X, 0); Grid(Axis.X, segX);
            Grid(Axis.Y, 0); Grid(Axis.Y, segY);
            Grid(Axis.Z, 0); Grid(Axis.Z, segZ);
        }
        if (flats == 0) OrientOutward(mesh, faces, center);
        else FaceTowardViewer(mesh, faces, flatX ? Axis.X : flatY ? Axis.Y : Axis.Z);
    }

    /// <summary>A globe: rings of quads between the poles, triangles at the poles, its axis along X, Y or Z.</summary>
    public static void Globe(Mesh mesh, Vec3 center, Vec3 radius, int sides, int segments, Axis axis, int surface)
    {
        sides = Math.Max(3, sides);
        segments = Math.Max(2, segments);
        var faces = new List<int>();
        int bottom = mesh.AddPoint(center + Along(axis, -1, 0, 0, radius));
        var rings = new int[segments - 1, sides];
        for (int s = 1; s < segments; s++)
        {
            double phi = Math.PI * s / segments - Math.PI / 2;
            for (int i = 0; i < sides; i++)
            {
                double theta = 2 * Math.PI * i / sides;
                rings[s - 1, i] = mesh.AddPoint(center + Along(axis, Math.Sin(phi), Math.Cos(phi) * Math.Cos(theta), Math.Cos(phi) * Math.Sin(theta), radius));
            }
        }
        int top = mesh.AddPoint(center + Along(axis, 1, 0, 0, radius));
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            faces.Add(mesh.AddPolygon([bottom, rings[0, j], rings[0, i]], surface));
            for (int s = 0; s < segments - 2; s++)
                faces.Add(mesh.AddPolygon([rings[s, i], rings[s, j], rings[s + 1, j], rings[s + 1, i]], surface));
            faces.Add(mesh.AddPolygon([top, rings[segments - 2, i], rings[segments - 2, j]], surface));
        }
        OrientOutward(mesh, faces, center);
    }

    /// <summary>
    /// A point on an axis-aligned ellipsoid-ish frame: <paramref name="h"/> along the axis, <paramref name="a"/> and
    /// <paramref name="b"/> across it, each scaled by the radius on that axis.
    /// </summary>
    private static Vec3 Along(Axis axis, double h, double a, double b, Vec3 r) => axis switch
    {
        Axis.X => new Vec3(h * r.X, a * r.Y, b * r.Z),
        Axis.Y => new Vec3(a * r.X, h * r.Y, b * r.Z),
        _ => new Vec3(a * r.X, b * r.Y, h * r.Z),
    };

    /// <summary>A tessellated ball: an icosahedron subdivided <paramref name="level"/> times, pushed out to the ellipsoid.</summary>
    public static void TessellatedBall(Mesh mesh, Vec3 center, Vec3 radius, int level, int surface)
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        var verts = new List<Vec3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        var tris = new List<(int, int, int)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        for (int l = 0; l < Math.Clamp(level, 0, 6); l++)
        {
            var mid = new Dictionary<(int, int), int>();
            int M(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (mid.TryGetValue(key, out int m)) return m;
                verts.Add((verts[a] + verts[b]) / 2);
                return mid[key] = verts.Count - 1;
            }
            var next = new List<(int, int, int)>(tris.Count * 4);
            foreach (var (a, b, c) in tris)
            {
                int ab = M(a, b), bc = M(b, c), ca = M(c, a);
                next.Add((a, ab, ca)); next.Add((b, bc, ab)); next.Add((c, ca, bc)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
        int offset = mesh.Points.Count;
        foreach (var v in verts) mesh.Points.Add(center + Vec3.Scale(v.Normalized(), radius));
        var faces = tris.Select(f => mesh.AddPolygon([f.Item1 + offset, f.Item2 + offset, f.Item3 + offset], surface)).ToList();
        OrientOutward(mesh, faces, center);
    }

    /// <summary>
    /// A disc or cylinder (or, with <paramref name="topScale"/> 0, a cone) along an axis: <paramref name="height"/>
    /// along it, with flat caps (an n-gon each); with no height, one flat disc.
    /// </summary>
    public static void Disc(Mesh mesh, Vec3 center, Vec3 radius, double height, int sides, int segments, Axis axis, double topScale, bool caps, int surface)
    {
        sides = Math.Max(3, sides);
        segments = Math.Max(1, segments);
        var faces = new List<int>();
        if (Math.Abs(height) < 1e-12)
        {
            var ring = Enumerable.Range(0, sides).Select(i => mesh.AddPoint(center + Across(axis, i, sides, radius, 0))).ToList();
            faces.Add(mesh.AddPolygon(ring, surface));
            FaceTowardViewer(mesh, faces, axis);
            return;
        }
        var rings = new List<int[]>();
        for (int s = 0; s <= segments; s++)
        {
            double f = (double)s / segments;
            double scale = 1 + (topScale - 1) * f;
            double h = -height / 2 + height * f;
            if (scale < 1e-12)
            {
                rings.Add([mesh.AddPoint(center + Across(axis, 0, sides, Vec3.Zero, h))]);
                break;
            }
            rings.Add(Enumerable.Range(0, sides).Select(i => mesh.AddPoint(center + Across(axis, i, sides, radius * scale, h))).ToArray());
        }
        for (int s = 0; s + 1 < rings.Count; s++)
        {
            var a = rings[s];
            var b = rings[s + 1];
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                faces.Add(b.Length == 1 ? mesh.AddPolygon([a[i], a[j], b[0]], surface) : mesh.AddPolygon([a[i], a[j], b[j], b[i]], surface));
            }
        }
        if (caps)
        {
            faces.Add(mesh.AddPolygon(rings[0], surface));
            if (rings[^1].Length > 1) faces.Add(mesh.AddPolygon(rings[^1], surface));
        }
        OrientOutward(mesh, faces, center);
    }

    /// <summary>The i-th of n points round a ring across an axis, at a height along it (not scaled by the radius).</summary>
    private static Vec3 Across(Axis axis, int i, int sides, Vec3 r, double h)
    {
        double theta = 2 * Math.PI * i / sides, c = Math.Cos(theta), s = Math.Sin(theta);
        return axis switch
        {
            Axis.X => new Vec3(h, c * r.Y, s * r.Z),
            Axis.Y => new Vec3(c * r.X, h, s * r.Z),
            _ => new Vec3(c * r.X, s * r.Y, h),
        };
    }

    /// <summary>Turns each face of a closed, roughly convex shape to face away from its centre.</summary>
    public static void OrientOutward(Mesh mesh, IEnumerable<int> faces, Vec3 center)
    {
        foreach (int f in faces)
        {
            var p = mesh.Polygons[f];
            if (p.Count < 3) continue;
            if (Vec3.Dot(mesh.Normal(p), mesh.Centroid(p) - center) < 0) p.Points.Reverse();
        }
        mesh.Touch();
    }

    /// <summary>Turns flat faces to face the viewer of the view that looks down their axis (toward -axis).</summary>
    public static void FaceTowardViewer(Mesh mesh, IEnumerable<int> faces, Axis axis)
    {
        // The Top view looks down -Y, so a flat shape in XZ faces +Y; Front looks down +Z (faces -Z); Side looks down -X (faces +X).
        var toward = axis switch { Axis.Y => Vec3.UnitY, Axis.Z => -Vec3.UnitZ, _ => Vec3.UnitX };
        foreach (int f in faces)
        {
            var p = mesh.Polygons[f];
            if (p.Count >= 3 && Vec3.Dot(mesh.Normal(p), toward) < 0) p.Points.Reverse();
        }
        mesh.Touch();
    }
}
