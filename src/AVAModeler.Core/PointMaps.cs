namespace AVAModeler.Core;

/// <summary>What a point map holds: a weight (LWO2's WGHT), a colour (RGB) or a colour with alpha (RGBA).</summary>
public enum PointMapKind { Weight, Rgb, Rgba }

/// <summary>
/// A named per-point map: a weight from 0 to 1 a point (for bones, as LWO2's VMAP of type WGHT) or a colour (RGB or
/// RGBA, 0 to 1 a channel, as LWO2's RGB and RGBA VMAPs). Points not in the map have none: a weight of 0, no colour.
/// Like UV maps, point maps follow the edits that renumber points.
/// </summary>
public sealed class PointMap(string name, PointMapKind kind)
{
    public string Name = name;
    public PointMapKind Kind = kind;
    /// <summary>Each mapped point's values (one for a weight, three or four for a colour), by point index.</summary>
    public Dictionary<int, float[]> Values = [];

    public int Dimension => DimensionOf(Kind);

    public static int DimensionOf(PointMapKind kind) => kind switch { PointMapKind.Weight => 1, PointMapKind.Rgb => 3, _ => 4 };

    /// <summary>LWO2's VMAP type for the kind.</summary>
    public string Lwo2Type => Kind switch { PointMapKind.Weight => "WGHT", PointMapKind.Rgb => "RGB ", _ => "RGBA" };

    public bool IsColor => Kind != PointMapKind.Weight;

    public PointMap Clone() => new(Name, Kind) { Values = Values.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()) };

    /// <summary>A point's weight (0 where it has none).</summary>
    public double Weight(int point) => Values.TryGetValue(point, out var v) ? v[0] : 0;

    /// <summary>Sets a point's weight; 0 removes it, as LightWave keeps only the points a weight map names.</summary>
    public void SetWeight(int point, double weight)
    {
        if (weight == 0) Values.Remove(point);
        else Values[point] = [(float)weight];
    }

    /// <summary>A point's colour (0 to 1 a channel), or null.</summary>
    public Vec3? Color(int point) => Values.TryGetValue(point, out var v) && v.Length >= 3 ? new Vec3(v[0], v[1], v[2]) : null;

    /// <summary>Sets a point's colour (and alpha, for RGBA).</summary>
    public void SetColor(int point, Vec3 color, double alpha = 1) =>
        Values[point] = Kind == PointMapKind.Rgba ? [(float)color.X, (float)color.Y, (float)color.Z, (float)alpha] : [(float)color.X, (float)color.Y, (float)color.Z];

    public override string ToString() => $"{Name} ({Kind}, {Values.Count} points)";
}

public sealed partial class Mesh
{
    /// <summary>The mesh's weight and colour maps. Edits that renumber points keep them in step, as with UV maps.</summary>
    public List<PointMap> PointMaps = [];

    /// <summary>The point map of a name (and kind, if given), or null.</summary>
    public PointMap? FindPointMap(string name, PointMapKind? kind = null) =>
        PointMaps.Find(m => m.Name == name && (kind == null || m.Kind == kind || kind != PointMapKind.Weight && m.IsColor));

    /// <summary>The point map of a name, made (empty) if there isn't one. A map of that name but another kind is refused.</summary>
    public PointMap AddPointMap(string name, PointMapKind kind)
    {
        var map = PointMaps.Find(m => m.Name == name);
        if (map != null)
        {
            if (map.Kind != kind && (map.Kind == PointMapKind.Weight || kind == PointMapKind.Weight))
                throw new ArgumentException($"\"{name}\" is already a {(map.Kind == PointMapKind.Weight ? "weight" : "colour")} map.");
            return map;
        }
        map = new PointMap(name, kind);
        PointMaps.Add(map);
        Touch();
        return map;
    }

    public bool RemovePointMap(string name)
    {
        if (PointMaps.RemoveAll(m => m.Name == name) == 0) return false;
        Touch();
        return true;
    }

    /// <summary>Gives a new point the values another has in every point map: for tools that copy points.</summary>
    public void CopyPointMaps(int from, int to)
    {
        foreach (var m in PointMaps)
            if (m.Values.TryGetValue(from, out var v)) m.Values[to] = (float[])v.Clone();
    }

    partial void BlendPointMaps(int to, int a, int b, double t)
    {
        foreach (var m in PointMaps)
        {
            bool hasA = m.Values.TryGetValue(a, out var va), hasB = m.Values.TryGetValue(b, out var vb);
            if (!hasA && !hasB) continue;
            if (m.IsColor && (!hasA || !hasB)) { m.Values[to] = (float[])(hasA ? va! : vb!).Clone(); continue; }
            var v = new float[m.Dimension];
            for (int k = 0; k < v.Length; k++)
                v[k] = (float)((hasA ? va![k] : 0) * (1 - t) + (hasB ? vb![k] : 0) * t);
            if (m.Kind == PointMapKind.Weight && v[0] == 0) continue;
            m.Values[to] = v;
        }
    }

    /// <summary>Renumbers the point maps after points were removed (old index → new, -1 for gone).</summary>
    private void RemapPointMaps(int[] map)
    {
        foreach (var m in PointMaps)
            m.Values = m.Values.Where(kv => kv.Key >= 0 && kv.Key < map.Length && map[kv.Key] >= 0).ToDictionary(kv => map[kv.Key], kv => kv.Value);
    }
}
