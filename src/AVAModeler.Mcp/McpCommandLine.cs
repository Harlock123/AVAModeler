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
                         [--http [ADDRESS:]PORT [--token TOKEN] [--cert FILE --key FILE]]

          --mcp          Serve the Model Context Protocol on stdin and stdout for an AI agent, with no window.
          --attach       Drive the AVA Modeler that's open on this desktop (with Macros › Allow AI Agents on),
                         instead of a model of the server's own.
          --root FOLDER  The working folder that open, save and texture paths start from, and that open and
                         save stay inside (default: the current folder).
          --no-plugins   Don't load plugins from the plugins folder (headless only).
          --http [ADDRESS:]PORT
                         Serve over the network instead of stdio (MCP Streamable HTTP at /mcp), headless, until
                         stopped with Ctrl+C. ADDRESS is 0.0.0.0 (every network; the default), localhost or one of
                         this computer's addresses. Every agent connected works on the one model.
          --token TOKEN  The access token agents must send ("Authorization: Bearer TOKEN"), 16 characters or
                         more; else the AVAMODELER_MCP_TOKEN environment variable; else a new one, printed.
          --cert FILE --key FILE
                         Serve HTTPS with this certificate and private key (PEM files).
        """;

    /// <summary>Whether the command line asks for the MCP server.</summary>
    public static bool Wanted(IReadOnlyList<string> args) => args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the server until the client closes the stream. <paramref name="registry"/> makes the tool registry (given
    /// whether plugins may load). Returns the process's exit code.
    /// </summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, Func<bool, ToolRegistry> registry, Stream input, Stream output, TextWriter log, CancellationToken cancel = default) =>
        RunAsync(args, registry, input, output, log, ServerProfile.Modeler, cancel);

    /// <summary>
    /// Runs a profile's server (its tools, recipes and instructions, its live window's pipe) until the client closes the
    /// stream, as <c>AVAModeler --mcp</c> does for AVA Modeler's own.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<bool, ToolRegistry> registry, Stream input, Stream output, TextWriter log, ServerProfile profile, CancellationToken cancel = default)
    {
        string usage = profile.Usage ?? Usage;
        bool attach = false, plugins = true;
        string root = Environment.CurrentDirectory;
        string? http = null, token = null, cert = null, key = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mcp": break;
                case "--attach": attach = true; break;
                case "--no-plugins": plugins = false; break;
                case "--root" when i + 1 < args.Count: root = args[++i]; break;
                case "--http" when i + 1 < args.Count: http = args[++i]; break;
                case "--token" when i + 1 < args.Count: token = args[++i]; break;
                case "--cert" when i + 1 < args.Count: cert = args[++i]; break;
                case "--key" when i + 1 < args.Count: key = args[++i]; break;
                case "--help" or "-h":
                    await log.WriteLineAsync(usage).ConfigureAwait(false);
                    return 0;
                default:
                    await log.WriteLineAsync($"Unknown option {args[i]}.\n\n{usage}").ConfigureAwait(false);
                    return 2;
            }
        }
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            await log.WriteLineAsync($"The working folder {root} doesn't exist.").ConfigureAwait(false);
            return 2;
        }
        if (http != null)
        {
            if (attach)
            {
                await log.WriteLineAsync("--http serves a model of its own; to let other computers drive the open app, turn on its network door in Preferences.").ConfigureAwait(false);
                return 2;
            }
            return await ServeHttpAsync(http, token, cert, key, root, registry(plugins), log, profile, cancel).ConfigureAwait(false);
        }
        var transport = new StreamServerTransport(input, output, profile.Name);
        if (attach)
        {
            string pipe = profile.PipeName();
            await log.WriteLineAsync($"{profile.Title} MCP bridge to the live app (pipe {pipe}), working folder {root}.").ConfigureAwait(false);
            await using var bridge = new LiveBridge(pipe, root, profile);
            await using var server = McpServer.Create(transport, bridge.Options());
            await server.RunAsync(cancel).ConfigureAwait(false);
        }
        else
        {
            await log.WriteLineAsync($"{profile.Title} MCP server (headless), working folder {root}.").ConfigureAwait(false);
            await ModelerServer.RunAsync(transport, new HeadlessHost(root, registry(plugins)), profile, cancel).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>Splits <c>[ADDRESS:]PORT</c> (an IPv6 address in brackets): the address is every network unless given.</summary>
    public static bool TryParseEndpoint(string text, int defaultPort, out string address, out int port)
    {
        address = "0.0.0.0";
        port = defaultPort;
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        string portText = text;
        if (colon >= 0 && !(text.StartsWith('[') && text.EndsWith(']')))
        {
            address = text[..colon];
            portText = text[(colon + 1)..];
        }
        else if (!text.All(char.IsAsciiDigit))
        {
            address = text;
            return NetworkListener.ParseAddress(address) != null;
        }
        return int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535
            && NetworkListener.ParseAddress(address) != null;
    }

    /// <summary>The headless server over the network, one model for every agent, until <paramref name="cancel"/> (Ctrl+C).</summary>
    private static async Task<int> ServeHttpAsync(string endpoint, string? token, string? cert, string? key, string root, ToolRegistry registry, TextWriter log, ServerProfile profile, CancellationToken cancel)
    {
        if (!TryParseEndpoint(endpoint, profile.NetworkPort, out var address, out var port))
        {
            await log.WriteLineAsync($"\"{endpoint}\" isn't [ADDRESS:]PORT (for example 8765 or 0.0.0.0:8765).").ConfigureAwait(false);
            return 2;
        }
        bool made = false;
        token ??= Environment.GetEnvironmentVariable(profile.TokenVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : null;
        if (token == null)
        {
            token = NetworkListener.NewToken();
            made = true;
        }
        var settings = new NetworkSettings(address, port, token, cert, key);
        var host = new HeadlessHost(root, registry);
        await using var listener = new NetworkListener(settings, profile.Name, o => ModelerServer.Configure(o, host, profile), ModelerServer.EndSessionAsync);
        try
        {
            await listener.StartAsync(cancel).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            await log.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
        await log.WriteLineAsync($"{profile.Title} MCP server (headless) on the network, working folder {root}. Agents connect to:").ConfigureAwait(false);
        foreach (var url in listener.Urls) await log.WriteLineAsync("  " + url).ConfigureAwait(false);
        await log.WriteLineAsync(made
            ? $"with the header \"Authorization: Bearer {token}\" (a new token: give --token or {profile.TokenVariable} to keep one)."
            : "with the header \"Authorization: Bearer <your token>\".").ConfigureAwait(false);
        if (!settings.Secure) await log.WriteLineAsync("Plain HTTP: the token and the work cross the network unencrypted. Use --cert and --key on a network you don't trust.").ConfigureAwait(false);
        await log.WriteLineAsync("Ctrl+C stops it.").ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
