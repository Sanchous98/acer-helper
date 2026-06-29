using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AcerHelper;

public partial class App : Application
{
    private AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // tray app: closing windows must not quit the process
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new AppController(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
