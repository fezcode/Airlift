using Airlift.Core;
using Avalonia;

namespace Airlift.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v")) { Console.WriteLine(AppVersion.Display); return; }
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine("Airlift desktop app. For command-line package management use airlift-cli --help."); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}


