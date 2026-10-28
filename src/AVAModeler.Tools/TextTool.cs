using AVAModeler.Core;
using SkiaSharp;

namespace AVAModeler.Tools;

/// <summary>
/// Text: a line of type as flat polygons, one per letter outline, with each letter's holes (the middle of an O, an A)
/// joined to its outline by a narrow cut so every letter is a single polygon, as classic Modeler's text was.
/// </summary>
public sealed class TextTool : Tool
{
    public override string Name => "Text";
    public override string Tab => "Objects";
    public override string Summary => "Adds a line of text as flat polygons, one per letter (holes joined to the outline), in the current layer: Height is the font size in metres, the baseline starts at Position, facing along Axis (Z: readable in the Front view). Font is a font family name (blank: the default).";
    public override string Help => "Click where the text starts; type it in the numeric panel.";

    [Param("Text")] public string Text { get; set; } = "AVA";
    [Param("Font")] public string Font { get; set; } = "";
    [Param("Height", Distance = true, Min = 0, Description = "The font size.")] public double Height { get; set; } = 1;
    [Param("Position", Distance = true, Description = "Where the baseline starts.")] public Vec3 Position { get; set; }
    /// <summary>The axis the text faces along: Z puts it in the Front view.</summary>
    [Param("Axis", Description = "The axis the text faces along: Z reads in the Front view, Y in the Top view, X in the Side view.")] public Axis Axis { get; set; } = Axis.Z;
    [Param("Curve steps", Min = 1, Max = 32, Step = 1, Description = "Straight pieces per curve of a letter.")] public int CurveSteps { get; set; } = 6;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected override void OnDrag(DragInfo d)
    {
        Position = Context.SnapPoint(d.Current);
        if (d.First) Axis = d.ViewAxis;
    }

    protected override void Apply()
    {
        var mesh = Doc.Current.Mesh;
        int surface = SurfaceFor(Surface);
        foreach (var letter in Outlines(Text, Font, CurveSteps))
        {
            var ring = Keyhole(letter);
            if (ring.Count < 3) continue;
            var pts = ring.Select(p => mesh.AddPoint(Place(p))).ToList();
            int f = mesh.AddPolygon(pts, surface);
            Primitives.FaceTowardViewer(mesh, [f], Axis);
        }
    }

    /// <summary>A point of the type (em units, y up) to the world: scaled to the height, laid in the plane across the axis.</summary>
    private Vec3 Place(Vec2 p)
    {
        double x = p.X * Height, y = p.Y * Height;
        return Position + Axis switch
        {
            // Laid out to read left to right in the view that looks down the axis.
            Axis.Z => new Vec3(x, y, 0),
            Axis.X => new Vec3(0, y, x),
            _ => new Vec3(x, 0, y),
        };
    }

