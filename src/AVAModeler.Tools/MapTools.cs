using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>
/// A brush: dragged over the model, it lays dabs where the mouse's ray meets the foreground's surface, a share of its
/// radius apart, each working on the points within its radius by its falloff. The whole stroke is one undo step. Run
/// from a script it lays one dab at Center. With symmetry on, each dab has a mirrored twin.
/// </summary>
public abstract class BrushTool : Tool
{
    [Param("Radius", Distance = true, Min = 0, Order = 90, Description = "How far a dab reaches, in metres.")] public double Radius { get; set; } = 0.25;
    [Param("Strength", Min = 0, Max = 1, Step = 0.05, Order = 91, Description = "How much a dab does at its centre, 0 to 1.")] public double Strength { get; set; } = 0.5;
    [Param("Falloff", Order = 92, Description = "How a dab fades toward its radius: Smooth, Linear, Sharp, Dome or Constant.")] public BrushFalloff Falloff { get; set; } = BrushFalloff.Smooth;
    [Param("Center", Distance = true, Order = 93, Description = "Run from a script: where its one dab goes (on or near the surface).")] public Vec3 Center { get; set; }
    [Param("Use pressure", Order = 94, Description = "A pen's pressure scales each dab's strength.")] public bool UsePressure { get; set; } = true;

    /// <summary>The stroke so far: each dab's centre (on the original surface), layer and pressure. Null when run from a script.</summary>
    private List<(int Layer, Vec3 At, double Pressure)>? _stroke;
    private Vec3? _last;

    /// <summary>The dabs this run lays: the stroke's, or one at Center in each foreground layer.</summary>
    protected IReadOnlyList<(int Layer, Vec3 At, double Pressure)> Dabs =>
        _stroke ?? [.. Doc.ForegroundLayers.Select(l => (l, Center, 1.0))];

    /// <summary>A stroke given as surface points (agents' strokes): spaced into dabs as a mouse's would be.</summary>
    public void SetStroke(int layer, IReadOnlyList<Vec3> path, IReadOnlyList<double>? pressure = null)
    {
        _stroke = [.. Brushes.Space(path, Spacing).Select((p, i) => (layer, p, pressure is { Count: > 0 } pr ? pr[Math.Min(i * pr.Count / Math.Max(1, path.Count), pr.Count - 1)] : 1.0))];
    }

    /// <summary>How far apart a stroke's dabs are.</summary>
    protected virtual double Spacing => Math.Max(Radius * 0.25, 1e-6);

    protected override void OnBegin()
    {
        _stroke = null;
        _last = null;
    }

    protected override void OnDrag(DragInfo d)
    {
        _stroke ??= [];
        if (d.Ray is not { } ray) return;
        var layers = Doc.ForegroundLayers.ToList();
        var hit = Brushes.Raycast([.. layers.Select(Original)], ray);
        if (hit is not { } h) { _last = null; return; }
        int layer = layers[h.Mesh];
        var at = h.Hit.Point;
        double pressure = UsePressure ? Math.Clamp(d.Pressure, 0, 1) : 1;
        if (_last is not { } last) { _stroke.Add((layer, at, pressure)); _last = at; return; }
        foreach (var p in Brushes.Space([last, at], Spacing).Skip(1)) { _stroke.Add((layer, p, pressure)); _last = p; }
    }

    protected override void Apply()
    {
        foreach (var (layer, at, pressure) in Dabs)
        {
            if (layer >= Doc.Layers.Count) continue;
            var mesh = Doc.Layers[layer].Mesh;
            var allowed = Doc.Selection.Any ? Doc.Selection.ActivePoints(layer, mesh).ToHashSet() : null;
            DabAt(layer, mesh, allowed, at, pressure, mirrored: false);
            if (Doc.Symmetry is { } axis && !Symmetry.OnPlane(at, axis))
                DabAt(layer, mesh, allowed, Symmetry.Mirror(at, axis), pressure, mirrored: true);
        }
        foreach (int l in Doc.ForegroundLayers) Doc.Layers[l].Mesh.Touch();
    }

