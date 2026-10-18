using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>The Boolean tool's operations: the foreground combined with the background (the operand).</summary>
public enum BooleanOperation
{
    /// <summary>Everything inside either.</summary>
    Union,
    /// <summary>The foreground with the operand cut away.</summary>
    Subtract,
    /// <summary>Only what is inside both.</summary>
    Intersect,
    /// <summary>Both meshes together, untouched.</summary>
    Add,
}

/// <summary>Settings for a boolean.</summary>
public sealed record BooleanOptions
{
    /// <summary>A surface for the faces that come from the operand (the walls of a cut); null keeps the operand's own.</summary>
    public int? CutSurface { get; init; }

    /// <summary>The distance below which points and planes count as the same; null scales it to the models (1e-9 of their size).</summary>
    public double? Tolerance { get; init; }

    /// <summary>Whether to join the pieces of each cut face back into as few polygons as it can.</summary>
    public bool Rejoin { get; init; } = true;
}

/// <summary>
/// Solid booleans between two meshes by BSP trees (in the manner of csg.js, on n-gons in double precision). Both
/// meshes should be closed solids with outward faces; open meshes give something, but not a meaningful solid. The
/// result is a new mesh: faces keep the surface of the side they came from, coincident points are merged, slivers
/// dropped, T-junctions closed (so closed inputs give a watertight result) and the pieces of each cut face rejoined
/// where they make a simple polygon. The foreground's points and lines pass through unchanged; the operand's are
/// ignored. Points no polygon uses are not kept.
/// </summary>
public static class Booleans
{
    public static Mesh Union(Mesh foreground, Mesh operand, BooleanOptions? options = null) => Apply(BooleanOperation.Union, foreground, operand, options);
    public static Mesh Subtract(Mesh foreground, Mesh operand, BooleanOptions? options = null) => Apply(BooleanOperation.Subtract, foreground, operand, options);
    public static Mesh Intersect(Mesh foreground, Mesh operand, BooleanOptions? options = null) => Apply(BooleanOperation.Intersect, foreground, operand, options);

    /// <summary>Both meshes in one, the operand's points and polygons after the foreground's, nothing merged.</summary>
    public static Mesh Add(Mesh foreground, Mesh operand)
    {
        var m = foreground.Clone();
        m.Append(operand);
        return m;
    }

    /// <summary>Applies an operation; see the class notes for what comes back.</summary>
    public static Mesh Apply(BooleanOperation operation, Mesh foreground, Mesh operand, BooleanOptions? options = null)
    {
        if (operation == BooleanOperation.Add) return Add(foreground, operand);
        options ??= new BooleanOptions();
        double tol = options.Tolerance ?? Tolerance.For(foreground, operand);
        int fgCount = foreground.Polygons.Count;
        var a = ToCsg(foreground, 0, tol);
        var b = ToCsg(operand, fgCount, tol);

        List<CsgPolygon> pieces;
        if (a.Count == 0 || b.Count == 0)
        {
            // csg.js gets an empty side wrong (an empty minus a solid comes out as the solid turned inside out).
            pieces = operation switch
            {
                BooleanOperation.Union => [.. a, .. b],
                BooleanOperation.Subtract => a,
                _ => [],
            };
        }
        else pieces = Combine(operation, a, b, tol * 10);

        var result = new Mesh();
        var groups = new Dictionary<Polygon, int>();
        foreach (var p in pieces)
        {
            int first = result.Points.Count;
            result.Points.AddRange(p.Vertices);
            int surface = p.Group >= fgCount && options.CutSurface is int cut ? cut : p.Surface;
            var poly = new Polygon(Enumerable.Range(first, p.Vertices.Count), surface);
            result.Polygons.Add(poly);
            if (options.Rejoin) groups[poly] = p.Group;
        }
        var originals = foreground.Points.Concat(operand.Points).ToHashSet();
        MeshCleanup.AfterCut(result, tol, groups, originals);

        MeshCleanup.AppendPointsAndLines(result, foreground);
        return result;
    }

    private static List<CsgPolygon> Combine(BooleanOperation operation, List<CsgPolygon> a, List<CsgPolygon> b, double epsilon)
    {
        var ta = new BspTree(a, epsilon);
        var tb = new BspTree(b, epsilon);
        switch (operation)
        {
            case BooleanOperation.Union:
                ta.ClipTo(tb);
                tb.ClipTo(ta);
                tb.Invert();
                tb.ClipTo(ta);
                tb.Invert();
                ta.Add(tb.AllPolygons());
                return ta.AllPolygons();
            case BooleanOperation.Subtract:
                ta.Invert();
                ta.ClipTo(tb);
                tb.ClipTo(ta);
                tb.Invert();
                tb.ClipTo(ta);
                tb.Invert();
                ta.Add(tb.AllPolygons());
                ta.Invert();
                return ta.AllPolygons();
            default:
                ta.Invert();
                tb.ClipTo(ta);
                tb.Invert();
                ta.ClipTo(tb);
                tb.ClipTo(ta);
                ta.Add(tb.AllPolygons());
                ta.Invert();
                return ta.AllPolygons();
        }
    }

    /// <summary>
    /// A mesh's faces as convex planar polygons for the BSP: flat convex faces as they are, others cut into convex
    /// pieces (non-planar ones into triangles). Each piece's group is its face's index plus an offset.
    /// </summary>
    private static List<CsgPolygon> ToCsg(Mesh mesh, int groupOffset, double tolerance)
    {
        var result = new List<CsgPolygon>();
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
        {
            var poly = mesh.Polygons[pi];
            if (poly.Count < 3 || MeshCleanup.IsDegenerate(mesh, poly.Points, tolerance)) continue;
            var pts = poly.Points.Select(i => mesh.Points[i]).ToList();
            var parts = mesh.NonPlanarity(poly) <= tolerance
                ? Triangulator.ConvexParts(pts)
                : Chunk(Triangulator.Triangulate(pts));
            foreach (var part in parts)
            {
                var v = part.Select(i => pts[i]).ToList();
                var n = Triangulator.NewellNormal(v);
                if (n.LengthSquared == 0) continue;
                var centroid = v.Aggregate(Vec3.Zero, (s, p) => s + p) / v.Count;
                result.Add(new CsgPolygon(v, Plane.FromPointNormal(centroid, n), poly.Surface, pi + groupOffset));
            }
        }
        return result;
    }

    private static List<int[]> Chunk(int[] triples) => triples.Chunk(3).ToList();
}
