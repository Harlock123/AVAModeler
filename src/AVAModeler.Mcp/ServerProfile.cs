using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// What one app's MCP server offers agents: its name, its instructions, which of <see cref="ModelerTools"/>' tools and
/// <see cref="Recipes"/>' prompts it serves (with their descriptions adjusted if need be), tools and prompts of its own,
/// and the pipe its live window listens on. AVA Modeler serves <see cref="Modeler"/>, everything; a standalone Layout
/// (AVALightWave) serves the scene tools only (<see cref="ToolGroups.Scene"/>) with tools and recipes of its own. The
/// handlers are the same either way: a profile chooses among them, it never changes what they do.
/// </summary>
public sealed record ServerProfile
{
    /// <summary>The server's name (MCP's server info, the transport's name), e.g. "AVAModeler".</summary>
    public required string Name { get; init; }

    /// <summary>The name a person reads ("AVA Modeler").</summary>
    public required string Title { get; init; }

    /// <summary>What every agent is told on connecting.</summary>
    public required string Instructions { get; init; }

    /// <summary>The server's version (MCP's server info); the engine's own unless given.</summary>
    public string? Version { get; init; }

    /// <summary>Which of <see cref="ModelerTools"/>' tools to serve, by name (all unless given).</summary>
    public Func<string, bool> IncludeTool { get; init; } = _ => true;

    /// <summary>Which of <see cref="Recipes"/>' prompts to serve, by name (all unless given).</summary>
    public Func<string, bool> IncludePrompt { get; init; } = _ => true;

    /// <summary>A tool's description as this server gives it (name, the engine's description → the one to serve); unchanged unless given.</summary>
    public Func<string, string, string>? Describe { get; init; }

    /// <summary>Tools of the app's own, bound to a host, served beside the chosen ones.</summary>
    public Func<IModelerHost, IEnumerable<McpServerTool>>? ExtraTools { get; init; }

    /// <summary>Prompts (recipes) of the app's own, served beside the chosen ones.</summary>
    public Func<IEnumerable<McpServerPrompt>>? ExtraPrompts { get; init; }

    /// <summary>
    /// Whether add_object with no file may put "the Modeler's object" in the scene: true in AVA Modeler, where the open
    /// object is live in Layout; a standalone Layout has none, so there it asks for a file instead.
    /// </summary>
    public bool HasModelerObject { get; init; } = true;

    /// <summary>The pipe the app's live window listens on for agents (per user).</summary>
    public Func<string> PipeName { get; init; } = () => LiveProtocol.PipeName;

    /// <summary>The program's name on the command line, for usage and messages ("AVAModeler").</summary>
    public string Program { get; init; } = "AVAModeler";

    /// <summary>The <c>--mcp</c> command line's usage text (<see cref="McpCommandLine.Usage"/> unless given).</summary>
    public string? Usage { get; init; }

    /// <summary>What the live bridge says when no window is taking agents.</summary>
    public string NotRunning { get; init; } = LiveBridge.NotRunning;

    /// <summary>Added to the instructions when the server is the live window's.</summary>
    public string LiveNote { get; init; } =
        "This is the live app: the person using AVA Modeler sees every change in its views, and their own edits share the same document and undo history.";

    /// <summary>AVA Modeler's own server: every tool and recipe.</summary>
    public static ServerProfile Modeler { get; } = new()
    {
        Name = ModelerServer.Name,
        Title = "AVA Modeler",
        Instructions = ModelerServer.Instructions,
    };
}

/// <summary>Which of the server's tools work on what.</summary>
public static class ToolGroups
{
    /// <summary>
    /// The tools that need only Layout's scene (and files): staging, lighting, animating, deforming and rendering. The
    /// rest model (they need the Modeler's object: run_tool, set_surface, save and the like, add_morph and copy_layer,
    /// which copy a Modeler layer). A standalone Layout serves these; a test checks every tool is in one group or the other.
    /// </summary>
    public static readonly IReadOnlySet<string> Scene = new HashSet<string>(StringComparer.Ordinal)
    {
        "get_scene", "get_keys", "new_scene", "open_scene", "save_scene", "export_scene", "add_object", "add_light", "add_camera",
        "add_null", "add_bone", "remove_item", "set_item", "set_transform", "look_at", "set_key", "delete_key", "set_end_behavior",
        "set_follow_path", "bake_path", "shift_keys", "scale_keys", "set_scene", "undo_scene", "redo_scene", "render_scene",
        "render_scene_view", "render_animation", "make_movie",
        "set_morph", "remove_morph", "add_displacement", "set_displacement", "remove_displacement", "skelegons_to_bones", "set_ik", "bake_ik",
    };

    /// <summary>The modeling tools: every other one of <see cref="ModelerTools"/>'.</summary>
    public static readonly IReadOnlySet<string> Modeling = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_tools", "describe_tool", "run_tool", "add_points", "add_polygons", "set_selection_mode", "select_points", "select_polygons",
        "select_box", "select_all", "select_none", "select_invert", "select_connected", "select_edges", "select_loop", "select_ring",
        "set_layers", "undo", "redo", "new", "open", "save", "set_surface", "set_texture", "set_backdrop", "set_procedural",
        "remove_textures", "make_uvs", "set_uv_map", "run_lua", "get_document", "get_points", "get_polygons", "check_mesh",
        "render_view", "render", "copy_layer", "add_morph",
    };
}
