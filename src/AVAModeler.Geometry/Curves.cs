using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Curves drawn as chains of 2-point lines, the way the Modeler keeps them: following a chain, smoothing it through its
/// points, and spline patches across a closed loop of them.
/// </summary>
public static class Curves
{
    /// <summary>
    /// The chains the given 2-point lines make, each as its point indices in order, and whether it closes on itself.
    /// A point where three or more lines meet ends the chains there.
    /// </summary>
    public static List<(List<int> Points, bool Closed)> Chains(Mesh mesh, IEnumerable<int> lines)
    {
        var next = new Dictionary<int, List<int>>();
        foreach (int i in lines)
        {
            var p = mesh.Polygons[i];
            if (p.Count != 2 || p.Points[0] == p.Points[1]) continue;
            (next.TryGetValue(p.Points[0], out var a) ? a : next[p.Points[0]] = []).Add(p.Points[1]);
            (next.TryGetValue(p.Points[1], out var b) ? b : next[p.Points[1]] = []).Add(p.Points[0]);
        }
        var used = new HashSet<(int, int)>();
        bool Take(int a, int b) => used.Add(a < b ? (a, b) : (b, a));
        var chains = new List<(List<int>, bool)>();
        void Walk(int start)
        {
            foreach (int first in next[start])
            {
                if (!Take(start, first)) continue;
                var chain = new List<int> { start, first };
                bool closed = false;
                while (next[chain[^1]].Count == 2)
                {
                    int cur = chain[^1], prev = chain[^2];
                    int n = next[cur][0] == prev && next[cur].Count > 1 ? next[cur][1] : next[cur][0];
                    if (!Take(cur, n)) break;
                    if (n == chain[0]) { closed = true; break; }
                    chain.Add(n);
                }
                chains.Add((chain, closed));
            }
        }
        // Ends and junctions first, then whatever's left (closed loops).
        foreach (int p in next.Keys.Where(k => next[k].Count != 2).ToList()) Walk(p);
        foreach (int p in next.Keys.ToList()) Walk(p);
        return chains;
    }

    /// <summary>
    /// A smooth curve through the points (centripetal Catmull–Rom, so it doesn't overshoot or loop at uneven
    /// spacing), <paramref name="perSpan"/> pieces between each pair; closed curves wrap round.
    /// </summary>
    public static List<Vec3> Smooth(IReadOnlyList<Vec3> points, int perSpan, bool closed = false)
    {
        int n = points.Count;
        if (n < 3 || perSpan < 2) return [.. points];
        Vec3 P(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        var result = new List<Vec3>();
        int spans = closed ? n : n - 1;
        for (int s = 0; s < spans; s++)
        {
            Vec3 p0 = P(s - 1), p1 = P(s), p2 = P(s + 1), p3 = P(s + 2);
            // At an open curve's ends, a mirrored neighbour keeps the end tangent along the first and last pieces.
            if (!closed && s == 0) p0 = p1 * 2 - p2;
            if (!closed && s == spans - 1) p3 = p2 * 2 - p1;
            double t0 = 0, t1 = t0 + Math.Sqrt(Math.Max(1e-12, Vec3.Distance(p0, p1)));
            double t2 = t1 + Math.Sqrt(Math.Max(1e-12, Vec3.Distance(p1, p2))), t3 = t2 + Math.Sqrt(Math.Max(1e-12, Vec3.Distance(p2, p3)));
            for (int k = 0; k < perSpan; k++)
            {
                double t = t1 + (t2 - t1) * k / perSpan;
                var a1 = Lerp(p0, p1, t0, t1, t); var a2 = Lerp(p1, p2, t1, t2, t); var a3 = Lerp(p2, p3, t2, t3, t);
                var b1 = Lerp(a1, a2, t0, t2, t); var b2 = Lerp(a2, a3, t1, t3, t);
                result.Add(Lerp(b1, b2, t1, t2, t));
            }
        }
        if (!closed) result.Add(points[^1]);
        return result;
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, double ta, double tb, double t) => tb - ta < 1e-12 ? a : a * ((tb - t) / (tb - ta)) + b * ((t - ta) / (tb - ta));

    /// <summary>
    /// The corners of a closed loop for a patch of <paramref name="sides"/> sides: the points where it turns most
    /// sharply, in the loop's order.
    /// </summary>
    public static int[] SharpestCorners(IReadOnlyList<Vec3> loop, int sides)
    {
        int n = loop.Count;
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = (loop[i] - loop[(i + n - 1) % n]).Normalized();
            var b = (loop[(i + 1) % n] - loop[i]).Normalized();
            turn[i] = Math.Acos(Math.Clamp(Vec3.Dot(a, b), -1, 1));
        }
        return Enumerable.Range(0, n).OrderByDescending(i => turn[i]).Take(sides).Order().ToArray();
    }

