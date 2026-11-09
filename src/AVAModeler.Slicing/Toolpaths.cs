using AVAModeler.Core;
using Clipper2Lib;

namespace AVAModeler.Slicing;

/// <summary>What a printed line is part of: it decides the speed, and G-code previewers colour by it.</summary>
public enum PathKind { OuterWall, InnerWall, Skin, Infill, Support, Skirt, Brim }

/// <summary>One printed line: a polyline the nozzle extrudes along, closed (back to its start) or open.</summary>
public sealed record Extrusion(PathKind Kind, IReadOnlyList<Vec2> Points, bool Closed)
{
    public double Length => Geometry2D.Perimeter(Points, Closed);
}

/// <summary>A layer's lines in the order they're printed.</summary>
public sealed record ToolpathLayer(SliceLayer Layer, IReadOnlyList<Extrusion> Paths);

/// <summary>The whole print as toolpaths, layer by layer, ready for the G-code writer.</summary>
public sealed record PrintPlan(SlicedModel Sliced, PrinterProfile Printer, SliceSettings Settings, IReadOnlyList<ToolpathLayer> Layers)
{
    /// <summary>The length of line printed of each kind (mm), over the whole print.</summary>
    public IReadOnlyDictionary<PathKind, double> Lengths =>
        Layers.SelectMany(l => l.Paths).GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Sum(p => p.Length));
}

/// <summary>
/// Turns sliced layers into toolpaths. Per layer: walls are the region shrunk by half a line, then a line more for each
/// further loop; the inside left within the walls (reaching <see cref="SliceSettings.InfillOverlap"/> into them) is
/// solid skin where the layers above or below don't cover it (top and bottom surfaces, within that many layers) and
/// sparse infill elsewhere. Lines are laid on one grid for the whole print, so each layer's sit on the one below.
/// Supports fill, a layer below an overhang down to the bed or the object, what the layer below doesn't hold up,
/// kept <see cref="SliceSettings.SupportGap"/> clear of the object. The first layer gets a skirt or a brim. Each
/// island is printed in turn (nearest first): inner walls, the outer wall, skin, infill.
/// </summary>
public static class Planner
{
    public static PrintPlan Plan(SlicedModel sliced, PrinterProfile printer)
    {
        var s = sliced.Settings.Checked(printer);
        double w = s.LineWidth;
        var layers = sliced.Layers;
        int n = layers.Count;

        var region = layers.Select(l => Polygons2D.Region(l.Islands)).ToArray();
        // The inside each layer leaves for skin and infill: within the walls, by a half line for the lines' own width.
        double inset = s.Walls * w + w / 2 - s.InfillOverlap * w;
        var inside = region.Select(r => Polygons2D.Offset(r, -inset)).ToArray();
        // Solid where a surface is near: the inside not covered by every one of the layers above (or below) it.
        var solid = new PathsD[n];
        var sparse = new PathsD[n];
        for (int i = 0; i < n; i++)
        {
            var above = Covered(region, i + 1, i + s.TopLayers, n);
            var below = Covered(region, i - s.BottomLayers, i - 1, n);
            solid[i] = new PathsD();
            if (above != null) solid[i] = Polygons2D.Difference(inside[i], above);
            if (below != null) solid[i] = Polygons2D.Union(solid[i], Polygons2D.Difference(inside[i], below));
            sparse[i] = Polygons2D.Difference(inside[i], solid[i]);
        }
        var support = s.Supports ? SupportRegions(region, layers, s) : new PathsD[n];

        var result = new List<ToolpathLayer>(n);
        var at = new Vec2(0, 0);
        for (int i = 0; i < n; i++)
        {
            var paths = new List<Extrusion>();
            void Add(Extrusion e)
            {
                var placed = e.Closed ? Seam(e, s.Seam, at) : e;
                paths.Add(placed);
                at = placed.Closed ? placed.Points[0] : placed.Points[^1];
            }
            if (i == 0) foreach (var e in Order(FirstLayerExtras(Polygons2D.Paths(layers[0].Islands.Select(isl => isl.Outline.Points)), support[0], s), at)) Add(e);
            if (support[i] is { Count: > 0 } sup)
                foreach (var e in Order(Fill(sup, w * 100 / s.SupportPercent, [0], PathKind.Support), at)) Add(e);

            var islands = layers[i].Islands.ToList();
            while (islands.Count > 0)
            {
                var here = at;
                var island = islands.MinBy(isl => isl.Outline.Points.Min(p => (p - here).LengthSquared))!;
                islands.Remove(island);
                var own = Polygons2D.Region(island);
                foreach (var e in Walls(own, s)) Add(e);
                double turn = i % 2 == 0 ? 0 : 90;
                var mySolid = Polygons2D.Intersect(solid[i], own);
                foreach (var e in Order(Fill(mySolid, w, [s.InfillAngle + turn], PathKind.Skin), at)) Add(e);
                if (s.InfillPercent > 0)
                {
                    var mySparse = Polygons2D.Intersect(sparse[i], own);
                    var (spacing, angles) = s.Infill == InfillPattern.Grid
                        ? (2 * w * 100 / s.InfillPercent, new[] { s.InfillAngle, s.InfillAngle + 90 })
                        : (w * 100 / s.InfillPercent, new[] { s.InfillAngle + turn });
                    foreach (var e in Order(Fill(mySparse, spacing, angles, PathKind.Infill), at)) Add(e);
                }
            }
            result.Add(new ToolpathLayer(layers[i], paths));
        }
        return new PrintPlan(sliced, printer, s, result);
    }

