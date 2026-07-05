using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcerHelper.Composition;

namespace AcerHelper.UI;

public partial class App : Avalonia.Application
{
    private AppController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // tray app: closing windows must not quit the process
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // composition root: detect device, wire settings, build the application service
            var service = new LaptopService(DeviceFactory.Create(), new JsonSettingsStore());
            _controller = new AppController(desktop, service);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
