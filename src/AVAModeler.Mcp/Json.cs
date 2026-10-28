using System.Text.Json;
using AVAModeler.Core;
using ModelContextProtocol;

namespace AVAModeler.Mcp;

/// <summary>Reading an agent's arguments: JSON values into the plain values the command API takes, with clear errors.</summary>
internal static class Json
{
    /// <summary>
    /// A JSON value as the command API takes it, the way a Lua macro passes one: a number as a double, text, true or
    /// false, a list of numbers (a vector) as double[]; an object {x, y, z} is a vector too.
    /// </summary>
    public static object? Plain(JsonElement e, string what) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble()
            : throw new McpException($"{what} must be a list of numbers, such as [1, 0, 0].")).ToArray(),
        JsonValueKind.Object when TryVector(e, out var v) => new[] { v.X, v.Y, v.Z },
        JsonValueKind.Null => throw new McpException($"{what} needs a value, not null."),
        _ => throw new McpException($"{what} must be a number, text, true/false or a vector [x, y, z]."),
    };

    private static bool TryVector(JsonElement e, out Vec3 v)
    {
        double Get(string n) => e.TryGetProperty(n, out var p) || e.TryGetProperty(n.ToUpperInvariant(), out p)
            ? p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN : double.NaN;
        v = new Vec3(Get("x"), Get("y"), Get("z"));
        return double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    }

    /// <summary>A vector from three numbers.</summary>
    public static Vec3 Vector(double[] a, string what)
    {
        if (a is not { Length: 3 } || a.Any(x => !double.IsFinite(x))) throw new McpException($"{what} must be three numbers [x, y, z].");
        return new Vec3(a[0], a[1], a[2]);
    }

    /// <summary>One of an enum's names (any case), with the choices listed if it isn't one.</summary>
    public static T Choice<T>(string value, string what, params T[] excluded) where T : struct, Enum
    {
        var names = Enum.GetValues<T>().Where(v => !excluded.Contains(v)).Select(v => v.ToString().ToLowerInvariant()).ToList();
        if (value is not null && !int.TryParse(value, out _) && Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var r) && Enum.IsDefined(r) && !excluded.Contains(r)) return r;
        throw new McpException($"{what} must be one of {string.Join(", ", names)}, not \"{value}\".");
    }

    /// <summary>Checks an optional fraction is from 0 to 1.</summary>
    public static void Fraction(double? v, string what)
    {
        if (v is { } x && (x < 0 || x > 1 || !double.IsFinite(x))) throw new McpException($"{what} must be from 0 to 1, not {x}.");
    }

    /// <summary>Checks a picture's size.</summary>
    public static void Size(int width, int height, int max)
    {
        if (width < 16 || width > max || height < 16 || height > max) throw new McpException($"width and height must be from 16 to {max} pixels.");
    }
}