    /// <summary>
    /// The area every layer from <paramref name="from"/> to <paramref name="to"/> covers: none past the print's ends (so
    /// the layers near them are solid), or null when the range is empty (no solid layers asked for on that side).
    /// </summary>
    private static PathsD? Covered(PathsD[] region, int from, int to, int n)
    {
        if (to < from) return null;
        if (from < 0 || to >= n) return [];
        var c = region[from];
        for (int j = from + 1; j <= to && c.Count > 0; j++) c = Polygons2D.Intersect(c, region[j]);
        return c;
    }

    /// <summary>The walls of one island, innermost first and the outer wall last, so the outside is laid against something.</summary>
    private static IEnumerable<Extrusion> Walls(PathsD own, SliceSettings s)
    {
        var loops = new List<Extrusion>();
        for (int k = 0; k < s.Walls; k++)
        {
            var ring = Polygons2D.Offset(own, -(s.LineWidth / 2 + k * s.LineWidth));
            if (ring.Count == 0) break;
            var kind = k == 0 ? PathKind.OuterWall : PathKind.InnerWall;
            loops.AddRange(ring.Select(p => new Extrusion(kind, Polygons2D.Points(p), true)));
        }
        loops.Reverse();
        return loops;
    }

    /// <summary>The brim's lines round the first layer's outsides (not in its holes), innermost first, or else the skirt's loops a gap away from it and its support.</summary>
    private static IEnumerable<Extrusion> FirstLayerExtras(PathsD first, PathsD? support, SliceSettings s)
    {
        double w = s.LineWidth;
        if (s.BrimLines > 0)
        {
            for (int k = 0; k < s.BrimLines; k++)
                foreach (var p in Polygons2D.Offset(first, w / 2 + k * w, Polygons2D.Corners.Round))
                    if (Clipper.Area(p) > 0) yield return new Extrusion(PathKind.Brim, Polygons2D.Points(p), true);
            yield break;
        }
        var footprint = support is { Count: > 0 } ? Polygons2D.Union(first, support) : first;
        for (int k = 0; k < s.SkirtLoops; k++)
            foreach (var p in Polygons2D.Offset(footprint, s.SkirtDistance + w / 2 + k * w, Polygons2D.Corners.Round))
                if (Clipper.Area(p) > 0) yield return new Extrusion(PathKind.Skirt, Polygons2D.Points(p), true);
    }

