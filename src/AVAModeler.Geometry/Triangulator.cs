using AVAModeler.Core;
using LibTessDotNet.Double;
using TessVec3 = LibTessDotNet.Double.Vec3;
using Vec3 = AVAModeler.Core.Vec3;

namespace AVAModeler.Geometry;

/// <summary>A triangle of a mesh face: the polygon it came from and three mesh point indices, wound as the face is.</summary>
public readonly record struct MeshTriangle(int Polygon, int A, int B, int C);

/// <summary>
/// Turns faces into triangles, for drawing, ray casting and the Triple command. Concave, many-sided and non-planar
/// faces are flattened onto the plane of their Newell normal and cut up with LibTess; triangles and convex faces take
/// a quicker path. Triangles keep their face's winding, so they face the same way.
/// </summary>
public static class Triangulator
{
    /// <summary>
    /// Triangles covering a face, as index triples into the face's own point list (0 to Count-1). Points, lines and
    /// faces with no area give none.
    /// </summary>
    public static int[] TriangulatePolygon(Mesh mesh, Polygon polygon) =>
        polygon.Count < 3 ? [] : Triangulate(polygon.Points.Select(i => mesh.Points[i]).ToArray());

    /// <summary>Triangles covering a closed outline of points (any number, any shape), as index triples into the list.</summary>
    public static int[] Triangulate(IReadOnlyList<Vec3> outline)
    {
        int n = outline.Count;
        if (n < 3) return [];
        if (n == 3) return [0, 1, 2];
        var flat = Flatten(outline, out bool ok);
        if (!ok) return [];
        if (IsConvex(flat))
        {
            if (n == 4)
                return (outline[0] - outline[2]).LengthSquared <= (outline[1] - outline[3]).LengthSquared
                    ? [0, 1, 2, 0, 2, 3]
                    : [0, 1, 3, 1, 2, 3];
            var fan = new int[(n - 2) * 3];
            for (int i = 1; i + 1 < n; i++) (fan[(i - 1) * 3], fan[(i - 1) * 3 + 1], fan[(i - 1) * 3 + 2]) = (0, i, i + 1);
            return fan;
        }
        return Tessellate(flat, 3).SelectMany(t => t).ToArray();
    }

    /// <summary>
    /// The outline cut into convex pieces (each a list of indices into the outline, wound as it is); a convex outline
    /// comes back whole.
    /// </summary>
    internal static List<int[]> ConvexParts(IReadOnlyList<Vec3> outline)
    {
        if (outline.Count < 3) return [];
        var flat = Flatten(outline, out bool ok);
        if (!ok) return [];
        if (outline.Count == 3 || IsConvex(flat)) return [Enumerable.Range(0, outline.Count).ToArray()];
        return Tessellate(flat, 64);
    }

    /// <summary>Every face of a mesh as triangles, in polygon order, for drawing and ray casts. Points and lines are left out.</summary>
    public static List<MeshTriangle> TriangulateMesh(Mesh mesh)
    {
        var result = new List<MeshTriangle>(mesh.Polygons.Count * 2);
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
        {
            var poly = mesh.Polygons[pi];
            if (poly.Count < 3) continue;
            var t = TriangulatePolygon(mesh, poly);
            for (int i = 0; i < t.Length; i += 3)
                result.Add(new MeshTriangle(pi, poly.Points[t[i]], poly.Points[t[i + 1]], poly.Points[t[i + 2]]));
        }
        return result;
    }

