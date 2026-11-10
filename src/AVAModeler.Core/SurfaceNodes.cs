using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAModeler.Core;

/// <summary>The kinds of node a surface's node graph is built from.</summary>
public enum NodeKind
{
    /// <summary>The surface itself: what's linked into it takes the place of the surface's own settings.</summary>
    Output,
    Color,
    Value,
    /// <summary>One of LightWave's procedural textures (<see cref="Procedurals"/>), its amount 0 to 1.</summary>
    Texture,
    /// <summary>Fractal noise, 0 to 1.</summary>
    Noise,
    /// <summary>A value turned into a colour along a ramp of colours.</summary>
    Gradient,
    /// <summary>Two colours mixed, added, multiplied and so on by a factor.</summary>
    Mix,
    /// <summary>Sums on two values.</summary>
    Math,
    /// <summary>Where the point is (the object's coordinates or the world's), as X, Y and Z.</summary>
    Position,
    /// <summary>How squarely the surface faces the eye: 1 head on, 0 edge on.</summary>
    Incidence,
    /// <summary>How steep the surface is (0 flat, 1 a wall), and how much it faces up.</summary>
    Slope,
    /// <summary>A colour's red, green and blue as values.</summary>
    Split,
    /// <summary>Red, green and blue values as a colour.</summary>
    Combine,
}

/// <summary>What a port carries: a value, or a colour (0 to 1 a channel). One goes into the other: a colour's average, a value's grey.</summary>
public enum PortType { Value, Color }

/// <summary>An input or output of a kind of node, with the default an unlinked input has.</summary>
public sealed record NodePort(string Name, PortType Type, double Default = 0, Rgb? DefaultColor = null, string Description = "");

/// <summary>A number a node is set with that isn't an input (a texture's size, the noise's octaves), with its limits.</summary>
public sealed record NodeSetting(string Name, double Default, double Min, double Max, string Description);