    /// <summary>
    /// Parallel lines <paramref name="spacing"/> apart at each angle, on a grid fixed to the bed's origin, clipped to the
    /// region (which is already where the lines' middles may go).
    /// </summary>
    internal static List<Extrusion> Fill(PathsD region, double spacing, double[] angles, PathKind kind)
    {
        var lines = new List<Extrusion>();
        if (region.Count == 0) return lines;
        var all = region.SelectMany(p => p).ToList();
        foreach (double degrees in angles)
        {
            double a = degrees * Math.PI / 180;
            var d = new Vec2(Math.Cos(a), Math.Sin(a));
            var nrm = new Vec2(-d.Y, d.X);
            double lo = all.Min(p => p.x * nrm.X + p.y * nrm.Y), hi = all.Max(p => p.x * nrm.X + p.y * nrm.Y);
            double t0 = all.Min(p => p.x * d.X + p.y * d.Y) - 1, t1 = all.Max(p => p.x * d.X + p.y * d.Y) + 1;
            var raw = new List<List<Vec2>>();
            for (long k = (long)Math.Floor(lo / spacing); k * spacing <= hi; k++)
            {
                // Half a spacing off the origin, so a line never runs exactly along a wall at a round number.
                double o = (k + 0.5) * spacing;
                if (o < lo || o > hi) continue;
                raw.Add([nrm * o + d * t0, nrm * o + d * t1]);
            }
            foreach (var piece in Polygons2D.ClipLines(raw, region))
                if (Geometry2D.Perimeter(piece, false) > 1e-3) lines.Add(new Extrusion(kind, piece, false));
        }
        return lines;
    }

    /// <summary>Support for each layer, worked down from the top (see <see cref="Planner"/>).</summary>
    private static PathsD[] SupportRegions(PathsD[] region, IReadOnlyList<SliceLayer> layers, SliceSettings s)
    {
        int n = region.Length;
        var support = new PathsD[n];
        for (int i = 0; i < n; i++) support[i] = [];
        // What each layer leaves hanging: beyond the layer below, grown by how far a slope at the support angle reaches in one layer.
        var overhang = new PathsD[n];
        overhang[0] = [];
        for (int i = 1; i < n; i++)
        {
            double reach = layers[i].Height * Math.Tan(s.SupportAngle * Math.PI / 180);
            overhang[i] = Polygons2D.Difference(region[i], Polygons2D.Offset(region[i - 1], reach));
            // Slivers thinner than a line aren't worth holding up.
            overhang[i] = Polygons2D.Offset(Polygons2D.Offset(overhang[i], -s.LineWidth / 2), s.LineWidth / 2);
        }
        var carried = new PathsD();
        for (int i = n - 2; i >= 0; i--)
        {
            // One layer of air under the overhang, so the support comes away.
            var wanted = i + 2 < n ? Polygons2D.Union(carried, overhang[i + 2]) : carried;
            carried = Polygons2D.Difference(wanted, Polygons2D.Offset(region[i], s.SupportGap));
            // Support meeting the object stops there, standing on it.
            support[i] = carried;
        }
        return support;
    }

    /// <summary>Lines in a short order: from the nozzle, each next one the nearest end of what's left (and run from that end).</summary>
    public static List<Extrusion> Order(IEnumerable<Extrusion> items, Vec2 from)
    {
        var left = items.ToList();
        var ordered = new List<Extrusion>(left.Count);
        var at = from;
        while (left.Count > 0)
        {
            int best = -1;
            bool flip = false;
            double bestD = double.PositiveInfinity;
            for (int i = 0; i < left.Count; i++)
            {
                var e = left[i];
                double d0 = (e.Points[0] - at).LengthSquared;
                if (d0 < bestD) { bestD = d0; best = i; flip = false; }
                if (e.Closed) continue;
                double d1 = (e.Points[^1] - at).LengthSquared;
                if (d1 < bestD) { bestD = d1; best = i; flip = true; }
            }
            var pick = left[best];
            left.RemoveAt(best);
            if (flip) pick = pick with { Points = pick.Points.Reverse().ToList() };
            ordered.Add(pick);
            at = pick.Closed ? pick.Points[0] : pick.Points[^1];
        }
        return ordered;
    }

    /// <summary>A closed loop turned to start at its seam: its rearmost point, or its point nearest the nozzle.</summary>
    public static Extrusion Seam(Extrusion loop, SeamPosition seam, Vec2 nozzle)
    {
        var pts = loop.Points;
        int start = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            bool better = seam == SeamPosition.Rear
                ? pts[i].Y > pts[start].Y + 1e-9 || Math.Abs(pts[i].Y - pts[start].Y) <= 1e-9 && pts[i].X < pts[start].X
                : (pts[i] - nozzle).LengthSquared < (pts[start] - nozzle).LengthSquared;
            if (better) start = i;
        }
        if (start == 0) return loop;
        return loop with { Points = [.. pts.Skip(start), .. pts.Take(start)] };
    }
}
