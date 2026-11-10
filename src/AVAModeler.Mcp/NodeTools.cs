using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>Surfaces' node graphs. Each change is one undo step; a graph that doesn't check out changes nothing.</summary>
public sealed partial class ModelerTools
{
    [McpServerTool(Name = "set_surface_nodes", Destructive = true)]
    [Description("Gives a surface a node graph (or with off true takes it away): nodes whose outputs feed other nodes' inputs, ending in one output node; what's linked into the output's inputs (color, diffuse, specular, luminosity, reflection, transparency, bump) takes the place of the surface's own setting there in renders, after its texture layers (the views show the surface's own settings). nodes is a list of objects: {\"id\": a whole number of your choosing, \"type\": one of output, color, value, texture, noise, gradient, mix, math, position, incidence, slope, split, combine, \"option\": the mix mode, sum, texture or space, \"settings\": {name: number}, \"inputs\": {input: a number, a colour [r, g, b] 0-255, or \"<id>.<output>\" to link another node's output}, \"stops\": [[at 0-1, [r, g, b]], ...] for a gradient}. A colour going into a value input gives its average; a value into a colour input its grey. Kinds (inputs → outputs; options; settings): output (Color, Diffuse, Specular, Luminosity, Reflection, Transparency, Bump: a height 0-1 tilting the shading; setting Bump Height in metres, default 0.01); color (Color → Color); value (Value → Value); texture (→ Value 0-1; option one of LightWave's procedurals: Checkerboard, Grid, Dots, Fractal Noise, Marble, Wood, Ripples, Underwater, Crumple, Turbulence, Value, Bump Array; settings Size (metres a pattern unit, default 1), World (1 world coordinates, 0 the object's), and the procedural's own, as set_procedural lists them); noise (→ Value 0-1; settings Scale metres default 1, Octaves 1-8 default 4, Roughness 0-1 default 0.5, World); gradient (Value → Color, Alpha; stops); mix (A, B colours, Factor 0-1 → Color; option Mix, Add, Multiply, Subtract, Screen, Difference, Lighten, Darken); math (A, B → Value; option Add, Subtract, Multiply, Divide, Power, Minimum, Maximum, Invert (1 − A), Absolute, Sine, Step (1 where A ≥ B), Clamp (A in 0-1), Smooth Step (0 to 1 as A goes 0 to B)); position (→ X, Y, Z metres; option Object or World); incidence (→ Value: 1 facing the eye, 0 edge on); slope (→ Slope 0 flat to 1 a wall, Up 1 facing up); split (Color → R, G, B); combine (R, G, B → Color). Names are matched ignoring case. Kept in the object file (LWOB) with the surface. Returns the surface with its graph.")]
    public Task<string> SetSurfaceNodes(
        [Description("The surface's name (it must exist: set_surface makes one).")] string surface,
        [Description("The nodes, as described above; one of type output.")] JsonElement[]? nodes = null,
        [Description("Take the surface's node graph away.")] bool off = false,
        CancellationToken cancel = default) =>
        Do($"set the {surface} surface's nodes", c =>
        {
            var doc = c.Document;
            var s = doc.Surfaces.FirstOrDefault(x => x.Name == surface)
                ?? throw new ArgumentException($"There's no surface \"{surface}\": the surfaces are {string.Join(", ", doc.Surfaces.Select(x => x.Name))} (set_surface makes one).");
            SurfaceNodeGraph? graph = null;
            if (!off)
            {
                if (nodes is not { Length: > 0 }) throw new ArgumentException("Give the nodes (a list with one of type output), or off true.");
                graph = NodeGraphJson.Read(nodes);
            }
            var before = Census.Of(doc);
            doc.Edit($"Surface {surface} Nodes", () => s.Nodes = graph, []);
            var result = Census.Changes(doc, before, $"Surface {surface} Nodes");
            result["surface"] = Census.Surface(doc, s);
            return result;
        }, cancel);
}

