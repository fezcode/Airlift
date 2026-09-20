using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Airlift.Core;

namespace Airlift.Desktop;
public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The styles resolve their colours dynamically, so a palette has to exist before any
        // control is styled. The stored preference replaces it once settings are readable.
        Themes.Apply(Themes.Default.Id, this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var manager = new PackageManager();
            Themes.Apply(manager.Settings.Theme, this);
            desktop.MainWindow = new MainWindow(manager);
            desktop.Exit += (_, _) => manager.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