    /// <summary>One dab: change the points under <paramref name="at"/>. <paramref name="mirrored"/> for symmetry's twin.</summary>
    protected abstract void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored);
}

/// <summary>The map a map tool works on: the named one, else the document's current one, made if new.</summary>
internal static class MapChoice
{
    public static PointMap Weight(Document doc, Mesh mesh, string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? doc.CurrentWeightMap ?? "Weight" : name.Trim();
        doc.CurrentWeightMap = n;
        return mesh.AddPointMap(n, PointMapKind.Weight);
    }

    public static PointMap Color(Document doc, Mesh mesh, string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? doc.CurrentColorMap ?? "Color" : name.Trim();
        doc.CurrentColorMap = n;
        return mesh.FindPointMap(n, PointMapKind.Rgb) ?? mesh.AddPointMap(n, PointMapKind.Rgb);
    }

    /// <summary>The weight maps named in a comma-separated list, or every weight map for "".</summary>
    public static List<PointMap> Weights(Mesh mesh, string? names)
    {
        var all = mesh.PointMaps.Where(m => m.Kind == PointMapKind.Weight).ToList();
        if (string.IsNullOrWhiteSpace(names)) return all;
        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var w in wanted)
            if (!all.Any(m => m.Name == w)) throw new ArgumentException($"There's no weight map \"{w}\".{(all.Count > 0 ? $" There are: {string.Join(", ", all.Select(m => m.Name))}." : "")}");
        return all.Where(m => wanted.Contains(m.Name)).ToList();
    }
}

/// <summary>Paint Weights: a brush that paints a weight map.</summary>
public sealed class PaintWeightsTool : BrushTool
{
    public override string Name => "Paint Weights";
    public override string Tab => "Maps";
    public override string Help => "Drag over the model to paint the weight map (shown in colour: blue 0, red 1).";
    public override string Summary => "A brush that paints a weight map (Map, made if new; the current one if empty): Add, Subtract, Set (toward Value) or Smooth (toward the neighbours' weights), by Strength at its centre fading over Radius. Run from a script it lays one dab at Center. With symmetry on the mirrored dab paints the map of the opposite name (\"Left Arm\" and \"Right Arm\") where there is one. Shows the map in the shaded views.";

    [Param("Map", Description = "The weight map to paint (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Mode", Description = "Add, Subtract, Set (toward Value) or Smooth.")] public WeightPaintMode Mode { get; set; } = WeightPaintMode.Add;
    [Param("Value", Min = 0, Max = 1, Step = 0.05, Description = "With Set: the weight it paints toward.")] public double Value { get; set; } = 1;

    protected override void DabAt(int layer, Mesh mesh, IReadOnlySet<int>? allowed, Vec3 at, double pressure, bool mirrored)
    {
        var map = MapChoice.Weight(Doc, mesh, Map);
        if (mirrored && MapNames.Opposite(map.Name) is { } o && mesh.FindPointMap(o, PointMapKind.Weight) is { } twin) map = twin;
        Doc.ShownPointMap = Doc.CurrentWeightMap;
        WeightMaps.Dab(mesh, map, Brushes.Under(mesh, allowed, at, Radius, Falloff), Mode, Strength * pressure, Value);
    }
}

/// <summary>Set Weight: gives the selected points a weight in a map.</summary>
public sealed class SetWeightTool : Tool
{
    public override string Name => "Set Weight";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Gives the selected points (every point, with none selected) Value in the weight map Map (made if new; the current one if empty). A weight of 0 takes a point out of the map.";

    [Param("Map", Description = "The weight map (made if new); empty for the current one.")] public string Map { get; set; } = "";
    [Param("Value", Min = 0, Max = 1, Step = 0.05)] public double Value { get; set; } = 1;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            var map = MapChoice.Weight(Doc, mesh, Map);
            foreach (int p in points) map.SetWeight(p, Value);
            mesh.Touch();
        }
        Doc.ShownPointMap = Doc.CurrentWeightMap;
    }
}