/// <summary>Node graphs as agents write and read them: ids, type names, options, settings, inputs (numbers, colours 0-255 or "id.output" links) and stops.</summary>
internal static class NodeGraphJson
{
    public static SurfaceNodeGraph Read(JsonElement[] nodes)
    {
        var graph = new SurfaceNodeGraph();
        var links = new List<(SurfaceNode To, string Input, string From)>();
        int column = 0;
        foreach (var e in nodes)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each node is an object: {\"id\": 1, \"type\": \"output\", ...}.");
            int id = e.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.Number && idE.TryGetInt32(out int i) && i > 0
                ? i : throw new ArgumentException("Each node needs an \"id\", a whole number above 0.");
            string typeName = e.TryGetProperty("type", out var tE) && tE.ValueKind == JsonValueKind.String ? tE.GetString()! : throw new ArgumentException($"Node {id} needs a \"type\".");
            var kind = SurfaceNodes.Find(typeName) ?? throw new ArgumentException($"Node {id}'s type \"{typeName}\" isn't one of {string.Join(", ", Enum.GetNames<NodeKind>()).ToLowerInvariant()}.");
            var type = SurfaceNodes.Of(kind);
            var n = new SurfaceNode { Id = id, Kind = kind, X = 20 + 200 * (column % 4), Y = 20 + 140 * (column / 4) };
            column++;
            if (kind == NodeKind.Output) (n.X, n.Y) = (820, 40);
            if (e.TryGetProperty("x", out var xE) && xE.ValueKind == JsonValueKind.Number) n.X = xE.GetDouble();
            if (e.TryGetProperty("y", out var yE) && yE.ValueKind == JsonValueKind.Number) n.Y = yE.GetDouble();
            if (e.TryGetProperty("option", out var oE) && oE.ValueKind == JsonValueKind.String) n.Option = oE.GetString()!;
            else n.Option = type.Options.FirstOrDefault() ?? "";
            if (e.TryGetProperty("settings", out var sE))
            {
                if (sE.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Node {id}'s settings are an object of numbers, {{\"Scale\": 0.5}}.");
                foreach (var p in sE.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Number) throw new ArgumentException($"Node {id}'s {p.Name} must be a number.");
                    n.Numbers[SettingName(n, p.Name)] = p.Value.GetDouble();
                }
            }
            if (e.TryGetProperty("inputs", out var iE))
            {
                if (iE.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Node {id}'s inputs are an object: {{\"Factor\": 0.5, \"A\": [255, 0, 0], \"B\": \"3.Color\"}}.");
                foreach (var p in iE.EnumerateObject())
                {
                    var port = type.Input(p.Name) ?? throw new ArgumentException($"A {kind} node has no input \"{p.Name}\"; it has {Names(type.Inputs)}.");
                    switch (p.Value.ValueKind)
                    {
                        case JsonValueKind.String: links.Add((n, port.Name, p.Value.GetString()!)); break;
                        case JsonValueKind.Number when port.Type == PortType.Value: n.Numbers[port.Name] = p.Value.GetDouble(); break;
                        case JsonValueKind.Number:
                        {
                            double v = p.Value.GetDouble();
                            n.Colors[port.Name] = Json.Colour([v, v, v], $"node {id}'s {port.Name}");
                            break;
                        }
                        case JsonValueKind.Array when port.Type == PortType.Color:
                            n.Colors[port.Name] = Json.Colour([.. p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN)], $"node {id}'s {port.Name}");
                            break;
                        default:
                            throw new ArgumentException($"Node {id}'s {port.Name} is {(port.Type == PortType.Color ? "a colour [r, g, b] 0-255" : "a number")} or a link \"<id>.<output>\".");
                    }
                }
            }
            if (e.TryGetProperty("stops", out var stE))
            {
                if (kind != NodeKind.Gradient) throw new ArgumentException($"Only a gradient has stops (node {id} is a {kind}).");
                n.Stops = [.. stE.EnumerateArray().Select(x =>
                {
                    if (x.ValueKind != JsonValueKind.Array || x.GetArrayLength() != 2 || x[0].ValueKind != JsonValueKind.Number || x[1].ValueKind != JsonValueKind.Array)
                        throw new ArgumentException($"Node {id}'s stops are [[at, [r, g, b]], ...].");
                    return new GradientStop(x[0].GetDouble(), Json.Colour([.. x[1].EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Number ? c.GetDouble() : double.NaN)], $"node {id}'s stop"));
                })];
            }
            else if (kind == NodeKind.Gradient) n.Stops = [new(0, new Rgb(0, 0, 0)), new(1, new Rgb(255, 255, 255))];
            if (graph.Find(id) != null) throw new ArgumentException($"Two nodes have the id {id}.");
            graph.Nodes.Add(n);
        }
        if (graph.Output == null) throw new ArgumentException("A graph needs a node of type output: what's linked into it changes the surface.");
        foreach (var (to, input, from) in links)
        {
            int dot = from.LastIndexOf('.');
            if (dot <= 0 || !int.TryParse(from[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromId))
                throw new ArgumentException($"Node {to.Id}'s {input} links to \"{from}\": a link is \"<id>.<output>\", such as \"3.Value\".");
            graph.Link(fromId, from[(dot + 1)..], to.Id, input);
        }
        return graph.Checked();
    }

    /// <summary>A setting's name as the node kind spells it (a texture's procedural's own settings included).</summary>
    private static string SettingName(SurfaceNode n, string name)
    {
        if (n.Type.Setting(name) is { } s) return s.Name;
        if (n.Kind == NodeKind.Texture && Procedurals.Find(n.Option) is { } proc && proc.Find(name) is { } p) return p.Name;
        if (n.Type.Input(name) is { Type: PortType.Value } i) return i.Name;
        var names = n.Type.Settings.Select(x => x.Name).ToList();
        if (n.Kind == NodeKind.Texture && Procedurals.Find(n.Option) is { } t) names.AddRange(t.Parameters.Select(x => x.Name));
        throw new ArgumentException($"A {n.Kind} node{(n.Kind == NodeKind.Texture ? $" ({n.Option})" : "")} has no setting \"{name}\"; it has {(names.Count == 0 ? "none" : string.Join(", ", names))}.");
    }

    private static string Names(IEnumerable<NodePort> ports) => ports.Any() ? string.Join(", ", ports.Select(p => p.Name)) : "none";

    /// <summary>A graph as agents read it, in the shape <see cref="Read"/> takes.</summary>
    public static JsonArray Write(SurfaceNodeGraph g) => new([.. g.Nodes.Select(n =>
    {
        var t = n.Type;
        var o = new JsonObject { ["id"] = n.Id, ["type"] = n.Kind.ToString().ToLowerInvariant() };
        if (n.Option.Length > 0) o["option"] = n.Option;
        var settings = new JsonObject();
        foreach (var (k, v) in n.Numbers.Where(kv => t.Input(kv.Key) == null)) settings[k] = Census.Round(v);
        if (settings.Count > 0) o["settings"] = settings;
        var inputs = new JsonObject();
        foreach (var p in t.Inputs)
        {
            if (n.Links.TryGetValue(p.Name, out var l)) inputs[p.Name] = $"{l.Node}.{l.Output}";
            else if (p.Type == PortType.Color && n.Colors.TryGetValue(p.Name, out var c)) inputs[p.Name] = new JsonArray(c.R, c.G, c.B);
            else if (p.Type == PortType.Value && n.Numbers.TryGetValue(p.Name, out double v)) inputs[p.Name] = Census.Round(v);
        }
        if (inputs.Count > 0) o["inputs"] = inputs;
        if (n.Kind == NodeKind.Gradient) o["stops"] = new JsonArray([.. n.Stops.Select(s => (JsonNode)new JsonArray(Census.Round(s.At), new JsonArray(s.Color.R, s.Color.G, s.Color.B)))]);
        return (JsonNode)o;
    })]);
}
