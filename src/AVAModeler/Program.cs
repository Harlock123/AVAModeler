using Avalonia;

namespace AVAModeler;

internal static class Program
{
    /// <summary>Starts the app; an object file on the command line opens in the Modeler.</summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
