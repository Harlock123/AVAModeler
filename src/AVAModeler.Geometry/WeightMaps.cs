using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How a weight brush dab changes the weights under it.</summary>
public enum WeightPaintMode { Add, Subtract, Set, Smooth }

/// <summary>
/// Weight maps' sums: painting a dab, smoothing, normalising several maps to add to 1, mirroring across an axis, and
/// weights from skelegons (an automatic rig: each point's share of each bone by distance, smoothly).
/// </summary>
public static class WeightMaps
{
    /// <summary>
    /// One dab of a weight brush on the points under it (with their falloff): Add and Subtract change a weight by
    /// strength × falloff, Set moves it that far toward <paramref name="value"/>, Smooth toward its neighbours' average.
    /// Weights stay from 0 to 1.
    /// </summary>
    public static void Dab(Mesh mesh, PointMap map, IReadOnlyList<(int Point, double Weight)> under, WeightPaintMode mode, double strength, double value = 1)
    {
        if (under.Count == 0) return;
        double[]? average = null;
        if (mode == WeightPaintMode.Smooth)
        {
            average = new double[under.Count];
            for (int k = 0; k < under.Count; k++)
            {
                var nb = mesh.Neighbours(under[k].Point).ToList();
                average[k] = nb.Count == 0 ? map.Weight(under[k].Point) : nb.Average(map.Weight);
            }
        }
        for (int k = 0; k < under.Count; k++)
        {
            var (p, f) = under[k];
            double w = map.Weight(p), a = Math.Clamp(strength * f, 0, 1);
            w = mode switch
            {
                WeightPaintMode.Add => w + a,
                WeightPaintMode.Subtract => w - a,
                WeightPaintMode.Set => w + (value - w) * a,
                _ => w + (average![k] - w) * a,
            };
            map.SetWeight(p, Math.Clamp(w, 0, 1));
        }
    }

    /// <summary>Eases each chosen point's weight toward its neighbours' average, a share a pass.</summary>
    public static void Smooth(Mesh mesh, PointMap map, IReadOnlyCollection<int> points, double strength, int iterations)
    {
        var chosen = points.Distinct().ToArray();
        var neighbours = chosen.Select(i => mesh.Neighbours(i).ToArray()).ToArray();
        for (int it = 0; it < iterations; it++)
        {
            var next = new double[chosen.Length];
            for (int k = 0; k < chosen.Length; k++)
            {
                double w = map.Weight(chosen[k]);
                next[k] = neighbours[k].Length == 0 ? w : w + (neighbours[k].Average(map.Weight) - w) * strength;
            }
            for (int k = 0; k < chosen.Length; k++) map.SetWeight(chosen[k], Math.Clamp(next[k], 0, 1));
        }
    }

    /// <summary>
    /// Scales the maps' weights at each chosen point so they add to 1 (a point in none of them is left alone), as bones
    /// expect where several share a point.
    /// </summary>
    public static int Normalize(IReadOnlyList<PointMap> maps, IReadOnlyCollection<int> points)
    {
        int changed = 0;
        foreach (int p in points)
        {
            double sum = maps.Sum(m => m.Weight(p));
            if (sum <= 0 || Math.Abs(sum - 1) < 1e-9) continue;
            foreach (var m in maps) if (m.Weight(p) > 0) m.SetWeight(p, m.Weight(p) / sum);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Copies each chosen point's weights on the positive (or negative) side of an axis to its mirror partner: in the
    /// same map, or in the map of the opposite name ("Left Arm" to "Right Arm") where there is one. Returns how many
    /// points were given weights.
    /// </summary>
    public static int Mirror(Mesh mesh, IReadOnlyList<PointMap> maps, Axis axis, bool fromPositive, IReadOnlyCollection<int> points)
    {
        var pairs = Symmetry.Pairs(mesh, axis);
        var sources = points.Where(i => i < pairs.Length && pairs[i] >= 0 && pairs[i] != i && (mesh.Points[i][axis] > 0) == fromPositive).ToList();
        var snapshot = mesh.PointMaps.ToDictionary(m => m, m => m.Clone());
        var targets = new HashSet<int>();
        foreach (var map in maps)
        {
            var opposite = MapNames.Opposite(map.Name) is { } o ? mesh.FindPointMap(o, PointMapKind.Weight) : null;
            var into = opposite ?? map;
            var from = snapshot[map];
            foreach (int i in sources)
            {
                into.SetWeight(pairs[i], from.Weight(i));
                targets.Add(pairs[i]);
            }
        }
        return targets.Count;
    }

    /// <summary>
    /// Weight maps from a mesh's skelegons: one map per skelegon, named as the bone it becomes, each point weighted by
    /// 1 / distance^falloff to each skelegon, normalised to add to 1, with shares under <paramref name="threshold"/>
    /// dropped (and the rest normalised again). Existing maps of those names are replaced. Returns the maps' names.
    /// </summary>
    public static List<string> FromSkelegons(Mesh mesh, IReadOnlyList<Surface> surfaces, double falloff = 4, double threshold = 0.01, IReadOnlyCollection<int>? points = null, string prefix = SkelegonNames.SurfacePrefix)
    {
        var bones = SkelegonNames.Of(mesh, surfaces, prefix);
        if (bones.Count == 0) throw new InvalidOperationException("Weights from Skelegons needs skelegons: 2-point polygons on a surface named Skelegon… (draw them with Points and Make Polygon, or Make Curve).");
        var skelegonPoints = new HashSet<int>(mesh.Polygons.Where(p => p.Count == 2 && p.Surface >= 0 && p.Surface < surfaces.Count && surfaces[p.Surface].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Points));
        var maps = new List<PointMap>();
        foreach (var b in bones)
        {
            var existing = mesh.PointMaps.Find(m => m.Name == b.Name);
            if (existing != null && existing.Kind != PointMapKind.Weight) throw new ArgumentException($"\"{b.Name}\" is already a colour map: rename the skelegon's surface.");
            var map = existing ?? mesh.AddPointMap(b.Name, PointMapKind.Weight);
            maps.Add(map);
        }
        var chosen = points ?? Enumerable.Range(0, mesh.Points.Count).ToArray();
        var w = new double[bones.Count];
        foreach (int p in chosen)
        {
            if (skelegonPoints.Contains(p)) continue;
            double sum = 0;
            for (int k = 0; k < bones.Count; k++)
            {
                double d = Math.Max(DistanceToSegment(mesh.Points[p], bones[k].Start, bones[k].End), 1e-6);
                w[k] = 1 / Math.Pow(d, falloff);
                sum += w[k];
            }
            for (int k = 0; k < w.Length; k++) w[k] /= sum;
            double kept = 0;
            for (int k = 0; k < w.Length; k++) if (w[k] < threshold) w[k] = 0; else kept += w[k];
            for (int k = 0; k < w.Length; k++) maps[k].SetWeight(p, kept > 0 ? w[k] / kept : 0);
        }
        mesh.Touch();
        return bones.Select(b => b.Name).ToList();
    }

    public static double DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 == 0 ? 0 : Math.Clamp(Vec3.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>A weight's colour in the views: blue at 0, green at a half, red at 1.</summary>
    public static Vec3 HeatColor(double w)
    {
        w = Math.Clamp(w, 0, 1);
        return w < 0.5
            ? Vec3.Lerp(new Vec3(0.15, 0.25, 0.9), new Vec3(0.2, 0.85, 0.25), w * 2)
            : Vec3.Lerp(new Vec3(0.2, 0.85, 0.25), new Vec3(0.95, 0.2, 0.15), (w - 0.5) * 2);
    }
}