    /// <summary>
    /// The text's outlines, grouped per letter: each group is an outer contour followed by its holes, in em units
    /// (1 = the font size) with y up.
    /// </summary>
    public static List<List<List<Vec2>>> Outlines(string text, string fontFamily, int curveSteps)
    {
        using var typeface = (string.IsNullOrWhiteSpace(fontFamily) ? null : SKTypeface.FromFamilyName(fontFamily)) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 100);
        using var path = font.GetTextPath(text, new SKPoint(0, 0));
        var contours = Flatten(path, curveSteps).Select(c => c.Select(p => new Vec2(p.X / 100, -p.Y / 100)).ToList()).Where(c => c.Count >= 3).ToList();
        // The outer contours are those not inside any other; each hole goes with the smallest outer that holds it.
        var outers = contours.Where(c => !contours.Any(o => o != c && Contains(o, c[0]))).ToList();
        var groups = outers.Select(o => new List<List<Vec2>> { Wind(o, clockwise: true) }).ToList();
        foreach (var c in contours.Except(outers))
        {
            int best = -1;
            double bestArea = double.PositiveInfinity;
            for (int i = 0; i < outers.Count; i++)
                if (Contains(outers[i], c[0]) && Math.Abs(Area(outers[i])) < bestArea) { best = i; bestArea = Math.Abs(Area(outers[i])); }
            if (best >= 0) groups[best].Add(Wind(c, clockwise: false));
        }
        return groups;
    }

    private static List<List<SKPoint>> Flatten(SKPath path, int steps)
    {
        var result = new List<List<SKPoint>>();
        List<SKPoint>? current = null;
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (current is { Count: > 2 }) result.Add(current);
                    current = [pts[0]];
                    break;
                case SKPathVerb.Line:
                    current?.Add(pts[1]);
                    break;
                case SKPathVerb.Quad:
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = (float)i / steps, u = 1 - t;
                        current?.Add(new SKPoint(u * u * pts[0].X + 2 * u * t * pts[1].X + t * t * pts[2].X, u * u * pts[0].Y + 2 * u * t * pts[1].Y + t * t * pts[2].Y));
                    }
                    break;
                case SKPathVerb.Conic:
                    float w = it.ConicWeight();
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = (float)i / steps, u = 1 - t;
                        float d = u * u + 2 * w * u * t + t * t;
                        current?.Add(new SKPoint((u * u * pts[0].X + 2 * w * u * t * pts[1].X + t * t * pts[2].X) / d, (u * u * pts[0].Y + 2 * w * u * t * pts[1].Y + t * t * pts[2].Y) / d));
                    }
                    break;
                case SKPathVerb.Cubic:
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = (float)i / steps, u = 1 - t;
                        current?.Add(new SKPoint(
                            u * u * u * pts[0].X + 3 * u * u * t * pts[1].X + 3 * u * t * t * pts[2].X + t * t * t * pts[3].X,
                            u * u * u * pts[0].Y + 3 * u * u * t * pts[1].Y + 3 * u * t * t * pts[2].Y + t * t * t * pts[3].Y));
                    }
                    break;
                case SKPathVerb.Close:
                    if (current is { Count: > 2 }) result.Add(current);
                    current = null;
                    break;
            }
        }
        if (current is { Count: > 2 }) result.Add(current);
        // Drop a closing point that repeats the first.
        foreach (var c in result)
            while (c.Count > 1 && Math.Abs(c[0].X - c[^1].X) < 1e-4 && Math.Abs(c[0].Y - c[^1].Y) < 1e-4) c.RemoveAt(c.Count - 1);
        return result;
    }

    private static double Area(List<Vec2> c)
    {
        double a = 0;
        for (int i = 0; i < c.Count; i++) a += Vec2.Cross(c[i], c[(i + 1) % c.Count]);
        return a / 2;
    }

    private static List<Vec2> Wind(List<Vec2> c, bool clockwise)
    {
        bool isClockwise = Area(c) < 0;
        if (isClockwise == clockwise) return c;
        var r = new List<Vec2>(c);
        r.Reverse();
        return r;
    }

    private static bool Contains(List<Vec2> poly, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// One ring from an outline and its holes: each hole (rightmost first) is joined to the outline at the nearest
    /// point it can see, by a cut walked in and back out.
    /// </summary>
    public static List<Vec2> Keyhole(List<List<Vec2>> group)
    {
        var ring = new List<Vec2>(group[0]);
        foreach (var hole in group.Skip(1).OrderByDescending(h => h.Max(p => p.X)))
        {
            int hi = 0;
            for (int i = 1; i < hole.Count; i++) if (hole[i].X > hole[hi].X) hi = i;
            var m = hole[hi];
            int best = -1;
            double bestD = double.PositiveInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                double d = (ring[i] - m).LengthSquared;
                if (d >= bestD || !Visible(m, ring[i], ring, group.Skip(1))) continue;
                bestD = d;
                best = i;
            }
            if (best < 0) best = ring.Select((p, i) => (d: (p - m).LengthSquared, i)).MinBy(x => x.d).i;
            var joined = new List<Vec2>(ring.Count + hole.Count + 2);
            joined.AddRange(ring.Take(best + 1));
            for (int k = 0; k <= hole.Count; k++) joined.Add(hole[(hi + k) % hole.Count]);
            joined.Add(ring[best]);
            joined.AddRange(ring.Skip(best + 1));
            ring = joined;
        }
        return ring;
    }

    private static bool Visible(Vec2 a, Vec2 b, List<Vec2> ring, IEnumerable<List<Vec2>> holes)
    {
        bool Crosses(List<Vec2> poly)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                Vec2 p = poly[i], q = poly[(i + 1) % poly.Count];
                if (Near(p, a) || Near(q, a) || Near(p, b) || Near(q, b)) continue;
                if (SegmentsCross(a, b, p, q)) return true;
            }
            return false;
        }
        return !Crosses(ring) && !holes.Any(Crosses);
    }

    private static bool Near(Vec2 a, Vec2 b) => (a - b).LengthSquared < 1e-12;

    private static bool SegmentsCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double d1 = Vec2.Cross(b - a, c - a), d2 = Vec2.Cross(b - a, d - a), d3 = Vec2.Cross(d - c, a - c), d4 = Vec2.Cross(d - c, b - c);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }
}