    /// <summary>
    /// The Triple command: replaces each face of four or more points (of those given, or all) with triangles of the
    /// same surface. The first triangle takes the face's place, so polygon indices stay valid; the rest are added at
    /// the end. Returns how many faces were split.
    /// </summary>
    public static int Triple(Mesh mesh, IEnumerable<int>? polygons = null)
    {
        int split = 0;
        foreach (int pi in (polygons ?? Enumerable.Range(0, mesh.Polygons.Count)).Distinct().ToList())
        {
            var poly = mesh.Polygons[pi];
            if (poly.Count < 4) continue;
            var t = TriangulatePolygon(mesh, poly);
            if (t.Length == 0) continue;
            var pts = poly.Points;
            var source = poly.Clone();
            poly.Points = [pts[t[0]], pts[t[1]], pts[t[2]]];
            for (int i = 3; i < t.Length; i += 3)
            {
                var tri = new Polygon([pts[t[i]], pts[t[i + 1]], pts[t[i + 2]]], poly.Surface);
                mesh.Polygons.Add(tri);
                mesh.InheritUVs(tri, source, q => q);
            }
            split++;
        }
        if (split > 0) mesh.Touch();
        return split;
    }

    /// <summary>
    /// The outline in 2D on the plane of its Newell normal, in axes that make the outline run anticlockwise (positive
    /// area). Not ok when the outline has no area.
    /// </summary>
    private static Vec2[] Flatten(IReadOnlyList<Vec3> outline, out bool ok)
    {
        var normal = NewellNormal(outline);
        ok = normal.LengthSquared > 0;
        if (!ok) return [];
        var u = normal.AnyPerpendicular();
        var v = Vec3.Cross(normal, u);
        var flat = new Vec2[outline.Count];
        for (int i = 0; i < flat.Length; i++) flat[i] = new Vec2(Vec3.Dot(outline[i], u), Vec3.Dot(outline[i], v));
        return flat;
    }

    internal static Vec3 NewellNormal(IReadOnlyList<Vec3> pts)
    {
        var s = Vec3.Zero;
        for (int i = 0; i < pts.Count; i++) s += Vec3.Cross(pts[i], pts[(i + 1) % pts.Count]);
        return s.Normalized();
    }

    /// <summary>Whether an anticlockwise 2D outline is convex: it never turns right and goes round exactly once.</summary>
    private static bool IsConvex(Vec2[] p)
    {
        int n = p.Length;
        double turning = 0;
        for (int i = 0; i < n; i++)
        {
            var a = p[(i + 1) % n] - p[i];
            var b = p[(i + 2) % n] - p[(i + 1) % n];
            double cross = Vec2.Cross(a, b);
            if (cross < 0 || a.LengthSquared == 0) return false;
            turning += Math.Atan2(cross, Vec2.Dot(a, b));
        }
        return Math.Abs(turning - 2 * Math.PI) < 1e-6;
    }

    /// <summary>LibTess on an anticlockwise 2D outline: convex polygons of up to <paramref name="size"/> points, wound anticlockwise.</summary>
    private static List<int[]> Tessellate(Vec2[] flat, int size)
    {
        var tess = new Tess { NoEmptyPolygons = true };
        var contour = new ContourVertex[flat.Length];
        for (int i = 0; i < flat.Length; i++) contour[i] = new ContourVertex { Position = new TessVec3(flat[i].X, flat[i].Y, 0), Data = i };
        tess.AddContour(contour, ContourOrientation.Original);
        // Where the outline crosses itself LibTess makes a new point; it stands in for the nearest of the originals.
        tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, size,
            (_, data, weights) => data[Array.IndexOf(weights, weights.Max())], new TessVec3(0, 0, 1));
        var result = new List<int[]>(tess.ElementCount);
        for (int e = 0; e < tess.ElementCount; e++)
        {
            var part = new List<int>(size);
            for (int k = 0; k < size; k++)
            {
                int vi = tess.Elements[e * size + k];
                if (vi == Tess.Undef) break;
                part.Add((int)tess.Vertices[vi].Data);
            }
            if (part.Count < 3) continue;
            double area = 0;
            for (int i = 0; i < part.Count; i++) area += Vec2.Cross(flat[part[i]], flat[part[(i + 1) % part.Count]]);
            if (area < 0) part.Reverse();
            if (area != 0) result.Add([.. part]);
        }
        return result;
    }
}
