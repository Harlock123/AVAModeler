using Avalonia;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Tools;

namespace AVAModeler;

internal static class Program
{
    /// <summary>
    /// Starts the app; an object file on the command line opens in the Modeler. With <c>--mcp</c> it runs no window at
    /// all: it serves the Model Context Protocol on stdio for an AI agent instead (see <see cref="McpCommandLine"/>).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpCommandLine.Wanted(args))
            return McpCommandLine.RunAsync(args, Registry, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The tools, with the user's plugins as the app loads them (problems go to stderr: stdout is the MCP stream).</summary>
    private static ToolRegistry Registry(bool plugins)
    {
        var registry = ToolRegistry.Create();
        if (plugins)
            foreach (var problem in registry.LoadPlugins(Preferences.PluginsFolder))
                Console.Error.WriteLine($"A plugin didn't load: {problem}");
        return registry;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
