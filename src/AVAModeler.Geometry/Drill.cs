using AVAModeler.Core;
using LibTessDotNet.Double;
using TessVec3 = LibTessDotNet.Double.Vec3;
using Vec3 = AVAModeler.Core.Vec3;

namespace AVAModeler.Geometry;

/// <summary>What the Drill tool keeps of each foreground polygon, cut by the operand's outline.</summary>
public enum DrillOperation
{
    /// <summary>Keep only the parts inside the outline.</summary>
    Core,
    /// <summary>Keep only the parts outside the outline (cut holes).</summary>
    Tunnel,
    /// <summary>Keep everything, giving the parts inside a surface of their own.</summary>
    Stencil,
    /// <summary>Keep everything, just cut along the outline.</summary>
    Slice,
}

/// <summary>
/// LightWave's Drill: the operand's faces are seen flat along an axis, as outlines, and pushed endlessly along that
/// axis into prisms; each foreground face is cut where those prisms' walls cross it. This is polygon clipping, not a
/// solid boolean, so it works on open surfaces. Overlapping outlines count as one region.
/// </summary>
public static class Drill
{
    /// <summary>
    /// Drills a foreground mesh with an operand along an axis and returns the new mesh. Faces the outline misses (or
    /// wholly contains) come through as they were; cut faces are split, their pieces welded to their neighbours with
    /// T-junctions closed, and the pieces on each side rejoined where they make a simple polygon. Every piece keeps
    /// its face's surface, except Stencil's inside pieces, which take <paramref name="stencilSurface"/>. Points and
    /// lines come through unchanged; points no polygon uses are not kept.
    /// </summary>
    public static Mesh Apply(Mesh foreground, Mesh operand, Axis axis, DrillOperation operation, int stencilSurface = 0, double? tolerance = null)
    {
        double tol = tolerance ?? Tolerance.For(foreground, operand);
        var outlines = Outlines(operand, axis, tol);
        bool keepInside = operation != DrillOperation.Tunnel, keepOutside = operation != DrillOperation.Core;
        var result = new Mesh { Points = [.. foreground.Points] };
        var groups = new Dictionary<Polygon, int>();
        var types = new int[16];

        for (int pi = 0; pi < foreground.Polygons.Count; pi++)
        {
            var poly = foreground.Polygons[pi];
            if (poly.Count < 3) continue;
            var pts = poly.Points.Select(i => foreground.Points[i]).ToList();
            var box = Flat(pts, axis);
            var near = outlines.Where(o => o.Overlaps(box, tol)).ToList();
            var inside = new List<List<Vec3>>();
            var outside = new List<List<Vec3>>();
            if (near.Count > 0)
                foreach (var part in Triangulator.ConvexParts(pts))
                    Cut(part.Select(i => pts[i]).ToList(), near, axis, tol, inside, outside, ref types);
            else outside.Add(pts);

            int insideSurface = operation == DrillOperation.Stencil ? stencilSurface : poly.Surface;
            if (inside.Count == 0 || outside.Count == 0)
            {
                // Not cut after all: the face goes through whole, as it was.
                bool isInside = inside.Count > 0;
                if (isInside ? keepInside : keepOutside)
                    result.Polygons.Add(new Polygon(poly.Points, isInside ? insideSurface : poly.Surface));
                continue;
            }
            if (keepInside) Emit(result, inside, insideSurface, groups, pi * 2 + 1);
            if (keepOutside) Emit(result, outside, poly.Surface, groups, pi * 2);
        }

        var originals = foreground.Points.ToHashSet();
        MeshCleanup.AfterCut(result, tol, groups, originals);
        MeshCleanup.AppendPointsAndLines(result, foreground);
        return result;
    }

    private static void Emit(Mesh result, List<List<Vec3>> pieces, int surface, Dictionary<Polygon, int> groups, int group)
    {
        foreach (var piece in pieces)
        {
            int first = result.Points.Count;
            result.Points.AddRange(piece);
            var p = new Polygon(Enumerable.Range(first, piece.Count), surface);
            result.Polygons.Add(p);
            groups[p] = group;
        }
    }

    /// <summary>
    /// Sorts the parts of a convex polygon into those inside some outline and those outside all of them. The outlines
    /// don't overlap, so a part inside one is done with; a part outside one goes on to the next.
    /// </summary>
    private static void Cut(List<Vec3> polygon, List<Outline> outlines, Axis axis, double tol,
        List<List<Vec3>> inside, List<List<Vec3>> outside, ref int[] types)
    {
        var pending = new List<List<Vec3>> { polygon };
        foreach (var outline in outlines)
        {
            var next = new List<List<Vec3>>();
            foreach (var start in pending)
            {
                List<Vec3>? rest = start;
                foreach (var plane in outline.Walls(axis))
                {
                    if (types.Length < rest.Count) types = new int[rest.Count * 2];
                    // The walls face out of the outline; a part lying in a wall counts as outside.
                    int side = PlaneSplit.Classify(plane, rest, tol, types);
                    if (side == PlaneSplit.Back) continue;
                    if (side == PlaneSplit.Spanning)
                    {
                        var (front, back) = PlaneSplit.Split(plane, rest, types);
                        if (front != null) next.Add(front);
                        rest = back;
                    }
                    else
                    {
                        next.Add(rest);
                        rest = null;
                    }
                    if (rest == null) break;
                }
                if (rest != null) inside.Add(rest);
            }
            pending = next;
        }
        outside.AddRange(pending);
    }

