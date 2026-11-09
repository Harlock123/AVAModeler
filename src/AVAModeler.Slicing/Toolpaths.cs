using AVAModeler.Core;
using Clipper2Lib;

namespace AVAModeler.Slicing;

/// <summary>What a printed line is part of: it decides the speed, and G-code previewers colour by it.</summary>
public enum PathKind { OuterWall, InnerWall, Skin, Infill, Support, Skirt, Brim, GapFill, Bridge, SupportInterface, Ironing, PrimeTower }

/// <summary>
/// One printed line: a polyline the nozzle extrudes along, closed (back to its start) or open. <see cref="Width"/> is
/// its own width when it isn't the print's line width (gap fill is narrower), else 0. <see cref="Flow"/> scales the
/// plastic it lays (ironing lays a trickle).
/// </summary>
public sealed record Extrusion(PathKind Kind, IReadOnlyList<Vec2> Points, bool Closed, double Width = 0, double Flow = 1)
{
    public double Length => Geometry2D.Perimeter(Points, Closed);
    /// <summary>The extruder that prints it when its kind doesn't say (the prime tower's lines: each extruder primes its own); -1 by kind.</summary>
    public int Tool { get; init; } = -1;
}

/// <summary>
/// A layer's lines in the order they're printed, and where travel can stay inside the object (<see cref="Comb"/>: the
/// layer's islands shrunk a line in from their walls), so the nozzle needn't cross a wall or retract.
/// </summary>
public sealed record ToolpathLayer(SliceLayer Layer, IReadOnlyList<Extrusion> Paths)
{
    public IReadOnlyList<Island> Comb { get; init; } = [];
}

/// <summary>The whole print as toolpaths, layer by layer, ready for the G-code writer.</summary>
public sealed record PrintPlan(SlicedModel Sliced, PrinterProfile Printer, SliceSettings Settings, IReadOnlyList<ToolpathLayer> Layers)
{
    /// <summary>What the person should know about the plan (no room on the bed for the prime tower).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>Where the prime tower stands (mm on the bed), if there is one.</summary>
    public (Vec2 Min, Vec2 Max)? PrimeTower { get; init; }

    /// <summary>The length of line printed of each kind (mm), over the whole print.</summary>
    public IReadOnlyDictionary<PathKind, double> Lengths =>
        Layers.SelectMany(l => l.Paths).GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Sum(p => p.Length));
}