/// <summary>A kind of node: what it does, its inputs, outputs, settings and options (a mix's mode, a sum, a texture).</summary>
public sealed record NodeType(NodeKind Kind, string Summary, IReadOnlyList<NodePort> Inputs, IReadOnlyList<NodePort> Outputs,
    IReadOnlyList<NodeSetting> Settings, IReadOnlyList<string> Options)
{
    public string Name => Kind.ToString();
    // Names match ignoring case, spaces, underscores and dashes ("Bump Height", "bump_height").
    public NodePort? Input(string name) => Inputs.FirstOrDefault(p => Same(p.Name, name));
    public NodePort? Output(string name) => Outputs.FirstOrDefault(p => Same(p.Name, name));
    public NodeSetting? Setting(string name) => Settings.FirstOrDefault(p => Same(p.Name, name));

    public static bool Same(string a, string b) => Squash(a) == Squash(b);
    private static string Squash(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>Where an input takes its value from: another node's output.</summary>
public sealed record NodeLink(int Node, string Output);

/// <summary>A colour on a gradient's ramp, where the value is <see cref="At"/> (0 to 1).</summary>
public sealed record GradientStop(double At, Rgb Color);

/// <summary>
/// One node of a graph: its kind, where it sits in the node editor, its option, the constants its unlinked inputs
/// take and its settings (by name), its colours (a colour input's constant), its links and, for a gradient, its stops.
/// </summary>
public sealed class SurfaceNode
{
    public int Id { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NodeKind Kind { get; set; }
    /// <summary>Where it sits in the node editor (pixels, from the editor's top left).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>The mix's mode, the sum, the texture's procedural or the position's space; empty for the first.</summary>
    public string Option { get; set; } = "";
    /// <summary>Unlinked value inputs' constants and the settings, by name; a missing one is its default.</summary>
    public Dictionary<string, double> Numbers { get; set; } = [];
    /// <summary>Unlinked colour inputs' constants, by name; a missing one is its default.</summary>
    public Dictionary<string, Rgb> Colors { get; set; } = [];
    /// <summary>The inputs linked to other nodes' outputs, by input name.</summary>
    public Dictionary<string, NodeLink> Links { get; set; } = [];
    /// <summary>A gradient's colours along its ramp.</summary>
    public List<GradientStop> Stops { get; set; } = [];

    public SurfaceNode Clone() => new()
    {
        Id = Id, Kind = Kind, X = X, Y = Y, Option = Option, Numbers = new(Numbers), Colors = new(Colors), Links = new(Links), Stops = [.. Stops],
    };

    public NodeType Type => SurfaceNodes.Of(Kind);

    /// <summary>An unlinked value input's constant, or a setting.</summary>
    public double Number(string name) =>
        Numbers.TryGetValue(name, out double v) ? v : Type.Input(name)?.Default ?? Type.Setting(name)?.Default ?? ProceduralDefault(name);

    /// <summary>An unlinked colour input's constant.</summary>
    public Rgb ColorOf(string name) => Colors.TryGetValue(name, out var c) ? c : Type.Input(name)?.DefaultColor ?? Rgb.Grey;

    private double ProceduralDefault(string name) =>
        Kind == NodeKind.Texture && Procedurals.Find(Option) is { } t && t.Find(name) is { } p ? p.Default : 0;
}

/// <summary>
/// A surface's node graph: nodes whose outputs feed others' inputs, ending in one <see cref="NodeKind.Output"/> node.
/// What's linked into the output takes the place of the surface's own colour, diffuse, specular, luminosity, reflection
/// or transparency where a ray hits it, after its texture layers; a linked Bump tilts the shading normal by the height
/// it gives (0 to 1, times the output's Bump Height in metres). Only the ray tracer draws them: the views show the
/// surface's own settings. Kept in object files as a sub-chunk of the surface (<c>AVND</c>, one line of JSON).
/// </summary>
public sealed class SurfaceNodeGraph
{
    public List<SurfaceNode> Nodes { get; set; } = [];

    public SurfaceNodeGraph Clone() => new() { Nodes = Nodes.Select(n => n.Clone()).ToList() };

    public SurfaceNode? Find(int id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The output node, or null if there's none yet.</summary>
    public SurfaceNode? Output => Nodes.FirstOrDefault(n => n.Kind == NodeKind.Output);

    /// <summary>A new graph with just the output node, ready to link into.</summary>
    public static SurfaceNodeGraph New() => new() { Nodes = [new SurfaceNode { Id = 1, Kind = NodeKind.Output, X = 420, Y = 40 }] };

    /// <summary>The next free node number.</summary>
    public int NextId => Nodes.Count == 0 ? 1 : Nodes.Max(n => n.Id) + 1;

    /// <summary>Adds a node of a kind (its option the first), returning it.</summary>
    public SurfaceNode Add(NodeKind kind, double x = 0, double y = 0, string? option = null)
    {
        if (kind == NodeKind.Output && Output != null) throw new ArgumentException("A graph has one output node already.");
        var n = new SurfaceNode { Id = NextId, Kind = kind, X = x, Y = y, Option = option ?? SurfaceNodes.Of(kind).Options.FirstOrDefault() ?? "" };
        if (kind == NodeKind.Gradient) n.Stops = [new(0, new Rgb(0, 0, 0)), new(1, new Rgb(255, 255, 255))];
        Nodes.Add(n);
        return n;
    }

    /// <summary>Takes a node away, and every link to it.</summary>
    public void Remove(int id)
    {
        Nodes.RemoveAll(n => n.Id == id);
        foreach (var n in Nodes)
            foreach (var k in n.Links.Where(l => l.Value.Node == id).Select(l => l.Key).ToList()) n.Links.Remove(k);
    }

    /// <summary>
    /// Links an output to an input, checked: both exist, and the link doesn't make a loop (a node can't feed itself,
    /// however far round).
    /// </summary>
    public void Link(int fromNode, string output, int toNode, string input)
    {
        var from = Find(fromNode) ?? throw new ArgumentException($"There's no node {fromNode}.");
        var to = Find(toNode) ?? throw new ArgumentException($"There's no node {toNode}.");
        var o = from.Type.Output(output) ?? throw new ArgumentException($"A {from.Kind} node has no output \"{output}\"; it has {Names(from.Type.Outputs)}.");
        var i = to.Type.Input(input) ?? throw new ArgumentException($"A {to.Kind} node has no input \"{input}\"; it has {Names(to.Type.Inputs)}.");
        if (Feeds(toNode, fromNode)) throw new ArgumentException($"Linking node {fromNode} into node {toNode} would make a loop.");
        to.Links[i.Name] = new NodeLink(fromNode, o.Name);
    }

    /// <summary>Whether <paramref name="node"/> feeds <paramref name="target"/>, however far round (or is it).</summary>
    private bool Feeds(int node, int target)
    {
        if (node == target) return true;
        // Walking upstream from the target, through what feeds its inputs.
        var seen = new HashSet<int>();
        var todo = new Stack<int>([target]);
        while (todo.Count > 0)
        {
            int id = todo.Pop();
            if (!seen.Add(id)) continue;
            if (id == node) return true;
            if (Find(id) is { } n) foreach (var l in n.Links.Values) todo.Push(l.Node);
        }
        return false;
    }

    private static string Names(IEnumerable<NodePort> ports) => ports.Any() ? string.Join(", ", ports.Select(p => p.Name)) : "none";

    /// <summary>
    /// Checks the graph: unique node numbers, one output at most, known kinds, options, inputs, outputs and links, no
    /// loops, settings within their limits and finite constants. Throws an <see cref="ArgumentException"/> saying what's wrong.
    /// </summary>
    public SurfaceNodeGraph Checked()
    {
        if (Nodes.Select(n => n.Id).Distinct().Count() != Nodes.Count) throw new ArgumentException("Two nodes have the same number.");
        if (Nodes.Count(n => n.Kind == NodeKind.Output) > 1) throw new ArgumentException("A graph has one output node at most.");
        if (Nodes.Count > SurfaceNodes.MaxNodes) throw new ArgumentException($"A graph has at most {SurfaceNodes.MaxNodes} nodes.");
        foreach (var n in Nodes)
        {
            if (!Enum.IsDefined(n.Kind)) throw new ArgumentException($"Node {n.Id}'s kind isn't one of {string.Join(", ", Enum.GetNames<NodeKind>())}.");
            var t = n.Type;
            if (n.Kind == NodeKind.Texture)
            {
                var proc = Procedurals.Find(n.Option) ?? throw new ArgumentException($"Node {n.Id}'s texture \"{n.Option}\" isn't one of {Procedurals.Names}.");
                n.Option = proc.Name;
                foreach (var (k, v) in n.Numbers)
                    if (t.Setting(k) == null)
                    {
                        var p = proc.Find(k) ?? throw new ArgumentException($"Node {n.Id} ({proc.Name}) has no setting \"{k}\"; it has {string.Join(", ", t.Settings.Select(s => s.Name).Concat(proc.Parameters.Select(q => q.Name)))}.");
                        if (!double.IsFinite(v) || v < p.Min || v > p.Max) throw new ArgumentException($"Node {n.Id}'s {p.Name} must be from {p.Min} to {p.Max} (it's {v}).");
                    }
            }
            else if (t.Options.Count > 0)
            {
                var o = t.Options.FirstOrDefault(x => x.Equals(n.Option, StringComparison.OrdinalIgnoreCase))
                    ?? (n.Option.Length == 0 ? t.Options[0] : throw new ArgumentException($"Node {n.Id} ({n.Kind}) is one of {string.Join(", ", t.Options)}, not \"{n.Option}\"."));
                n.Option = o;
            }
            foreach (var (k, v) in n.Numbers)
            {
                if (!double.IsFinite(v)) throw new ArgumentException($"Node {n.Id}'s {k} must be a number.");
                if (t.Setting(k) is { } s && (v < s.Min || v > s.Max)) throw new ArgumentException($"Node {n.Id}'s {s.Name} must be from {s.Min} to {s.Max} (it's {v}).");
                if (t.Setting(k) == null && t.Input(k) == null && n.Kind != NodeKind.Texture)
                    throw new ArgumentException($"A {n.Kind} node has no input or setting \"{k}\".");
            }
            foreach (var k in n.Colors.Keys)
                if (t.Input(k) is not { Type: PortType.Color }) throw new ArgumentException($"A {n.Kind} node has no colour input \"{k}\".");
            foreach (var (input, link) in n.Links)
            {
                if (t.Input(input) == null) throw new ArgumentException($"A {n.Kind} node has no input \"{input}\"; it has {Names(t.Inputs)}.");
                var from = Find(link.Node) ?? throw new ArgumentException($"Node {n.Id}'s {input} is linked to node {link.Node}, which isn't there.");
                if (from.Type.Output(link.Output) == null) throw new ArgumentException($"Node {n.Id}'s {input} is linked to node {link.Node}'s \"{link.Output}\", which a {from.Kind} node hasn't got.");
            }
            if (n.Kind == NodeKind.Gradient)
            {
                if (n.Stops.Count == 0) throw new ArgumentException($"Node {n.Id}'s gradient needs a colour at least.");
                if (n.Stops.Any(s => !double.IsFinite(s.At))) throw new ArgumentException($"Node {n.Id}'s gradient stops must be at numbers.");
                n.Stops = [.. n.Stops.OrderBy(s => s.At)];
            }
        }
        _ = Order();
        return this;
    }

    /// <summary>The nodes, every node after those feeding it; throws if there's a loop.</summary>
    public List<SurfaceNode> Order()
    {
        var order = new List<SurfaceNode>();
        var state = new Dictionary<int, int>();
        void Visit(SurfaceNode n)
        {
            if (state.TryGetValue(n.Id, out int s))
            {
                if (s == 1) throw new ArgumentException($"Node {n.Id} feeds itself through its links: a graph can't loop.");
                return;
            }
            state[n.Id] = 1;
            foreach (var l in n.Links.Values) if (Find(l.Node) is { } from) Visit(from);
            state[n.Id] = 2;
            order.Add(n);
        }
        foreach (var n in Nodes) Visit(n);
        return order;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The graph as one line of JSON (object files keep it so).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A graph from <see cref="ToJson"/>'s line, checked.</summary>
    public static SurfaceNodeGraph FromJson(string json) =>
        (JsonSerializer.Deserialize<SurfaceNodeGraph>(json, Json) ?? throw new ArgumentException("A node graph's JSON was empty.")).Checked();
}

/// <summary>Where a ray hit, for a node graph: the point in the object's and the world's coordinates, the shading normal (world) and the way to the eye.</summary>
public readonly record struct NodeContext(Vec3 Local, Vec3 World, Vec3 Normal, Vec3 ToEye);

/// <summary>What a node graph gives a surface at a point: each of its settings, or null where the output's input isn't linked.</summary>
public readonly record struct NodeLook(Vec3? Color, double? Diffuse, double? Specular, double? Luminosity, double? Reflection, double? Transparency);

/// <summary>The catalog of node kinds, and a graph compiled to evaluate quickly.</summary>
public static class SurfaceNodes
{
    public const int MaxNodes = 200;

    private static NodePort V(string name, double def = 0, string description = "") => new(name, PortType.Value, def, null, description);
    private static NodePort C(string name, Rgb def, string description = "") => new(name, PortType.Color, 0, def, description);
    private static NodeSetting S(string name, double def, double min, double max, string description) => new(name, def, min, max, description);

    /// <summary>The mix modes, the sums and the position's spaces, as the Mix, Math and Position nodes' options.</summary>
    public static readonly string[] MixModes = ["Mix", "Add", "Multiply", "Subtract", "Screen", "Difference", "Lighten", "Darken"];
    public static readonly string[] MathOps = ["Add", "Subtract", "Multiply", "Divide", "Power", "Minimum", "Maximum", "Invert", "Absolute", "Sine", "Step", "Clamp", "Smooth Step"];
    public static readonly string[] Spaces = ["Object", "World"];

    public static readonly IReadOnlyList<NodeType> All =
    [
        new(NodeKind.Output, "The surface: what's linked in takes the place of its own setting there (Bump tilts its shading by a height, 0 to 1, times Bump Height).",
            [C("Color", Rgb.Grey), V("Diffuse", 1), V("Specular"), V("Luminosity"), V("Reflection"), V("Transparency"), V("Bump")],
            [], [S("Bump Height", 0.01, 0, 1, "How far (m) a bump height of 1 stands out.")], []),
        new(NodeKind.Color, "A colour.", [C("Color", Rgb.Grey)], [C("Color", Rgb.Grey)], [], []),
        new(NodeKind.Value, "A number.", [V("Value", 0.5)], [V("Value")], [], []),
        new(NodeKind.Texture, "One of LightWave's procedural textures (the option names it), its amount 0 to 1, one pattern unit every Size metres.",
            [], [V("Value")],
            [S("Size", 1, 1e-6, 1e6, "Metres a pattern unit."), S("World", 0, 0, 1, "1 to lay it in the world's coordinates (it stays put as the object moves), 0 the object's.")],
            [.. Procedurals.All.Select(t => t.Name)]),
        new(NodeKind.Noise, "Fractal noise, 0 to 1: drifts and patches about Scale metres across, rougher with more octaves and roughness.",
            [], [V("Value")],
            [S("Scale", 1, 1e-6, 1e6, "Metres across its broadest patches."), S("Octaves", 4, 1, 8, "Layers of finer noise."),
             S("Roughness", 0.5, 0, 1, "How strong each finer layer is."), S("World", 0, 0, 1, "1 for the world's coordinates, 0 the object's.")],
            []),
        new(NodeKind.Gradient, "A value turned into a colour along a ramp: its stops' colours at their places (0 to 1), blended between.",
            [V("Value", 0.5)], [C("Color", Rgb.Grey), V("Alpha")], [], []),
        new(NodeKind.Mix, "Colours A and B mixed by Factor (0 A, 1 B), or B added, multiplied, taken away and so on over A by Factor (the option).",
            [C("A", new Rgb(0, 0, 0)), C("B", new Rgb(255, 255, 255)), V("Factor", 0.5)], [C("Color", Rgb.Grey)], [], MixModes),
        new(NodeKind.Math, "A sum on values A and B (the option): Invert is 1 − A, Step is 1 where A ≥ B, Clamp keeps A in 0 to 1, Smooth Step eases from 0 to 1 as A goes from 0 to B.",
            [V("A"), V("B")], [V("Value")], [], MathOps),
        new(NodeKind.Position, "Where the point is, in metres, in the object's or the world's coordinates (the option).",
            [], [V("X"), V("Y"), V("Z")], [], Spaces),
        new(NodeKind.Incidence, "How squarely the surface faces the eye: 1 head on, 0 edge on (for a rim, or a fresnel's edge).",
            [], [V("Value")], [], []),
        new(NodeKind.Slope, "How steep the surface is in the world, 0 flat to 1 a wall (Slope), and how far it faces up, 1 facing up to 0 sideways or down (Up).",
            [], [V("Slope"), V("Up")], [], []),
        new(NodeKind.Split, "A colour's red, green and blue as values, 0 to 1.", [C("Color", Rgb.Grey)], [V("R"), V("G"), V("B")], [], []),
        new(NodeKind.Combine, "Values red, green and blue (0 to 1) as a colour.", [V("R"), V("G"), V("B")], [C("Color", Rgb.Grey)], [], []),
    ];

    public static NodeType Of(NodeKind kind) => All.First(t => t.Kind == kind);

    /// <summary>A node kind by name (case ignored), or null.</summary>
    public static NodeKind? Find(string name) => Enum.TryParse<NodeKind>(name, true, out var k) && Enum.IsDefined(k) && !int.TryParse(name, out _) ? k : null;

    /// <summary>The graph ready to evaluate, or null if it has no output node or nothing linked into it.</summary>
    public static NodeProgram? Compile(SurfaceNodeGraph? graph)
    {
        if (graph?.Output is not { } output || output.Links.Count == 0) return null;
        return new NodeProgram(graph);
    }
}

/// <summary>
/// A node graph compiled to evaluate at many points: its nodes in order (only those the output needs), each output a
/// slot of a scratch array. Safe to use from many threads.
/// </summary>
public sealed class NodeProgram
{
    private abstract record Source;
    private sealed record Slot(int Index, PortType Type) : Source;
    private sealed record Constant(Vec3 Value, PortType Type) : Source;

    private sealed record Step(SurfaceNode Node, Source[] Inputs, int FirstSlot, ProceduralTexture? Texture);

    private readonly List<Step> _steps = [];
    private readonly int _slots;
    private readonly Source?[] _out = new Source?[7];
    private static readonly string[] OutputInputs = ["Color", "Diffuse", "Specular", "Luminosity", "Reflection", "Transparency", "Bump"];
    [ThreadStatic] private static Vec3[]? _scratch;

    /// <summary>Whether its Bump is linked.</summary>
    public bool HasBump => _out[6] != null;

    /// <summary>The output's Bump Height (m).</summary>
    public double BumpHeight { get; }

    internal NodeProgram(SurfaceNodeGraph graph)
    {
        var order = graph.Order();
        var output = graph.Output!;
        // Only the nodes the output needs, in order.
        var needed = new HashSet<int>();
        var todo = new Stack<int>([output.Id]);
        while (todo.Count > 0)
        {
            int id = todo.Pop();
            if (!needed.Add(id)) continue;
            if (graph.Find(id) is { } n) foreach (var l in n.Links.Values) todo.Push(l.Node);
        }
        var slotOf = new Dictionary<(int, string), Slot>();
        int next = 0;
        Source Input(SurfaceNode n, NodePort port)
        {
            if (n.Links.TryGetValue(port.Name, out var link) && slotOf.TryGetValue((link.Node, link.Output), out var s)) return s;
            return port.Type == PortType.Color ? new Constant(n.ColorOf(port.Name).ToVec(), PortType.Color) : new Constant(new Vec3(n.Number(port.Name), 0, 0), PortType.Value);
        }
        foreach (var n in order)
        {
            if (!needed.Contains(n.Id)) continue;
            var t = n.Type;
            if (n.Kind == NodeKind.Output)
            {
                for (int i = 0; i < OutputInputs.Length; i++)
                    if (n.Links.ContainsKey(OutputInputs[i])) _out[i] = Input(n, t.Input(OutputInputs[i])!);
                continue;
            }
            var inputs = t.Inputs.Select(p => Input(n, p)).ToArray();
            ProceduralTexture? texture = null;
            if (n.Kind == NodeKind.Texture && Procedurals.Find(n.Option) is { } proc)
            {
                double size = n.Number("Size");
                var layer = new TextureLayer { Procedural = proc.Name, Size = new Vec3(size, size, size), WorldCoordinates = n.Number("World") >= 0.5 };
                Procedurals.Apply(layer, proc);
                foreach (var p in proc.Parameters) layer.Parameters[Procedurals.SlotIndex(p.Slot)] = n.Number(p.Name);
                texture = new ProceduralTexture(layer, proc);
            }
            _steps.Add(new Step(n, inputs, next, texture));
            foreach (var o in t.Outputs) slotOf[(n.Id, o.Name)] = new Slot(next++, o.Type);
        }
        _slots = next;
        BumpHeight = output.Number("Bump Height");
    }

    private static Vec3 Color(Vec3[] s, Source src) => src switch
    {
        Slot { Type: PortType.Color } x => s[x.Index],
        Slot x => new Vec3(s[x.Index].X, s[x.Index].X, s[x.Index].X),
        Constant { Type: PortType.Color } c => c.Value,
        Constant c => new Vec3(c.Value.X, c.Value.X, c.Value.X),
        _ => Vec3.Zero,
    };

    private static double Value(Vec3[] s, Source src) => src switch
    {
        Slot { Type: PortType.Color } x => (s[x.Index].X + s[x.Index].Y + s[x.Index].Z) / 3,
        Slot x => s[x.Index].X,
        Constant { Type: PortType.Color } c => (c.Value.X + c.Value.Y + c.Value.Z) / 3,
        Constant c => c.Value.X,
        _ => 0,
    };

    private Vec3[] Run(in NodeContext ctx)
    {
        var s = _scratch is { } buffer && buffer.Length >= _slots ? buffer : _scratch = new Vec3[Math.Max(_slots, 16)];
        foreach (var step in _steps)
        {
            var n = step.Node;
            int o = step.FirstSlot;
            var i = step.Inputs;
            switch (n.Kind)
            {
                case NodeKind.Color: s[o] = Color(s, i[0]); break;
                case NodeKind.Value: s[o] = new Vec3(Value(s, i[0]), 0, 0); break;
                case NodeKind.Texture:
                {
                    var tex = step.Texture!;
                    s[o] = new Vec3(tex.Amount(tex.WorldCoordinates ? ctx.World : ctx.Local), 0, 0);
                    break;
                }
                case NodeKind.Noise:
                {
                    var p = (n.Number("World") >= 0.5 ? ctx.World : ctx.Local) / Math.Max(1e-9, n.Number("Scale"));
                    double v = Noise.Fractal(p, (int)Math.Clamp(Math.Round(n.Number("Octaves")), 1, 8), n.Number("Roughness"));
                    s[o] = new Vec3(Math.Clamp(0.5 + v, 0, 1), 0, 0);
                    break;
                }
                case NodeKind.Gradient:
                {
                    double v = Value(s, i[0]);
                    var stops = n.Stops;
                    Vec3 c;
                    if (stops.Count == 0) c = Vec3.Zero;
                    else if (v <= stops[0].At) c = stops[0].Color.ToVec();
                    else if (v >= stops[^1].At) c = stops[^1].Color.ToVec();
                    else
                    {
                        int k = 1;
                        while (k < stops.Count - 1 && stops[k].At < v) k++;
                        var (a0, b0) = (stops[k - 1], stops[k]);
                        double f = b0.At > a0.At ? (v - a0.At) / (b0.At - a0.At) : 1;
                        c = Vec3.Lerp(a0.Color.ToVec(), b0.Color.ToVec(), f);
                    }
                    s[o] = c;
                    s[o + 1] = new Vec3(Math.Clamp(v, 0, 1), 0, 0);
                    break;
                }
                case NodeKind.Mix:
                {
                    Vec3 a = Color(s, i[0]), b = Color(s, i[1]);
                    double f = Math.Clamp(Value(s, i[2]), 0, 1);
                    var over = n.Option switch
                    {
                        "Add" => a + b,
                        "Multiply" => new Vec3(a.X * b.X, a.Y * b.Y, a.Z * b.Z),
                        "Subtract" => a - b,
                        "Screen" => Vec3.One - new Vec3((1 - a.X) * (1 - b.X), (1 - a.Y) * (1 - b.Y), (1 - a.Z) * (1 - b.Z)),
                        "Difference" => new Vec3(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)),
                        "Lighten" => Vec3.Max(a, b),
                        "Darken" => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
                        _ => b,
                    };
                    var mixed = Vec3.Lerp(a, over, f);
                    s[o] = new Vec3(Math.Max(0, mixed.X), Math.Max(0, mixed.Y), Math.Max(0, mixed.Z));
                    break;
                }
                case NodeKind.Math:
                {
                    double a = Value(s, i[0]), b = Value(s, i[1]);
                    double r = n.Option switch
                    {
                        "Subtract" => a - b,
                        "Multiply" => a * b,
                        "Divide" => Math.Abs(b) < 1e-12 ? 0 : a / b,
                        "Power" => a < 0 && b != Math.Round(b) ? 0 : Math.Pow(a, b),
                        "Minimum" => Math.Min(a, b),
                        "Maximum" => Math.Max(a, b),
                        "Invert" => 1 - a,
                        "Absolute" => Math.Abs(a),
                        "Sine" => Math.Sin(a),
                        "Step" => a >= b ? 1 : 0,
                        "Clamp" => Math.Clamp(a, 0, 1),
                        "Smooth Step" => SmoothStep(b, a),
                        _ => a + b,
                    };
                    s[o] = new Vec3(double.IsFinite(r) ? r : 0, 0, 0);
                    break;
                }
                case NodeKind.Position:
                {
                    var p = n.Option == "World" ? ctx.World : ctx.Local;
                    s[o] = new Vec3(p.X, 0, 0);
                    s[o + 1] = new Vec3(p.Y, 0, 0);
                    s[o + 2] = new Vec3(p.Z, 0, 0);
                    break;
                }
                case NodeKind.Incidence:
                {
                    var e = ctx.ToEye.LengthSquared > 0 ? ctx.ToEye.Normalized() : ctx.Normal;
                    s[o] = new Vec3(Math.Clamp(Math.Abs(Vec3.Dot(ctx.Normal, e)), 0, 1), 0, 0);
                    break;
                }
                case NodeKind.Slope:
                    s[o] = new Vec3(1 - Math.Clamp(Math.Abs(ctx.Normal.Y), 0, 1), 0, 0);
                    s[o + 1] = new Vec3(Math.Clamp(ctx.Normal.Y, 0, 1), 0, 0);
                    break;
                case NodeKind.Split:
                {
                    var c = Color(s, i[0]);
                    s[o] = new Vec3(c.X, 0, 0);
                    s[o + 1] = new Vec3(c.Y, 0, 0);
                    s[o + 2] = new Vec3(c.Z, 0, 0);
                    break;
                }
                case NodeKind.Combine:
                    s[o] = new Vec3(Value(s, i[0]), Value(s, i[1]), Value(s, i[2]));
                    break;
            }
        }
        return s;
    }

    private static double SmoothStep(double edge, double x)
    {
        double t = edge <= 0 ? (x >= 0 ? 1 : 0) : Math.Clamp(x / edge, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The graph's settings for the surface at a point.</summary>
    public NodeLook Evaluate(in NodeContext ctx)
    {
        var s = Run(ctx);
        double? Val(int k) => _out[k] is { } src ? Value(s, src) : null;
        var color = _out[0] is { } c ? Color(s, c) : (Vec3?)null;
        if (color is { } cc) color = new Vec3(Math.Max(0, cc.X), Math.Max(0, cc.Y), Math.Max(0, cc.Z));
        return new NodeLook(color, Val(1), Val(2), Val(3), Val(4), Val(5));
    }

    /// <summary>The bump height at a point, 0 to 1 (0 if Bump isn't linked).</summary>
    public double Height(in NodeContext ctx) => _out[6] is { } src ? Value(Run(ctx), src) : 0;
}
