using AVAModeler.Tools;
using ModelContextProtocol.Server;

namespace AVAModeler.Mcp;

/// <summary>
/// <c>AVAModeler --mcp</c>: the MCP server on stdio, with no window. Headless, it works on a document of its own;
/// with <c>--attach</c> it is a bridge to the running app's live document instead (see <see cref="LiveBridge"/>).
/// Nothing but the MCP stream goes to stdout; notes go to stderr.
/// </summary>
public static class McpCommandLine
{
    public const string Usage = """
        AVAModeler --mcp [--attach] [--root FOLDER] [--no-plugins]

          --mcp          Serve the Model Context Protocol on stdin and stdout for an AI agent, with no window.
          --attach       Drive the AVA Modeler that's open on this desktop (with Macros › Allow AI Agents on),
                         instead of a model of the server's own.
          --root FOLDER  The working folder that open, save and texture paths start from, and that open and
                         save stay inside (default: the current folder).
          --no-plugins   Don't load plugins from the plugins folder (headless only).
        """;

    /// <summary>Whether the command line asks for the MCP server.</summary>
    public static bool Wanted(IReadOnlyList<string> args) => args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the server until the client closes the stream. <paramref name="registry"/> makes the tool registry (given
    /// whether plugins may load). Returns the process's exit code.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<bool, ToolRegistry> registry, Stream input, Stream output, TextWriter log, CancellationToken cancel = default)
    {
        bool attach = false, plugins = true;
        string root = Environment.CurrentDirectory;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mcp": break;
                case "--attach": attach = true; break;
                case "--no-plugins": plugins = false; break;
                case "--root" when i + 1 < args.Count: root = args[++i]; break;
                case "--help" or "-h":
                    await log.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 0;
                default:
                    await log.WriteLineAsync($"Unknown option {args[i]}.\n\n{Usage}").ConfigureAwait(false);
                    return 2;
            }
        }
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            await log.WriteLineAsync($"The working folder {root} doesn't exist.").ConfigureAwait(false);
            return 2;
        }
        var transport = new StreamServerTransport(input, output, ModelerServer.Name);
        if (attach)
        {
            await log.WriteLineAsync($"AVA Modeler MCP bridge to the live app (pipe {LiveProtocol.PipeName}), working folder {root}.").ConfigureAwait(false);
            await using var bridge = new LiveBridge(LiveProtocol.PipeName, root);
            await using var server = McpServer.Create(transport, bridge.Options());
            await server.RunAsync(cancel).ConfigureAwait(false);
        }
        else
        {
            await log.WriteLineAsync($"AVA Modeler MCP server (headless), working folder {root}.").ConfigureAwait(false);
            await ModelerServer.RunAsync(transport, new HeadlessHost(root, registry(plugins)), cancel).ConfigureAwait(false);
        }
        return 0;
    }
}