    /// <summary>A convex piece of the operand's outline, anticlockwise in the flat coordinates of the drill axis.</summary>
    private sealed class Outline(Vec2[] points)
    {
        private readonly Vec2[] _points = points;
        private readonly double _minU = points.Min(p => p.X), _maxU = points.Max(p => p.X);
        private readonly double _minV = points.Min(p => p.Y), _maxV = points.Max(p => p.Y);
        private Plane[]? _walls;

        public bool Overlaps((double MinU, double MaxU, double MinV, double MaxV) b, double tol) =>
            b.MinU <= _maxU + tol && b.MaxU >= _minU - tol && b.MinV <= _maxV + tol && b.MaxV >= _minV - tol;

        /// <summary>The prism's walls as planes facing outward.</summary>
        public Plane[] Walls(Axis axis)
        {
            if (_walls != null) return _walls;
            _walls = new Plane[_points.Length];
            for (int i = 0; i < _points.Length; i++)
            {
                var a = _points[i];
                var d = _points[(i + 1) % _points.Length] - a;
                var n = new Vec2(d.Y, -d.X).Normalized();
                _walls[i] = new Plane(Lift(n, axis), Vec2.Dot(n, a));
            }
            return _walls;
        }
    }

    /// <summary>The flat coordinates that looking along an axis gives.</summary>
    private static Vec2 Flat(Vec3 p, Axis axis) => axis switch
    {
        Axis.X => new Vec2(p.Y, p.Z),
        Axis.Y => new Vec2(p.Z, p.X),
        _ => new Vec2(p.X, p.Y),
    };

    /// <summary>A flat direction back in 3D, with nothing along the axis.</summary>
    private static Vec3 Lift(Vec2 v, Axis axis) => axis switch
    {
        Axis.X => new Vec3(0, v.X, v.Y),
        Axis.Y => new Vec3(v.Y, 0, v.X),
        _ => new Vec3(v.X, v.Y, 0),
    };

    private static (double, double, double, double) Flat(List<Vec3> pts, Axis axis)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in pts)
        {
            var f = Flat(p, axis);
            minU = Math.Min(minU, f.X);
            maxU = Math.Max(maxU, f.X);
            minV = Math.Min(minV, f.Y);
            maxV = Math.Max(maxV, f.Y);
        }
        return (minU, maxU, minV, maxV);
    }

    /// <summary>
    /// The union of the operand's faces seen along the axis, as non-overlapping convex pieces (LibTess merges them;
    /// every face is taken anticlockwise so that front and back faces add up rather than cancel).
    /// </summary>
    private static List<Outline> Outlines(Mesh operand, Axis axis, double tol)
    {
        var tess = new Tess { NoEmptyPolygons = true };
        bool any = false;
        foreach (var poly in operand.Polygons)
        {
            if (poly.Count < 3) continue;
            var flat = poly.Points.Select(i => Flat(operand.Points[i], axis)).ToArray();
            double area = 0;
            for (int i = 0; i < flat.Length; i++) area += Vec2.Cross(flat[i], flat[(i + 1) % flat.Length]);
            if (Math.Abs(area) <= tol * tol) continue;
            tess.AddContour(flat.Select(f => new ContourVertex { Position = new TessVec3(f.X, f.Y, 0) }).ToArray(),
                ContourOrientation.CounterClockwise);
            any = true;
        }
        if (!any) return [];
        // LibTess can fail merging convex pieces straight from overlapping contours, so first take the union's
        // clean outline, then cut that into convex pieces.
        tess.Tessellate(WindingRule.NonZero, ElementType.BoundaryContours, 3, null, new TessVec3(0, 0, 1));
        var union = new Tess { NoEmptyPolygons = true };
        for (int c = 0; c < tess.ElementCount; c++)
        {
            int start = tess.Elements[c * 2], count = tess.Elements[c * 2 + 1];
            union.AddContour(tess.Vertices.Skip(start).Take(count).ToArray(), ContourOrientation.Original);
        }
        const int size = 64;
        union.Tessellate(WindingRule.NonZero, ElementType.Polygons, size, null, new TessVec3(0, 0, 1));
        tess = union;
        var result = new List<Outline>();
        for (int e = 0; e < tess.ElementCount; e++)
        {
            var pts = new List<Vec2>(size);
            for (int k = 0; k < size; k++)
            {
                int vi = tess.Elements[e * size + k];
                if (vi == Tess.Undef) break;
                pts.Add(new Vec2(tess.Vertices[vi].Position.X, tess.Vertices[vi].Position.Y));
            }
            double area = 0;
            for (int i = 0; i < pts.Count; i++) area += Vec2.Cross(pts[i], pts[(i + 1) % pts.Count]);
            if (pts.Count < 3 || Math.Abs(area) <= tol * tol) continue;
            if (area < 0) pts.Reverse();
            result.Add(new Outline([.. pts]));
        }
        return result;
    }
}
