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

    /// <summary>
    /// Whether faces whose planes nearly match (within <see cref="WeldShare"/> of the models' size) are cut as one
    /// plane, and the operand's points that nearly meet the foreground's are moved onto them, so near-coplanar faces
    /// don't leave slivers or cracks. On by default.
    /// </summary>
    public bool Weld { get; init; } = true;

    /// <summary>How near, as a share of the models' size, counts as the same plane or point when welding.</summary>
    public const double WeldShare = 5e-6;
}

/// <summary>
/// Solid booleans between two meshes by BSP trees (in the manner of csg.js, on n-gons in double precision). Both
/// meshes should be closed solids with outward faces; open meshes give something, but not a meaningful solid. The
/// result is a new mesh: faces keep the surface of the side they came from, coincident points are merged, slivers
/// dropped, T-junctions closed (so closed inputs give a watertight result) and the pieces of each cut face rejoined
/// where they make a simple polygon. The foreground's points and lines pass through unchanged; the operand's are
/// ignored. Points no polygon uses are not kept. Faces keep the UVs of the faces they were cut from.
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
        double weld = options.Weld ? Math.Max(tol, BooleanOptions.WeldShare * Math.Max(foreground.Bounds().Include(operand.Bounds()).Extent, 1e-3)) : 0;
        var operandAt = weld > 0 ? SnapTo(operand, foreground, weld) : operand;
        var a = ToCsg(foreground, 0, tol);
        var b = ToCsg(operandAt, fgCount, tol);
        if (weld > 0) WeldPlanes([.. a, .. b], weld);

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
        var sources = new Dictionary<Polygon, (Mesh, Polygon)>();
        foreach (var p in pieces)
        {
            int first = result.Points.Count;
            result.Points.AddRange(p.Vertices);
            int surface = p.Group >= fgCount && options.CutSurface is int cut ? cut : p.Surface;
            var poly = new Polygon(Enumerable.Range(first, p.Vertices.Count), surface);
            result.Polygons.Add(poly);
            if (options.Rejoin) groups[poly] = p.Group;
            sources[poly] = p.Group < fgCount ? (foreground, foreground.Polygons[p.Group]) : (operand, operand.Polygons[p.Group - fgCount]);
        }
        var originals = foreground.Points.Concat(operand.Points).ToHashSet();
        MeshCleanup.AfterCut(result, tol, groups, originals);
        UVTransfer.FromSources(result, sources, foreground, operand);

        MeshCleanup.AppendPointsAndLines(result, foreground);
        return result;
    }

    /// <summary>A copy of the operand whose points within <paramref name="weld"/> of one of the target's are moved onto it.</summary>
    private static Mesh SnapTo(Mesh operand, Mesh target, double weld)
    {
        var copy = operand.Clone();
        double cell = weld * 2;
        var grid = new Dictionary<(long, long, long), List<Vec3>>();
        (long, long, long) Key(Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        foreach (var p in target.Points)
        {
            var k = Key(p);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = [];
            l.Add(p);
        }
        for (int i = 0; i < copy.Points.Count; i++)
        {
            var p = copy.Points[i];
            var (x, y, z) = Key(p);
            Vec3? best = null;
            double bestD = weld * weld;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                    foreach (var q in l)
                        if ((q - p).LengthSquared is var d && d <= bestD && d > 0) { bestD = d; best = q; }
            if (best is { } b) copy.Points[i] = b;
        }
        // Points that nearly lie on the target's face planes go onto them (onto every one they're near, so a corner
        // stays one point for all its faces).
        // Each face's plane, with its bounds a weld wider: a point counts as on a face only near the face itself.
        var faces = new List<(Plane Plane, Box3 Near)>();
        foreach (var poly in target.Polygons)
        {
            if (poly.Count < 3) continue;
            var n = target.Normal(poly);
            if (n.LengthSquared == 0) continue;
            var box = target.Bounds(poly.Points);
            faces.Add((Plane.FromPointNormal(target.Centroid(poly), n), new Box3(box.Min - Vec3.One * weld, box.Max + Vec3.One * weld)));
        }
        for (int i = 0; i < copy.Points.Count; i++)
        {
            var p = copy.Points[i];
            var near = faces.Where(f => f.Near.Contains(p) && Math.Abs(f.Plane.Distance(p)) is var d && d <= weld && d > 0).Select(f => f.Plane).ToList();
            if (near.Count == 0) continue;
            for (int it = 0; it < 8; it++)
                foreach (var q in near) p = q.Project(p);
            if ((p - copy.Points[i]).Length <= weld * 2) copy.Points[i] = p;
        }
        copy.Touch();
        return copy;
    }

    /// <summary>
    /// Gives pieces whose planes nearly match (normals within a hair, facing either way, and within
    /// <paramref name="weld"/> of each other) one plane, the first met, with their points pressed onto it.
    /// </summary>
    private static void WeldPlanes(List<CsgPolygon> pieces, double weld)
    {
        var canon = new List<Plane>();
        foreach (var p in pieces)
        {
            Plane? match = null;
            foreach (var c in canon)
            {
                double dot = Vec3.Dot(c.Normal, p.Plane.Normal);
                if (Math.Abs(dot) < 1 - 1e-9) continue;
                var same = dot > 0 ? c : c.Flipped();
                if (Math.Abs(same.D - p.Plane.D) <= weld && p.Vertices.All(v => Math.Abs(same.Distance(v)) <= weld * 2)) { match = same; break; }
            }
            if (match is not { } m) { canon.Add(p.Plane); continue; }
            // On the plane exactly, so the BSP never finds a piece across its own plane.
            p.Plane = m;
            for (int i = 0; i < p.Vertices.Count; i++) p.Vertices[i] = m.Project(p.Vertices[i]);
        }
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
