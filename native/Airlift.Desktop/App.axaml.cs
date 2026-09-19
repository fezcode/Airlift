using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Airlift.Core;

namespace Airlift.Desktop;
public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var manager = new PackageManager(); desktop.MainWindow = new MainWindow(manager);
            desktop.Exit += (_, _) => manager.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
