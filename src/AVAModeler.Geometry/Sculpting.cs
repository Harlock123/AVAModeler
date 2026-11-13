using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>The sculpt brushes.</summary>
public enum SculptBrush
{
    /// <summary>Raises the surface along its average normal under the brush.</summary>
    Draw,
    /// <summary>Pushes each point out along its own normal, swelling the shape.</summary>
    Inflate,
    /// <summary>Eases points toward their neighbours' average.</summary>
    Smooth,
    /// <summary>Moves the points under the brush along with the drag.</summary>
    Grab,
    /// <summary>Draws points in toward the brush's centre, across the surface.</summary>
    Pinch,
    /// <summary>Flattens points onto the plane of the surface under the brush.</summary>
    Flatten,
    /// <summary>Pinches and cuts in: a sharp groove (inverted, a ridge).</summary>
    Crease,
}

/// <summary>
/// The sculpt brushes' sums: one dab changes the points under it by their falloff. Heights are a share of the brush's
/// radius, so a brush works alike at any scale: at strength 1 and the centre, a Draw dab raises the surface by a tenth
/// of the radius.
/// </summary>
public static class Sculpting
{
    /// <summary>How far a dab of strength 1 moves a point at its centre, as a share of the radius.</summary>
    public const double Height = 0.1;

    /// <summary>
    /// One dab of a brush at <paramref name="center"/> on the points under it (their falloff already worked out).
    /// <paramref name="invert"/> digs instead of raising (Draw, Inflate, Crease) or pushes out instead of pinching.
    /// Grab uses <paramref name="offset"/>, the drag so far; the others ignore it.
    /// </summary>
    public static void Dab(Mesh mesh, IReadOnlyList<(int Point, double Weight)> under, SculptBrush brush, Vec3 center, double radius, double strength, bool invert, Vec3 offset = default)
    {
        if (under.Count == 0) return;
        double sign = invert ? -1 : 1;
        double h = Height * radius * strength * sign;
        var moves = new Vec3[under.Count];
        switch (brush)
        {
            case SculptBrush.Draw:
            {
                var n = AreaNormal(mesh, under);
                for (int k = 0; k < under.Count; k++) moves[k] = n * (h * under[k].Weight);
                break;
            }
            case SculptBrush.Inflate:
                for (int k = 0; k < under.Count; k++) moves[k] = PointNormal(mesh, under[k].Point) * (h * under[k].Weight);
                break;
            case SculptBrush.Smooth:
                for (int k = 0; k < under.Count; k++)
                {
                    var p = mesh.Points[under[k].Point];
                    var nb = mesh.Neighbours(under[k].Point).ToList();
                    if (nb.Count == 0) continue;
                    var avg = nb.Aggregate(Vec3.Zero, (s, j) => s + mesh.Points[j]) / nb.Count;
                    moves[k] = (avg - p) * Math.Clamp(strength * under[k].Weight, 0, 1);
                }
                break;
            case SculptBrush.Grab:
                for (int k = 0; k < under.Count; k++) moves[k] = offset * under[k].Weight;
                break;
            case SculptBrush.Pinch:
            case SculptBrush.Crease:
            {
                var n = AreaNormal(mesh, under);
                for (int k = 0; k < under.Count; k++)
                {
                    var p = mesh.Points[under[k].Point];
                    var toward = center - p;
                    toward -= n * Vec3.Dot(toward, n);
                    var move = toward * (0.5 * Math.Clamp(strength, 0, 1) * under[k].Weight * sign);
                    if (brush == SculptBrush.Crease) move -= n * (h * 0.5 * under[k].Weight);
                    moves[k] = move;
                }
                break;
            }
            case SculptBrush.Flatten:
            {
                var n = AreaNormal(mesh, under);
                var mid = Vec3.Zero;
                double total = 0;
                foreach (var (p, w) in under) { mid += mesh.Points[p] * w; total += w; }
                mid /= Math.Max(total, 1e-12);
                for (int k = 0; k < under.Count; k++)
                {
                    var p = mesh.Points[under[k].Point];
                    double d = Vec3.Dot(p - mid, n);
                    moves[k] = -n * (d * Math.Clamp(strength * under[k].Weight, 0, 1));
                }
                break;
            }
        }
        for (int k = 0; k < under.Count; k++) mesh.Points[under[k].Point] += moves[k];
    }

    /// <summary>A point's normal: the area-weighted average of its faces' (zero for a point on none).</summary>
    public static Vec3 PointNormal(Mesh mesh, int point)
    {
        var sum = Vec3.Zero;
        foreach (int pi in mesh.PolygonsOf(point))
        {
            var poly = mesh.Polygons[pi];
            if (poly.Count >= 3) sum += mesh.Normal(poly) * mesh.Area(poly);
        }
        return sum.LengthSquared > 0 ? sum.Normalized() : Vec3.Zero;
    }

    /// <summary>The surface's normal under a dab: its points' normals weighted by falloff.</summary>
    public static Vec3 AreaNormal(Mesh mesh, IReadOnlyList<(int Point, double Weight)> under)
    {
        var sum = Vec3.Zero;
        foreach (var (p, w) in under) sum += PointNormal(mesh, p) * w;
        return sum.LengthSquared > 0 ? sum.Normalized() : Vec3.UnitY;
    }
}
