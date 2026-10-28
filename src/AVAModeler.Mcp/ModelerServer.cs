using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// The MCP server: <see cref="ModelerTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection),
/// with instructions that tell an agent AVA Modeler's conventions. The same server runs over stdio headless, and over a
/// pipe in the live app.
/// </summary>
public static class ModelerServer
{
    public const string Name = "AVAModeler";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Modeler is a polygon modeler in the style of LightWave Modeler 4 and 5. You build objects with the same tools a person uses in its interface, and look at your work with render_view.

        Conventions (they matter):
        - Units are metres. The space is left-handed, as LightWave's: +X right, +Y up, +Z away from the viewer in the Front view. The Front view looks along +Z (X right, Y up), the Top view looks down along -Y (X right, Z up the picture), the Side view looks along -X (Z right, Y up).
        - A polygon has any number of points: one is a dot, two a line, three or more a face. A face is seen from the side where its points run clockwise, and its normal points out of that side; closed shapes face outward.
        - Indices count from 0: layers 0 to 9 (the app's layer buttons say 1 to 10), and points and polygons within each layer. Lua macros (run_lua) count from 1.
        - Each layer is in the foreground (being edited), the background (shown dimmed; the other operand for Boolean, Drill, Path Extrude and Rail Extrude) or off. New geometry goes into the current layer, the lowest-numbered foreground layer.
        - Tools act on the selection in the foreground layers. With nothing selected they act on everything there, as classic Modeler did, so set or clear the selection before each step. Several tools clear the selection when they finish.
        - Rotations are in degrees and right-handed about their axis: a positive angle about Y turns +Z toward +X.
        - Every change is one undo step (undo takes it back, and in the live app Ctrl+Z does too). A call that fails changes nothing.

        A good way to work: list_tools, then describe_tool for the tools you'll use; build with run_tool (or add_points and add_polygons); check counts with get_document and the shape with render_view (perspective, then top, front and side to check alignment); set_surface for colours; save as .lwo.
        """;

    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IModelerHost host)
    {
        var target = new ModelerTools(host);
        foreach (var method in typeof(ModelerTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Modeler",
        Version = typeof(ModelerServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IModelerHost host)
    {
        var options = new McpServerOptions { ServerInfo = Info, ServerInstructions = Instructions, ToolCollection = [] };
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                return await next(request, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(host)) options.ToolCollection.Add(tool);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IModelerHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (args == null || !tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IModelerHost
    {
        public string Root => Path.GetTempPath();
        public Task<T> RunAsync<T>(string action, Func<AVAModeler.Tools.Commands, T> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected to a modeler.");
    }
}