/// <summary>Smooth Weights: evens out a weight map over the selection.</summary>
public sealed class SmoothWeightsTool : Tool
{
    public override string Name => "Smooth Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Eases each selected point's weight in Map (the current weight map if empty) toward its neighbours' average, Iterations times at Strength, softening hard edges between bones' areas.";

    [Param("Map", Description = "The weight map; empty for the current one.")] public string Map { get; set; } = "";
    [Param("Strength", Min = 0, Max = 1, Step = 0.05)] public double Strength { get; set; } = 0.5;
    [Param("Iterations", Min = 1, Max = 100, Step = 1)] public int Iterations { get; set; } = 3;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Smooth(mesh, MapChoice.Weight(Doc, mesh, Map), points, Strength, Iterations);
            mesh.Touch();
        }
    }
}

/// <summary>Normalize Weights: makes the weight maps add to 1 at every selected point.</summary>
public sealed class NormalizeWeightsTool : Tool
{
    public override string Name => "Normalize Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Summary => "Scales the weights at each selected point (every point, with none selected) so the weight maps named in Maps (comma-separated; every weight map if empty) add to 1 there, as bones expect. Points in none of them are left alone.";

    [Param("Maps", Description = "The weight maps, comma-separated; empty for all of them.")] public string Maps { get; set; } = "";

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Normalize(MapChoice.Weights(mesh, Maps), points);
            mesh.Touch();
        }
    }
}

/// <summary>Mirror Weights: copies one side's weights onto the other.</summary>
public sealed class MirrorWeightsTool : Tool
{
    public override string Name => "Mirror Weights";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Copies the weights of the selected points (every point, with none selected) on the positive side of Axis (or the negative, with From positive off) to their mirror partners: into the same map, or into the map of the opposite name where there is one (\"Left Arm\" into \"Right Arm\", \"Arm.L\" into \"Arm.R\"). Maps names the weight maps (comma-separated; all if empty).";

    [Param("Maps", Description = "The weight maps, comma-separated; empty for all of them.")] public string Maps { get; set; } = "";
    [Param("Axis")] public Axis Axis { get; set; } = Axis.X;
    [Param("From positive", Description = "Copy from the positive side to the negative (off: the other way).")] public bool FromPositive { get; set; } = true;

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
        {
            WeightMaps.Mirror(mesh, MapChoice.Weights(mesh, Maps), Axis, FromPositive, points);
            mesh.Touch();
        }
    }
}

/// <summary>Weights from Skelegons: an automatic rig's weight maps.</summary>
public sealed class WeightsFromSkelegonsTool : Tool
{
    public override string Name => "Weights from Skelegons";
    public override string Tab => "Maps";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Makes a weight map for every skelegon in the foreground (2-point polygons on a surface named Skelegon…), named as the bone Layout makes from it, so each bone follows its own map. Each point (every point, or the selected ones) is shared among the skelegons by 1 / distance^Falloff, normalised to add to 1, with shares under Threshold dropped. Paint fixes afterwards with Paint Weights.";

    [Param("Falloff", Min = 1, Max = 16, Step = 0.5, Description = "How fast a skelegon's pull fades with distance: higher keeps each bone's area tighter.")] public double FalloffPower { get; set; } = 4;
    [Param("Threshold", Min = 0, Max = 0.5, Step = 0.01, Description = "Shares below this are dropped and the rest made to add to 1 again.")] public double Threshold { get; set; } = 0.01;

    protected override void Apply()
    {
        int made = 0;
        foreach (var (_, mesh, points) in ActivePoints())
        {
            if (SkelegonNames.Of(mesh, Doc.Surfaces).Count == 0) continue;
            var names = WeightMaps.FromSkelegons(mesh, Doc.Surfaces, FalloffPower, Threshold, Doc.Selection.Any ? points : null);
            made += names.Count;
            if (names.Count > 0) { Doc.CurrentWeightMap = names[0]; Doc.ShownPointMap = names[0]; }
        }
        if (made == 0) throw new InvalidOperationException("Weights from Skelegons needs skelegons in a foreground layer: 2-point polygons on a surface named Skelegon… (Points, then Make Polygon with two points, on a Skelegon surface).");
    }
}
