using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
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

            // composition root: detect device, wire settings, build the application service. The device's declared
            // settings travel with it, because the settings MODEL holds that set and switches it (Infrastructure/Composition/Settings.cs)
            // and IDevice carries no member for it — this is the hand-off.
            var (device, declaredSettings) = DeviceFactory.Create();
            var service = new LaptopService(device, new JsonSettingsStore(), declaredSettings,
                                            DeviceFactory.CreateDynamicLightingFactory());
            // Activate the persisted UI language before any window/view-model is built (they read their
            // strings via Loc at construction). Default is "System" -> follow the OS UI culture. Read through
            // the service's locked accessor rather than off the settings graph: the graph is mutable and the
            // background pass writes it, so every read of it belongs behind the lock that guards it.
            Loc.Use(service.Language);
            // --startup (autostart): run resident in the tray without popping the flyout on every logon.
            var startMinimized = desktop.Args?.Contains(AppArgs.Startup) ?? false;
            _controller = new AppController(desktop, service, startMinimized);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
