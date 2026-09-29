using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AcerHelper.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AcerHelper.UI;

public partial class App : Avalonia.Application
{
    private AppController? _controller;
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // tray app: closing windows must not quit the process
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // composition root: the WHOLE graph is stated once in CompositionRoot — device, settings, service,
            // the use cases over it and the controller. It used to be hand-wired here, and every use case the
            // graph grows (the applied edits, the profile switch) would have added another hand-off; the
            // container makes "what depends on what" one readable list instead. The controller's factory reads
            // desktop.Args for --startup, so the tray-only launch stays a composition detail.
            _services = CompositionRoot.Build(desktop);
            _controller = _services.GetRequiredService<AppController>();
            PopupPlacement.Install();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
