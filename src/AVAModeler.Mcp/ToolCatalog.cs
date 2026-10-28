using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Tools;

namespace AVAModeler.Mcp;

/// <summary>
/// The Modeler's tools described for an agent, read from the <see cref="ToolRegistry"/> and the tools' own
/// <see cref="ParamAttribute"/>s, so plugins' tools appear too and nothing is listed by hand.
/// </summary>
public static class ToolCatalog
{
    /// <summary>The tabs in the order the Modeler shows them; a plugin's own tabs come after.</summary>
    private static readonly string[] TabOrder = ["Objects", "Modify", "Multiply", "Polygon", "Tools"];

    /// <summary>Every tool: name, tab, how it runs, whether it works by name, and a one-line summary.</summary>
    public static JsonObject List(ToolRegistry registry)
    {
        var tools = new JsonArray();
        foreach (var tool in registry.Names.Select(registry.Create)
                     .OrderBy(t => Array.IndexOf(TabOrder, t.Tab) is var i && i < 0 ? TabOrder.Length : i).ThenBy(t => t.Tab).ThenBy(t => t.Name))
        {
            var o = new JsonObject
            {
                ["name"] = tool.Name,
                ["tab"] = tool.Tab,
                ["summary"] = SummaryOf(tool),
            };
            if (!tool.Scriptable) o["scriptable"] = false;
            tools.Add(o);
        }
        return new JsonObject
        {
            ["count"] = tools.Count,
            ["tools"] = tools,
            ["next"] = "describe_tool gives a tool's parameters; run_tool runs it on the selection (or everything in the foreground layers when nothing is selected).",
        };
    }

    /// <summary>A tool's summary, falling back to its help, then to its name.</summary>
    public static string SummaryOf(Tool tool) => tool.Summary is { Length: > 0 } s ? s : tool.Help is { Length: > 0 } h ? h : tool.Name;

    /// <summary>A tool in full: its summary and every parameter with type, default, unit, limits or choices.</summary>
    public static JsonObject Describe(ToolRegistry registry, string name)
    {
        if (!registry.Has(name)) throw new ArgumentException(UnknownTool(registry, name));
        var tool = registry.Create(name);
        var parameters = new JsonArray();
        var example = new JsonObject();
        foreach (var p in tool.Parameters)
        {
            var o = new JsonObject { ["name"] = p.Name, ["type"] = TypeName(p.Type) };
            if (p.Label != p.Name) o["label"] = p.Label;
            o["default"] = ToJson(p.Value);
            if (p.Info.Distance) o["unit"] = "metres";
            else if (p.Info.Angle) o["unit"] = "degrees";
            if (!double.IsNegativeInfinity(p.Info.Min)) o["min"] = p.Info.Min;
            if (!double.IsPositiveInfinity(p.Info.Max)) o["max"] = p.Info.Max;
            if (p.Type.IsEnum) o["choices"] = new JsonArray(Enum.GetNames(p.Type).Select(n => (JsonNode)n).ToArray());
            o["description"] = DescribeParameter(p);
            parameters.Add(o);
            example[p.Name] = ToJson(p.Value);
        }
        var result = new JsonObject
        {
            ["name"] = tool.Name,
            ["tab"] = tool.Tab,
            ["summary"] = SummaryOf(tool),
            ["scriptable"] = tool.Scriptable,
            ["parameters"] = parameters,
            ["example"] = new JsonObject { ["name"] = tool.Name, ["params"] = example },
        };
        if (tool.Help.Length > 0 && tool.Help != tool.Summary) result["in_the_app"] = tool.Help;
        if (!tool.Scriptable) result["note"] = "This tool only works with the mouse in the app; run_tool refuses it.";
        return result;
    }

    /// <summary>The error for a tool name the registry doesn't know, with the nearest names.</summary>
    public static string UnknownTool(ToolRegistry registry, string name)
    {
        var near = registry.Names.OrderBy(n => Distance(n.ToLowerInvariant(), (name ?? "").ToLowerInvariant())).Take(3);
        return $"There's no tool called \"{name}\". Did you mean {string.Join(", ", near.Select(n => $"\"{n}\""))}? list_tools lists them all.";
    }

    private static string TypeName(Type t) =>
        t == typeof(Vec3) ? "vector [x, y, z]" :
        t == typeof(double) ? "number" :
        t == typeof(int) ? "integer" :
        t == typeof(bool) ? "boolean" :
        t == typeof(string) ? "string" :
        t.IsEnum ? "choice" : t.Name;

    /// <summary>A parameter's meaning in words: its own description, its unit, its limits and its choices.</summary>
    private static string DescribeParameter(ToolParameter p)
    {
        var parts = new List<string>();
        parts.Add(p.Info.Description.Length > 0 ? p.Info.Description : p.Label + ".");
        if (p.Type == typeof(Vec3)) parts.Add(p.Info.Distance ? "[x, y, z] in metres." : p.Info.Angle ? "[x, y, z] in degrees." : "[x, y, z].");
        else if (p.Info.Distance) parts.Add("In metres.");
        else if (p.Info.Angle) parts.Add("In degrees.");
        if (p.Limits() is { Length: > 0 } limits) parts.Add($"Must be {limits}{(p.Type == typeof(Vec3) ? " on each axis" : "")}.");
        if (p.Type.IsEnum) parts.Add($"One of {string.Join(", ", Enum.GetNames(p.Type))}.");
        if (p.Type == typeof(string) && p.Name.Contains("Surface", StringComparison.OrdinalIgnoreCase)) parts.Add("A surface name (made if new).");
        return string.Join(" ", parts);
    }

    /// <summary>A parameter value as JSON: numbers, text, booleans, vectors as [x, y, z], choices by name.</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        Vec3 v => new JsonArray(v.X, v.Y, v.Z),
        double d => d,
        int i => i,
        bool b => b,
        string s => s,
        Enum e => e.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
