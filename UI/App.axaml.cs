using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcerHelper.Composition;
using AcerHelper.Localization;

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
            // Activate the persisted UI language before any window/view-model is built (they read their
            // strings via Loc at construction). Default is "System" -> follow the OS UI culture.
            Loc.Use(service.Settings.Language);
            // --startup (autostart): run resident in the tray without popping the flyout on every logon.
            var startMinimized = desktop.Args?.Contains(AppArgs.Startup) ?? false;
            _controller = new AppController(desktop, service, startMinimized);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