/// <summary>
/// Turns sliced layers into toolpaths. Per layer: walls are the region shrunk by half a line, then a
/// <see cref="Spacing"/> more for each further loop; the inside left within the walls (reaching <see cref="SliceSettings.InfillOverlap"/> into them) is
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
        // Lines side by side sit a spacing apart, a little under their width, so their rounded sides meet.
        var spacing = layers.Select(l => Spacing(w, l.Height)).ToArray();
        // The inside each layer leaves for skin and infill: where the middles of lines next to the innermost wall go,
        // reaching InfillOverlap into it.
        var inside = region.Select((r, i) => Polygons2D.Offset(r, -(w / 2 + s.Walls * spacing[i] - s.InfillOverlap * w))).ToArray();
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

        // A prime tower when supports print on the other extruder: up to the last layer that changes extruder.
        int modelTool = s.ModelExtruder, supportTool = s.SupportExtruder;
        int lastChange = -1;
        if (s.PrimeTower && supportTool >= 0 && supportTool != modelTool)
            for (int i = 0; i < n; i++)
                if (support[i] is { Count: > 0 }) lastChange = i;
        var problems = new List<string>();
        PathsD? tower = null;
        (Vec2, Vec2)? towerBox = null;
        if (lastChange >= 0)
        {
            towerBox = TowerPlace(sliced.Model.Bounds, printer, s.PrimeTowerSize);
            if (towerBox is var (tmin, tmax))
                tower = Polygons2D.Paths([[tmin, new Vec2(tmax.X, tmin.Y), tmax, new Vec2(tmin.X, tmax.Y)]]);
            else
            {
                problems.Add($"There's no room on the bed for the {PrinterProfile.Num(s.PrimeTowerSize)} mm prime tower beside the part: the idle extruder may drip on it. Make the tower smaller, or move the part.");
                lastChange = -1;
            }
        }

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
            if (i == 0)
            {
                var footprint = Polygons2D.Paths(layers[0].Islands.Select(isl => isl.Outline.Points));
                if (tower != null && lastChange >= 0) footprint = Polygons2D.Union(footprint, tower);
                foreach (var e in Order(FirstLayerExtras(footprint, support[0], s, spacing[0]), at)) Add(e);
            }
            // The tower's lines this layer, crossways each layer: half for each extruder to prime on, or (on a layer
            // with no change) just its outline, to keep it standing.
            List<Extrusion> towerA = [], towerB = [];
            if (tower != null && i <= lastChange)
            {
                var inner = Polygons2D.Offset(tower, -w / 2);
                var lines = Fill(inner, spacing[i], [i % 2 == 0 ? 45 : 135], PathKind.PrimeTower);
                var outline = inner.Select(p => new Extrusion(PathKind.PrimeTower, Polygons2D.Points(p), true)).ToList();
                if (support[i] is { Count: > 0 })
                {
                    towerA = lines.Where((_, k) => k % 2 == 0).Select(e => e with { Tool = supportTool }).ToList();
                    towerB = [.. outline.Select(e => e with { Tool = modelTool }), .. lines.Where((_, k) => k % 2 == 1).Select(e => e with { Tool = modelTool })];
                }
                else towerB = outline.Select(e => e with { Tool = modelTool }).ToList();
            }
            foreach (var e in Order(towerA, at)) Add(e);
            if (support[i] is { Count: > 0 } sup)
            {
                // The top few layers of each column (none of the support above covers them) are interface: dense,
                // crossways to the rest.
                var top = s.SupportInterfaceLayers > 0 ? Covered(support, i + 1, i + s.SupportInterfaceLayers, n) : null;
                var face = top == null ? new PathsD() : Polygons2D.Difference(sup, top);
                var body = face.Count > 0 ? Polygons2D.Difference(sup, face) : sup;
                foreach (var e in Order(Fill(body, spacing[i] * 100 / s.SupportPercent, [0], PathKind.Support), at)) Add(e);
                if (face.Count > 0)
                    foreach (var e in Order(Fill(face, spacing[i] * 100 / s.SupportInterfacePercent, [90], PathKind.SupportInterface), at)) Add(e);
            }

            foreach (var e in Order(towerB, at)) Add(e);
            var islands = layers[i].Islands.ToList();
            while (islands.Count > 0)
            {
                var here = at;
                var island = islands.MinBy(isl => isl.Outline.Points.Min(p => (p - here).LengthSquared))!;
                islands.Remove(island);
                var own = Polygons2D.Region(island);
                var walls = Walls(own, s, spacing[i]).ToList();
                foreach (var e in walls) Add(e);
                if (s.GapFill)
                    foreach (var e in Order(GapFill(own, walls, Polygons2D.Intersect(inside[i], own), s, spacing[i]), at)) Add(e);
                double turn = i % 2 == 0 ? 0 : 90;
                var mySolid = Polygons2D.Intersect(solid[i], own);
                if (s.Bridging && i > 0 && mySolid.Count > 0)
                {
                    var (bridges, rest) = Bridges(mySolid, region[i - 1], inside[i], s, spacing[i]);
                    foreach (var e in Order(bridges, at)) Add(e);
                    mySolid = rest;
                }
                foreach (var e in Order(Fill(mySolid, spacing[i], [s.InfillAngle + turn], PathKind.Skin), at)) Add(e);
                if (s.Ironing)
                {
                    // The tops of things: this layer's solid not covered by the next, ironed crossways to its skin.
                    var top = i + 1 < n ? Polygons2D.Difference(own, region[i + 1]) : own;
                    // Within the walls; strips thinner than a line (the edge of a slope) aren't worth ironing.
                    var iron = Polygons2D.Intersect(inside[i], top);
                    iron = Polygons2D.Offset(Polygons2D.Offset(iron, -w / 2), w / 4);
                    if (iron.Count > 0)
                        foreach (var e in Order(Fill(iron, s.IroningSpacing, [s.InfillAngle + turn + 90], PathKind.Ironing), at))
                            Add(e with { Flow = s.IroningFlowPercent / 100 });
                }
                if (s.InfillPercent > 0)
                {
                    var mySparse = Polygons2D.Intersect(sparse[i], own);
                    if (s.Infill == InfillPattern.Gyroid)
                        foreach (var e in Order(Gyroid(mySparse, spacing[i] * 100 / s.InfillPercent, layers[i].CutAt), at)) Add(e);
                    else
                    {
                        var (apart, angles) = s.Infill == InfillPattern.Grid
                            ? (2 * spacing[i] * 100 / s.InfillPercent, new[] { s.InfillAngle, s.InfillAngle + 90 })
                            : (spacing[i] * 100 / s.InfillPercent, new[] { s.InfillAngle + turn });
                        foreach (var e in Order(Fill(mySparse, apart, angles, PathKind.Infill), at)) Add(e);
                    }
                }
            }
            var comb = s.Combing ? Polygons2D.Islands(Polygons2D.Offset(region[i], -w)) : [];
            result.Add(new ToolpathLayer(layers[i], paths) { Comb = comb });
        }
        return new PrintPlan(sliced, printer, s, result) { Problems = problems, PrimeTower = lastChange >= 0 ? towerBox : null };
    }

    /// <summary>
    /// Where the prime tower goes: a square of <paramref name="size"/> mm, 5 mm to the right of the part, else its left,
    /// else in front or behind it, centred on it the other way, and kept 2 mm in from the bed's edge; null if none fit.
    /// </summary>
    internal static (Vec2 Min, Vec2 Max)? TowerPlace(Box3 part, PrinterProfile printer, double size)
    {
        const double gap = 5, edge = 2;
        double cx = (part.Min.X + part.Max.X) / 2, cy = (part.Min.Y + part.Max.Y) / 2;
        (Vec2, Vec2)[] tries =
        [
            (new Vec2(part.Max.X + gap, cy - size / 2), new Vec2(part.Max.X + gap + size, cy + size / 2)),
            (new Vec2(part.Min.X - gap - size, cy - size / 2), new Vec2(part.Min.X - gap, cy + size / 2)),
            (new Vec2(cx - size / 2, part.Min.Y - gap - size), new Vec2(cx + size / 2, part.Min.Y - gap)),
            (new Vec2(cx - size / 2, part.Max.Y + gap), new Vec2(cx + size / 2, part.Max.Y + gap + size)),
        ];
        foreach (var (min, max) in tries)
        {
            bool inside = printer.RoundBed
                ? new[] { min, max, new Vec2(min.X, max.Y), new Vec2(max.X, min.Y) }.All(c => (c - new Vec2(printer.BedWidth / 2, printer.BedDepth / 2)).Length <= printer.BedWidth / 2 - edge)
                : min.X >= edge && min.Y >= edge && max.X <= printer.BedWidth - edge && max.Y <= printer.BedDepth - edge;
            if (inside) return (min, max);
        }
        return null;
    }

    /// <summary>
    /// How far apart the middles of two lines side by side are: a squashed line's sides are rounded, so for its
    /// cross-section, (w − h)·h + π(h/2)², to fill the space between them they sit w − h(1 − π/4) apart.
    /// </summary>
    public static double Spacing(double width, double height) => width - height * (1 - Math.PI / 4);

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
    private static IEnumerable<Extrusion> Walls(PathsD own, SliceSettings s, double spacing)
    {
        var loops = new List<Extrusion>();
        for (int k = 0; k < s.Walls; k++)
        {
            // Only where the loop has room (at least half a line across): a loop squeezed thinner overlaps itself, and
            // gap fill takes that space instead.
            double d = s.LineWidth / 2 + k * spacing, slack = s.LineWidth / 4;
            var ring = Polygons2D.Offset(Polygons2D.Offset(own, -(d + slack)), slack);
            if (ring.Count == 0) break;
            var kind = k == 0 ? PathKind.OuterWall : PathKind.InnerWall;
            loops.AddRange(ring.Select(p => new Extrusion(kind, Polygons2D.Points(p), true)));
        }
        loops.Reverse();
        return loops;
    }

    /// <summary>
    /// Gap fill: the parts of an island that are wide enough to print (at least a third of a line) but that neither the
    /// walls nor the inside reach, such as the strip left between walls when there's no room for another loop, or a
    /// fin too thin for any wall. Each gap is cut into pieces of a few millimetres so each is nearly straight, and a
    /// piece gets one or more lines along its length as wide as it needs (down to a third of a line).
    /// </summary>
    internal static List<Extrusion> GapFill(PathsD own, List<Extrusion> walls, PathsD inside, SliceSettings s, double spacing)
    {
        double w = s.LineWidth, least = w / 3;
        var printable = Polygons2D.Offset(Polygons2D.Offset(own, -least / 2), least / 2);
        if (printable.Count == 0) return [];
        var covered = Polygons2D.Offset(inside, w / 2);
        if (walls.Count > 0) covered = Polygons2D.Union(covered, Polygons2D.Band(walls.Select(e => e.Points), w / 2));
        var gaps = Polygons2D.Difference(printable, covered);
        // Only what a line a third wide fits in (gaps thinner than that are left: the lines beside them spread into them).
        gaps = Polygons2D.Offset(Polygons2D.Offset(gaps, -least / 2), least / 2);
        var lines = new List<Extrusion>();
        if (gaps.Count == 0) return lines;
        const double cell = 3;
        var pts = gaps.SelectMany(p => p).ToList();
        double x0 = Math.Floor(pts.Min(p => p.x) / cell) * cell, y0 = Math.Floor(pts.Min(p => p.y) / cell) * cell;
        double x1 = pts.Max(p => p.x), y1 = pts.Max(p => p.y);
        for (double x = x0; x < x1; x += cell)
            for (double y = y0; y < y1; y += cell)
            {
                var box = Polygons2D.Paths([[new Vec2(x, y), new Vec2(x + cell, y), new Vec2(x + cell, y + cell), new Vec2(x, y + cell)]]);
                foreach (var piece in Polygons2D.Islands(Polygons2D.Intersect(gaps, box), 1e-4))
                    lines.AddRange(LinesAlong(piece, s, spacing, least));
            }
        return lines;
    }

    /// <summary>Lines along a nearly straight piece of a gap: along its long axis, as many as its width needs, each as wide as its share.</summary>
    private static IEnumerable<Extrusion> LinesAlong(Island piece, SliceSettings s, double spacing, double least)
    {
        var pts = piece.Outline.Points;
        // The long axis from the outline's second moments about its middle.
        var c = new Vec2(pts.Average(p => p.X), pts.Average(p => p.Y));
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            var d = p - c;
            sxx += d.X * d.X; syy += d.Y * d.Y; sxy += d.X * d.Y;
        }
        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var along = new Vec2(Math.Cos(angle), Math.Sin(angle));
        var across = new Vec2(-along.Y, along.X);
        double lo = pts.Min(p => Vec2.Dot(p - c, across)), hi = pts.Max(p => Vec2.Dot(p - c, across));
        double t0 = pts.Min(p => Vec2.Dot(p - c, along)) - 1, t1 = pts.Max(p => Vec2.Dot(p - c, along)) + 1;
        double width = hi - lo;
        if (width < least) yield break;
        int count = Math.Max(1, (int)Math.Round(width / spacing));
        double each = Math.Clamp(width / count, least, s.LineWidth);
        var raw = Enumerable.Range(0, count).Select(k =>
        {
            double o = lo + (k + 0.5) * width / count;
            return new List<Vec2> { c + across * o + along * t0, c + across * o + along * t1 };
        });
        foreach (var line in Polygons2D.ClipLines(raw, Polygons2D.Region(piece)))
            if (Geometry2D.Perimeter(line, false) >= least) yield return new Extrusion(PathKind.GapFill, line, false, each);
    }

    /// <summary>
    /// Bridges: the solid parts of a layer with nothing under them (beyond the layer below by more than a line). They're
    /// printed as straight lines over the air at the angle (tried every 15°) whose lines land on what's below at both
    /// ends for the most of their length, run on into the supported solid by two lines to anchor. Returns the bridge
    /// lines and the solid left for ordinary skin.
    /// </summary>
    internal static (List<Extrusion> Lines, PathsD Left) Bridges(PathsD solid, PathsD below, PathsD inside, SliceSettings s, double spacing)
    {
        double w = s.LineWidth;
        var air = Polygons2D.Difference(solid, Polygons2D.Offset(below, w));
        // Specks of air are just the edge of a slope: not bridges.
        air = Polygons2D.Offset(Polygons2D.Offset(air, -w), w);
        if (air.Count == 0 || Polygons2D.Area(air) < 4 * w * w) return ([], solid);
        var area = Polygons2D.Intersect(Polygons2D.Offset(air, 2 * w), Polygons2D.Union(solid, Polygons2D.Intersect(inside, below)));
        var landing = Polygons2D.Offset(below, -w / 2);
        List<Extrusion>? best = null;
        double bestScore = -1;
        for (int a = 0; a < 180; a += 15)
        {
            var lines = Fill(area, spacing, [a], PathKind.Bridge);
            double anchored = 0, total = 0;
            foreach (var e in lines)
            {
                total += e.Length;
                if (InsideAny(landing, e.Points[0]) && InsideAny(landing, e.Points[^1])) anchored += e.Length;
            }
            // Most of the length anchored at both ends; shorter spans break ties.
            double score = total > 0 ? anchored / total - 1e-4 * total / Math.Max(1, lines.Count) : 0;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                best = lines;
            }
        }
        return (best ?? [], Polygons2D.Difference(solid, area));
    }

    private static bool InsideAny(PathsD region, Vec2 p)
    {
        // Non-zero winding over every path: inside an outside and not in a hole.
        int winding = 0;
        foreach (var path in region)
        {
            bool inside = Geometry2D.Contains(Polygons2D.Points(path), p);
            if (inside) winding += Clipper.Area(path) > 0 ? 1 : -1;
        }
        return winding > 0;
    }

    /// <summary>The brim's lines round the first layer's outsides (not in its holes), innermost first, or else the skirt's loops a gap away from it and its support.</summary>
    private static IEnumerable<Extrusion> FirstLayerExtras(PathsD first, PathsD? support, SliceSettings s, double spacing)
    {
        double w = s.LineWidth;
        if (s.BrimLines > 0)
        {
            for (int k = 0; k < s.BrimLines; k++)
                foreach (var p in Polygons2D.Offset(first, w / 2 + k * spacing, Polygons2D.Corners.Round))
                    if (Clipper.Area(p) > 0) yield return new Extrusion(PathKind.Brim, Polygons2D.Points(p), true);
            yield break;
        }
        var footprint = support is { Count: > 0 } ? Polygons2D.Union(first, support) : first;
        for (int k = 0; k < s.SkirtLoops; k++)
            foreach (var p in Polygons2D.Offset(footprint, s.SkirtDistance + w / 2 + k * spacing, Polygons2D.Corners.Round))
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

    /// <summary>
    /// Gyroid infill for a region at height <paramref name="z"/>: the curves where sin kx cos ky + sin ky cos kz +
    /// sin kz cos kx = 0, traced by marching squares on a grid fixed to the bed (so layers line up) and clipped to the
    /// region. k is set so the curves lie about <paramref name="apart"/> mm from each other, as lines that far apart
    /// would, the waviness adding a little length.
    /// </summary>
    internal static List<Extrusion> Gyroid(PathsD region, double apart, double z)
    {
        var result = new List<Extrusion>();
        if (region.Count == 0) return result;
        double k = Math.PI / apart;
        double h = 2 * Math.PI / k / 12;
        var pts = region.SelectMany(p => p).ToList();
        int i0 = (int)Math.Floor(pts.Min(p => p.x) / h) - 1, i1 = (int)Math.Ceiling(pts.Max(p => p.x) / h) + 1;
        int j0 = (int)Math.Floor(pts.Min(p => p.y) / h) - 1, j1 = (int)Math.Ceiling(pts.Max(p => p.y) / h) + 1;
        int nx = i1 - i0 + 1, ny = j1 - j0 + 1;
        double sz = Math.Sin(k * z), cz = Math.Cos(k * z);
        var f = new double[nx, ny];
        for (int a = 0; a < nx; a++)
        {
            double x = (i0 + a) * h, sx = Math.Sin(k * x), cx = Math.Cos(k * x);
            for (int b = 0; b < ny; b++)
            {
                double y = (j0 + b) * h, sy = Math.Sin(k * y), cy = Math.Cos(k * y);
                double v = sx * cy + sy * cz + sz * cx;
                // Never exactly zero at a node, so each cell edge is crossed once or not at all.
                f[a, b] = v == 0 ? 1e-12 : v;
            }
        }
        // Where the curve crosses each cell edge, by the edge: horizontal (a, b)-(a+1, b) and vertical (a, b)-(a, b+1).
        long Edge(int a, int b, bool vertical) => ((long)a * ny + b) * 2 + (vertical ? 1 : 0);
        Vec2 Cut(int a, int b, bool vertical)
        {
            int a2 = vertical ? a : a + 1, b2 = vertical ? b + 1 : b;
            double t = f[a, b] / (f[a, b] - f[a2, b2]);
            return new Vec2((i0 + a + (a2 - a) * t) * h, (j0 + b + (b2 - b) * t) * h);
        }
        var links = new Dictionary<long, List<long>>();
        void Link(long e1, long e2)
        {
            (links.TryGetValue(e1, out var l1) ? l1 : links[e1] = []).Add(e2);
            (links.TryGetValue(e2, out var l2) ? l2 : links[e2] = []).Add(e1);
        }
        for (int a = 0; a + 1 < nx; a++)
            for (int b = 0; b + 1 < ny; b++)
            {
                // The cell's four edges, round it: bottom, right, top, left; those the sign changes across.
                var crossed = new List<long>(4);
                if (f[a, b] > 0 != f[a + 1, b] > 0) crossed.Add(Edge(a, b, false));
                if (f[a + 1, b] > 0 != f[a + 1, b + 1] > 0) crossed.Add(Edge(a + 1, b, true));
                if (f[a, b + 1] > 0 != f[a + 1, b + 1] > 0) crossed.Add(Edge(a, b + 1, false));
                if (f[a, b] > 0 != f[a, b + 1] > 0) crossed.Add(Edge(a, b, true));
                if (crossed.Count == 2) Link(crossed[0], crossed[1]);
                else if (crossed.Count == 4)
                {
                    // A saddle: pair the crossings by the cell's middle value.
                    double mid = (f[a, b] + f[a + 1, b] + f[a, b + 1] + f[a + 1, b + 1]) / 4;
                    if (mid > 0 == f[a, b] > 0) { Link(crossed[0], crossed[1]); Link(crossed[2], crossed[3]); }
                    else { Link(crossed[0], crossed[3]); Link(crossed[1], crossed[2]); }
                }
            }
        Vec2 At(long e)
        {
            bool vertical = (e & 1) == 1;
            long cell = e >> 1;
            return Cut((int)(cell / ny), (int)(cell % ny), vertical);
        }
        // Chains from each end (an edge with one link) first, then the closed loops left.
        var seen = new HashSet<long>();
        var lines = new List<List<Vec2>>();
        void Walk(long start)
        {
            var line = new List<Vec2> { At(start) };
            seen.Add(start);
            long prev = -1, cur = start;
            while (true)
            {
                long next = -1;
                foreach (var n in links[cur])
                    if (n != prev && !seen.Contains(n)) { next = n; break; }
                if (next < 0)
                {
                    if (links[cur].Contains(start) && line.Count > 2 && prev != start) line.Add(line[0]);
                    break;
                }
                line.Add(At(next));
                seen.Add(next);
                prev = cur;
                cur = next;
            }
            if (line.Count >= 2) lines.Add(line);
        }
        foreach (var (e, l) in links)
            if (l.Count == 1 && !seen.Contains(e)) Walk(e);
        foreach (var e in links.Keys)
            if (!seen.Contains(e)) Walk(e);
        foreach (var piece in Polygons2D.ClipLines(lines, region))
            if (Geometry2D.Perimeter(piece, false) > 1e-3) result.Add(new Extrusion(PathKind.Infill, piece, false));
        return result;
    }

    /// <summary>Support for each layer, worked down from the top (see <see cref="Planner"/>).</summary>
    private static PathsD[] SupportRegions(PathsD[] region, IReadOnlyList<SliceLayer> layers, SliceSettings s)
    {
        int n = region.Length;
        var support = new PathsD[n];
        for (int i = 0; i < n; i++) support[i] = [];
        if (n == 0) return support;
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
        if (s.SupportOn == SupportPlacement.BedOnly)
            // Only what stands on support all the way down to the bed: worked up from the first layer.
            for (int i = 1; i < n; i++) support[i] = Polygons2D.Intersect(support[i], support[i - 1]);
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