    /// <summary>
    /// A spline patch: a grid of quads across a closed boundary loop, split into 3 or 4 sides at
    /// <paramref name="corners"/> (indices into the loop, in order). Sides are 0→1, 1→2, 2→3 and 3→0 (the fourth is a
    /// single point for three sides). Each pair of opposite sides is resampled to <paramref name="across"/> or
    /// <paramref name="along"/> pieces (smoothed first if asked), and the inside filled by Coons' blend of the four, so
    /// the patch meets every side exactly. Returns the new polygons' indices.
    /// </summary>
    public static List<int> SplinePatch(Mesh mesh, IReadOnlyList<Vec3> loop, int[] corners, int across, int along, bool smooth, int surface = 0)
    {
        int n = loop.Count, sides = corners.Length;
        if (sides is not (3 or 4)) throw new ArgumentException("A spline patch has 3 or 4 sides.");
        List<Vec3> Side(int a, int b)
        {
            var pts = new List<Vec3>();
            for (int i = corners[a]; ; i = (i + 1) % n)
            {
                pts.Add(loop[i]);
                if (i == corners[b]) break;
            }
            if (smooth && pts.Count >= 3) pts = Smooth(pts, 8);
            return pts;
        }
        var bottom = Multiply.Resample(Side(0, 1), across);
        var right = Multiply.Resample(Side(1, 2), along);
        // The top and left run the other way round the loop: reversed so u and v both run away from corner 0.
        var top = Multiply.Resample(Enumerable.Reverse(Side(2, sides == 4 ? 3 : 0)).ToList(), across);
        var left = sides == 4 ? Multiply.Resample(Enumerable.Reverse(Side(3, 0)).ToList(), along) : Enumerable.Repeat(loop[corners[0]], along + 1).ToList();
        // (Three sides: the top is side 2→0, and the left collapses to corner 0.)
        Vec3 p00 = bottom[0], p10 = bottom[^1], p01 = top[0], p11 = top[^1];
        var grid = new int[across + 1, along + 1];
        var points = new Dictionary<Vec3, int>();
        int Point(Vec3 p)
        {
            if (!points.TryGetValue(p, out int i))
            {
                points[p] = i = mesh.Points.Count;
                mesh.Points.Add(p);
            }
            return i;
        }
        for (int i = 0; i <= across; i++)
            for (int j = 0; j <= along; j++)
            {
                double u = (double)i / across, v = (double)j / along;
                Vec3 p;
                if (j == 0) p = bottom[i];
                else if (j == along) p = top[i];
                else if (i == 0) p = left[j];
                else if (i == across) p = right[j];
                else
                    p = bottom[i] * (1 - v) + top[i] * v + left[j] * (1 - u) + right[j] * u
                        - (p00 * ((1 - u) * (1 - v)) + p10 * (u * (1 - v)) + p01 * ((1 - u) * v) + p11 * (u * v));
                grid[i, j] = Point(p);
            }
        var made = new List<int>();
        for (int i = 0; i < across; i++)
            for (int j = 0; j < along; j++)
            {
                int[] q = [grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]];
                // Where the patch narrows to a point (three sides), the quad's two corners there are one: a triangle.
                var face = q.Where((p, k) => p != q[(k + 3) % 4]).ToList();
                if (face.Count < 3) continue;
                made.Add(mesh.Polygons.Count);
                mesh.Polygons.Add(new Polygon(face, surface));
            }
        mesh.Touch();
        return made;
    }

    /// <summary>
    /// A curve of 2-point lines through existing points (<paramref name="controls"/>, in order), closed back to the
    /// first if asked. Smooth runs it through them as a smooth curve, <paramref name="perSpan"/> pieces between each
    /// pair, adding the points between (the controls stay where they are). Returns the new lines' indices.
    /// </summary>
    public static List<int> AddCurve(Mesh mesh, IReadOnlyList<int> controls, bool closed, bool smooth, int perSpan, int surface = 0)
    {
        if (controls.Count < 2) throw new ArgumentException("A curve needs at least two points.");
        if (closed && controls.Count < 3) throw new ArgumentException("A closed curve needs at least three points.");
        var chain = new List<int>();
        if (smooth && controls.Count >= 3 && perSpan >= 2)
        {
            var path = Smooth([.. controls.Select(i => mesh.Points[i])], perSpan, closed);
            for (int k = 0; k < path.Count; k++)
                // Every perSpan-th point is a control point, exactly where it was.
                chain.Add(k % perSpan == 0 && k / perSpan < controls.Count ? controls[k / perSpan] : mesh.AddPoint(path[k]));
        }
        else chain.AddRange(controls);
        var lines = new List<int>();
        int n = chain.Count;
        for (int k = 0; k + 1 < n || closed && k < n; k++)
            lines.Add(mesh.AddPolygon([chain[k], chain[(k + 1) % n]], surface));
        mesh.Touch();
        return lines;
    }

    /// <summary>
    /// A curve resampled to <paramref name="pieces"/> even pieces along it (pieces + 1 points open; pieces closed,
    /// the first not repeated).
    /// </summary>
    public static List<Vec3> Even(IReadOnlyList<Vec3> curve, int pieces, bool closed)
    {
        if (!closed) return Multiply.Resample(curve, pieces);
        var round = Multiply.Resample([.. curve, curve[0]], pieces);
        round.RemoveAt(round.Count - 1);
        return round;
    }

    /// <summary>
    /// A skin across curves (Loft): each curve resampled to <paramref name="across"/> even pieces and turned to run the
    /// way the one before does (a closed one also started where it's nearest), then joined in the order they stack up
    /// (from one end, each to the nearest left) by <paramref name="along"/> rows between each pair, straight or
    /// (<paramref name="smooth"/>) on smooth curves through all of them. All the curves must be open or all closed (a
    /// tube). Returns the new faces' indices.
    /// </summary>
    public static List<int> Loft(Mesh mesh, IReadOnlyList<IReadOnlyList<Vec3>> curves, bool closed, int across, int along, bool smooth, int surface = 0)
    {
        if (curves.Count < 2) throw new ArgumentException("Loft needs at least two curves.");
        // The order they stack in: from one of the two that are furthest apart, each time the nearest of the rest.
        var centres = curves.Select(c => c.Aggregate(Vec3.Zero, (a, b) => a + b) / c.Count).ToList();
        int first = 0;
        double far = -1;
        for (int a = 0; a < centres.Count; a++)
            for (int b = 0; b < centres.Count; b++)
                if (Vec3.Distance(centres[a], centres[b]) > far) (far, first) = (Vec3.Distance(centres[a], centres[b]), a);
        var order = new List<int> { first };
        while (order.Count < curves.Count)
            order.Add(Enumerable.Range(0, curves.Count).Where(i => !order.Contains(i)).MinBy(i => Vec3.Distance(centres[order[^1]], centres[i])));
        var rows = new List<List<Vec3>>();
        foreach (int c in order)
        {
            var even = Even(curves[c], across, closed);
            if (rows.Count > 0)
            {
                var prev = rows[^1];
                if (!closed)
                {
                    var back = Enumerable.Reverse(even).ToList();
                    if (Gap(back, prev) < Gap(even, prev)) even = back;
                }
                else
                {
                    // Either way round, from whichever point lines up best with the curve before.
                    List<Vec3>? best = null;
                    double bestGap = double.PositiveInfinity;
                    foreach (var way in new[] { even, Enumerable.Reverse(even).ToList() })
                        for (int shift = 0; shift < way.Count; shift++)
                        {
                            var turned = way.Skip(shift).Concat(way.Take(shift)).ToList();
                            double gap = Gap(turned, prev);
                            if (gap < bestGap) (best, bestGap) = (turned, gap);
                        }
                    even = best!;
                }
            }
            rows.Add(even);
        }
        int width = rows[0].Count;
        // The rows between: each column a curve through the curves' points, smooth or straight.
        var columns = new List<List<Vec3>>();
        for (int j = 0; j < width; j++)
        {
            var through = rows.Select(r => r[j]).ToList();
            List<Vec3> col;
            if (smooth && through.Count >= 3 && along >= 2) col = Smooth(through, along);
            else
            {
                col = [];
                for (int r = 0; r + 1 < through.Count; r++)
                    for (int k = 0; k < along; k++) col.Add(Vec3.Lerp(through[r], through[r + 1], (double)k / along));
                col.Add(through[^1]);
            }
            columns.Add(col);
        }
        int height = columns[0].Count;
        var index = new int[width, height];
        for (int j = 0; j < width; j++)
            for (int r = 0; r < height; r++) index[j, r] = mesh.AddPoint(columns[j][r]);
        var made = new List<int>();
        for (int j = 0; j < (closed ? width : width - 1); j++)
            for (int r = 0; r + 1 < height; r++)
            {
                int jn = (j + 1) % width;
                made.Add(mesh.AddPolygon([index[j, r], index[j, r + 1], index[jn, r + 1], index[jn, r]], surface));
            }
        mesh.Touch();
        return made;
    }

    private static double Gap(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Count; i++) sum += Vec3.Distance(a[i], b[i]);
        return sum;
    }
}
