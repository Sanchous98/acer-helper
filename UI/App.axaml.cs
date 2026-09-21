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

            // composition root: detect device, wire settings, build the application service. The machine arrives
            // BUILT — ports filled by the vendor backend that recognised it, and the settings that backend
            // declared travelling on it (<c>Device.DeclaredSettings</c>), which the service hands to the settings
            // MODEL at construction because the model is what holds and switches that set.
            var device = DeviceFactory.Create();
            var service = new LaptopService(device, new JsonSettingsStore(),
                                            DeviceFactory.CreateDynamicLightingFactory());
            // Popups are placed by the toolkit, not by us, and on a scaled XWayland the toolkit places them
            // windowPosition * (1 - 1/scale) up and to the left of their control (measured; see
            // PopupPlacement.cs). Installed once here, before the first window exists, and inert everywhere but
            // that one kind of session — it is the popup half of the same session bug Reanchor handles for the
            // flyout window itself.
            PopupPlacement.Install();

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
