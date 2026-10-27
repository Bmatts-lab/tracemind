using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TraceMind.App.Views;

namespace TraceMind.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0])) window.OpenPath(args[0]);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
