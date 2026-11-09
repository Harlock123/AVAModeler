using AVAModeler.Core;
using Clipper2Lib;

namespace AVAModeler.Slicing;

/// <summary>
/// The 2D polygon work behind toolpaths, on Clipper2 (Angus Johnson's polygon clipping and offsetting library, Boost
/// licence): unions, differences and intersections of regions, offsetting them in or out, and clipping lines to them.
/// A region is a set of closed polygons in mm, outsides anticlockwise and holes clockwise, filled by the non-zero rule.
/// Clipper works to <see cref="Precision"/> decimal places (a tenth of a micron).
/// </summary>
public static class Polygons2D
{
    public const int Precision = 4;

    /// <summary>How an offset turns corners.</summary>
    public enum Corners
    {
        /// <summary>Sharp corners stay sharp (up to twice the offset out from the corner, then cut square).</summary>
        Sharp,
        /// <summary>Corners rounded off: for outward offsets such as a brim or skirt.</summary>
        Round,
    }

    public static PathD Path(IEnumerable<Vec2> points) => new(points.Select(p => new PointD(p.X, p.Y)));

    public static PathsD Paths(IEnumerable<IEnumerable<Vec2>> polygons) => new(polygons.Select(Path));

    public static List<Vec2> Points(PathD path) => path.Select(p => new Vec2(p.x, p.y)).ToList();

    /// <summary>An island's outline and holes as a region.</summary>
    public static PathsD Region(Island island) => Paths([island.Outline.Points, .. island.Holes.Select(h => h.Points)]);

    /// <summary>Islands as one region.</summary>
    public static PathsD Region(IEnumerable<Island> islands) => new(islands.SelectMany(Region));

    /// <summary>The area these polygons cover, filled by the non-zero rule: overlaps merged, holes cut out.</summary>
    public static PathsD Union(PathsD paths) => Clipper.Union(paths, new PathsD(), FillRule.NonZero, Precision);

    public static PathsD Union(PathsD a, PathsD b) => Clipper.Union(a, b, FillRule.NonZero, Precision);

    public static PathsD Difference(PathsD a, PathsD b) => Clipper.Difference(a, b, FillRule.NonZero, Precision);

    public static PathsD Intersect(PathsD a, PathsD b) => Clipper.Intersect(a, b, FillRule.NonZero, Precision);

    /// <summary>
    /// The region grown by <paramref name="delta"/> mm (shrunk when negative). Shrinking can split a region or make
    /// parts of it vanish where it's narrower than twice the offset.
    /// </summary>
    public static PathsD Offset(PathsD region, double delta, Corners corners = Corners.Sharp)
    {
        if (region.Count == 0) return [];
        var join = corners == Corners.Round ? JoinType.Round : JoinType.Miter;
        // Arc tolerance: corners within 5 microns of a true circle.
        return Clipper.InflatePaths(region, delta, join, EndType.Polygon, 2.0, Precision, 0.005);
    }

    /// <summary>The area covered (mm²), holes taken away.</summary>
    public static double Area(PathsD region) => Clipper.Area(region);

    /// <summary>
    /// The parts of open lines (polylines) inside a region, each still an open polyline. The pieces don't keep the
    /// lines' order or direction; callers order them.
    /// </summary>
    public static List<List<Vec2>> ClipLines(IEnumerable<IEnumerable<Vec2>> lines, PathsD region)
    {
        var clipper = new ClipperD(Precision);
        clipper.AddOpenSubject(Paths(lines));
        clipper.AddClip(region);
        var closed = new PathsD();
        var open = new PathsD();
        clipper.Execute(ClipType.Intersection, FillRule.NonZero, closed, open);
        return open.Select(Points).Where(p => p.Count >= 2).ToList();
    }

    /// <summary>
    /// A region as islands: each outside with the holes directly in it. A solid standing in a hole is an island of its
    /// own. Loops with less area than <paramref name="minArea"/> (mm²) are dropped, as specks the nozzle can't print.
    /// </summary>
    public static List<Island> Islands(PathsD region, double minArea = 1e-6)
    {
        var tree = new PolyTreeD();
        var clipper = new ClipperD(Precision);
        clipper.AddSubject(region);
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        var islands = new List<Island>();
        void Walk(PolyPathD outside)
        {
            var outline = Loop(outside.Polygon!, hole: false);
            var holes = new List<Loop>();
            for (int i = 0; i < outside.Count; i++)
            {
                var hole = outside[i];
                if (Math.Abs(Clipper.Area(hole.Polygon!)) >= minArea) holes.Add(Loop(hole.Polygon!, hole: true));
                for (int j = 0; j < hole.Count; j++) Walk(hole[j]);
            }
            if (Math.Abs(Clipper.Area(outside.Polygon!)) >= minArea) islands.Add(new Island(outline, holes));
        }
        for (int i = 0; i < tree.Count; i++) Walk(tree[i]);
        return islands.OrderByDescending(i => i.Area).ToList();
    }

    /// <summary>A Clipper path as a loop wound the slicer's way (anticlockwise outside, clockwise hole).</summary>
    private static Loop Loop(PathD path, bool hole)
    {
        var pts = Points(path);
        if (Geometry2D.SignedArea(pts) < 0 != hole) pts.Reverse();
        return new Loop(pts);
    }
}
