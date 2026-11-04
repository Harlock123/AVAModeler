using AVAModeler.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAModeler.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What Claude is told: the MCP server's own instructions, and a few lines about working inside the app.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Modeler itself, in its AI Chat panel, with the person watching. Your tool calls change the object and the scene open in the window as you make them, and the person's Ctrl+Z undoes your modeling steps one at a time. Talk to them briefly and plainly: say what you're about to build, build it, look at it with render_view (and render or render_scene when a finished picture helps), then say what you made. Keep it to a few sentences; the tool calls show in the panel on their own. If the person is busy in the app (a tool armed or a dialog open), calls wait for them. Files you open or save are in the working folder.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => ModelerServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given. run_tool runs any Modeler tool by name (list_tools and describe_tool say what there is).";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view and render still show the person what you made, but check your work yourself with get_document and check_mesh (and get_scene in Layout).";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: modeling through run_tool and raw
    /// points and polygons, surfaces, a look and a check, files, and the main scene tools.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "list_tools", "describe_tool", "run_tool", "add_points", "add_polygons", "select_all", "select_none", "select_polygons",
        "set_layers", "undo", "new", "save", "set_surface", "get_document", "check_mesh", "render_view",
        "get_scene", "add_object", "add_light", "set_transform", "look_at", "set_key", "render_scene",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
