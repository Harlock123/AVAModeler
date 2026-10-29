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
        AVA Modeler is a polygon modeler in the style of LightWave Modeler 4 and 5, with a Layout workspace for scenes, animation and rendering. You build objects with the same tools a person uses in its interface, and look at your work with render_view.

        Conventions (they matter):
        - Units are metres. The space is left-handed, as LightWave's: +X right, +Y up, +Z away from the viewer in the Front view. The Front view looks along +Z (X right, Y up), the Top view looks down along -Y (X right, Z up the picture), the Side view looks along -X (Z right, Y up).
        - A polygon has any number of points: one is a dot, two a line, three or more a face. A face is seen from the side where its points run clockwise, and its normal points out of that side; closed shapes face outward.
        - Layers are numbered 1 to 10, as the app's layer buttons show them. Points and polygons are indexed from 0 within each layer (Lua macros, run_lua, count them from 1).
        - Colours are [r, g, b], whole numbers from 0 to 255, everywhere: surfaces, lights, ambient, backdrop and fog.
        - Each layer is in the foreground (being edited), the background (shown dimmed; the other operand for Boolean, Drill, Path Extrude and Rail Extrude) or off. New geometry goes into the current layer, the lowest-numbered foreground layer.
        - Tools act on the selection in the foreground layers. With nothing selected they act on everything there, as classic Modeler did, so set or clear the selection before each step. Several tools clear the selection when they finish.
        - Rotations are in degrees and right-handed about their axis: a positive angle about Y turns +Z toward +X.
        - Every change to the object is one undo step (undo takes it back, and in the live app Ctrl+Z does too). A call that fails changes nothing.

        A good way to model: list_tools, then describe_tool for the tools you'll use; build with run_tool (or add_points and add_polygons); check counts with get_document and the shape with render_view (perspective, then top, front and side to check alignment); set_surface for colours; save as .lwo.

        Layout (the scene tools) stages, lights, animates and renders what you model:
        - The object and the scene are separate. add_object with no file puts the Modeler's object in the scene live (later modeling shows there at once); with file, an object file from the working folder. Objects keep their own coordinates; their position, rotation and scale place them.
        - Same space and units. Items have position [x, y, z], rotation [heading, pitch, bank] in degrees and scale. Heading turns about +Y (90 faces +X), then pitch about the turned X axis (positive looks down), then bank about the turned Z axis. Cameras look along their +Z axis with +Y up; distant and spot lights shine along their +Z; point lights shine every way. look_at aims anything at a point or item.
        - A camera's lens is LightWave's zoom factor: zoom = 1 / tan(vertical field of view / 2); 3.2 (the default) is 34.7 degrees high. Light colours (and all scene colours) are 0 to 255, like surfaces'; intensity 1 is 100%; range is metres to fade out (0: no falloff); a spot's cone_angle is its half-angle.
        - Frames run from the scene's first to last frame, 1 to 60 at 30 frames a second by default (set_scene changes them). Channels are keyed at frames and interpolated by TCB splines. set_transform and look_at key when key is true (Layout's Auto key, on by default) and otherwise just set values; the first key on a channel replaces its constant, so key the start frame first, then later frames. get_scene and get_keys show what is keyed.
        - The scene has its own undo history, Layout's, separate from the object's: each scene call that changes it is one step ("Agent: …"), undo_scene and redo_scene step through it (in the live app it's shared with the person, whose Ctrl+Z in Layout undoes your steps too), and new_scene or open_scene clear it. A scene call that fails changes nothing and leaves no step. undo and redo don't touch the scene.
        - In the live app the scene is the one Layout shows; the window stays where the person has it (they see the scene with F2).
        - Objects deform in their own coordinates: morphs first, then bones, then displacements.
          Morphs: add_morph makes a target from a copy of a Modeler layer with the object's points in the same order (point i moves toward the target's point i). Copy the base to an empty layer with copy_layer, change the copy only with tools that move points, and put the object in the scene by its own layer (add_object layer 1: without a layer every non-empty layer is merged, the target's too). Its weight is the channel morph.<name> (0 the base shape, 1 the target): key it with set_key.
          Displacements: add_displacement adds a wave (straight crests travelling along an axis) or a ripple (circles round a centre) that moves points along one axis; its amplitude is the channel displacement.<number>.amplitude, numbered from 1.
          Skelegons: 2-point polygons (add_points, add_polygons) on a surface whose name starts with "Skelegon" (e.g. "Skelegon_Arm"); the first point is the bone's base, the second its tip, and one whose base is on another's tip becomes its child. skelegons_to_bones turns them into bones at rest there; key the bones' rotation to bend the object; set_item changes a bone's rest pose (rest_position, rest_rotation, rest_at_frame for Rest Here).

        A good way to make a scene: build the object; add_object; add_light (a key light from above to one side, e.g. distant with look_at the object, and maybe a weaker fill); set the camera with set_transform and look_at the object (or add_camera); render_scene_view (view camera, then top) to check placement; key the motion with set_transform or set_key at the first and later frames (look_at at each key for a camera that should keep watching); check an in-between frame with get_scene; render_scene a frame small and look at it; then larger, render_animation for a run of frames (with movie "mp4" or "gif" to join them, if ffmpeg is installed), and save_scene as .avascene.

        The server's prompts are saved recipes (a lathed vase, a table, a chess pawn, three-point lighting, a turntable render, a bouncing ball, the carbon atom, a morph and a waving flag): step-by-step plans with the exact tool calls, to follow or adapt.
        """;

    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IModelerHost host)
    {
        var target = new ModelerTools(host);
        foreach (var method in typeof(ModelerTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

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
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
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
        public Task<T> RunSceneAsync<T>(string action, Func<AVAModeler.Tools.Commands, AVAModeler.Layout.LayoutSession, T> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected to a modeler.");
    }
}
