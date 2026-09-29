using Avalonia.Controls.ApplicationLifetimes;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace AcerHelper.UI;

/// <summary>THE COMPOSITION ROOT, stated as ONE graph in one place. It used to be hand-wired in
/// <see cref="App.OnFrameworkInitializationCompleted"/>: a device, then a service, then a controller, and every
/// dependency the graph grows was another hand-off at the call site. A use case that OWNS its dependency (the
/// shape the applied edits are moving to — <c>ApplyUndervolt(IUndervoltTarget target)</c> rather than a static
/// <c>Run(counts, target)</c>) cannot be hand-wired at the consumer without the consumer naming the
/// infrastructure contract, which is the whole thing the seam exists to avoid; so the graph is resolved here
/// instead.
///
/// EVERY REGISTRATION IS A FACTORY DELEGATE, never the <c>AddSingleton&lt;TService, TImpl&gt;</c> type pair: the
/// type pair makes the container reflect over a constructor, and this app is NATIVE AOT on both TFMs
/// (README) with a zero-IL30xx budget — an explicit factory is the shape that needs no reflection over
/// application types. The one thing the container does reflect over is its own <c>IServiceCollection</c>
/// bookkeeping, which the framework's AOT annotations cover.
///
/// LIFETIMES ARE ALL SINGLETON, deliberately: a machine is one process-lifetime object, the settings graph is
/// one, and the service and every use case over it are stateless-but-for-their-captured-dependencies, so a
/// single instance of each is both correct and what the hand-wiring built.
///
/// WHAT IS NOT HERE YET. The action methods still live on <see cref="LaptopService"/> and are reached through
/// it; they move onto use cases registered here one slice at a time (CO first). Until a use case is moved it is
/// simply not registered — the container is the destination, not a half-migrated second path.</summary>
internal static class CompositionRoot
{
    /// <param name="desktop">The Avalonia desktop lifetime: the controller's ctor needs it, and
    /// <c>--startup</c> (autostart) — which the app reads off its args — is computed in the controller's factory
    /// so the tray-only launch stays a composition decision rather than a constructor parameter.</param>
    internal static ServiceProvider Build(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var services = new ServiceCollection();

        // The machine: detected and filled by the vendor backend that recognized it, carrying the settings it
        // declared (<c>Device.DeclaredSettings</c>). One instance for the process, exactly as composition built it.
        services.AddSingleton(_ => DeviceFactory.Create());

        // The settings store and the service that owns the machine, the graph and the vendor ports.
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore());
        services.AddSingleton(sp => new LaptopService(sp.GetRequiredService<Device>(), sp.GetRequiredService<ISettingsStore>()));

        // The contracts LaptopService IMPLEMENTS rather than declares: it is the layer that owns the graph and
        // the ports, so it is the one implementation of each. The forward registrations are what let a use case
        // depend on a contract without naming the service.
        services.AddSingleton<IUndervoltTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ITuningGate>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IFanAxisTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IGpuOffsetsTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IGpuPowerTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ICpuPowerOverlayTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IDeclaredSettingTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IProfileTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IPreferenceStore>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IClamshellTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IBatteryControlTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IKeyboardBrightnessTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IAutostartTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IBlueLightTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ISetTurboTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ITogglePerformanceTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ISourceProfileTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IPowerSourceSyncTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IFanModeTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IGpuOcModeTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ICpuPowerModeTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ICoModeTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IFanDriveTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IStartupStateTarget>(sp => sp.GetRequiredService<LaptopService>());
        // The QUERY contracts (Application/Queries.cs): forward registrations so a query use case depends on a
        // contract without naming the service.
        services.AddSingleton<ICurrentProfileTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ISelectableProfilesTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IBaseProfileTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ISourceProfileReadTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IFanStateTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IGpuOcStateTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ICoDomainsTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ICpuPowerStateTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<ISensorsReadTarget>(sp => sp.GetRequiredService<LaptopService>());
        services.AddSingleton<IBatteryReadTarget>(sp => sp.GetRequiredService<LaptopService>());

        // The re-apply use case (Application/ReapplySettings.cs) owns its target through its constructor. The
        // target is the executor the service's own boot path needs (the reconciler over the service and the four
        // mode-apply use cases), so composition builds ONE use case over it and assigns it back onto the service —
        // the same cycle-resolution idiom the profile switch above uses — so the boot path, the refresh pass and
        // the resume handler all share the one operation.
        services.AddSingleton<IReapplyTarget>(sp =>
        {
            var svc = sp.GetRequiredService<LaptopService>();
            return new HardwareReconciler(svc, new ApplyModeFan(svc), new ApplyModeGpuOc(svc),
                                          new ApplyModeCpuPower(svc), new ApplyModeCo(svc));
        });
        services.AddSingleton(sp =>
        {
            var svc = sp.GetRequiredService<LaptopService>();
            var reapply = new ReapplySettings(sp.GetRequiredService<IReapplyTarget>());
            svc.Reapply = reapply;
            return reapply;
        });

        // The lighting coordinator is the profile switch's ANNOUNCER, and it is built over the service — so it
        // has to exist before the switch does, and the service has to be told about it after (the service holds
        // the switch; the switch holds the coordinator; the coordinator holds the service). Composition resolves
        // the cycle once, here, by assigning the built switch back onto the service, so the service's own
        // profile-changing callers and the controller share the one use case. It takes the re-apply use case too
        // (its resume handler is one of the three re-apply sites), and the two power-source use cases its wake
        // re-sync drives (the cheap battery read and the per-source restore) — the same ones the refresh pass
        // uses, so a source that changed over sleep is restored before the wake's paint.
        services.AddSingleton(sp => new LightingCoordinator(sp.GetRequiredService<LaptopService>(),
                                                            sp.GetRequiredService<ReapplySettings>(),
                                                            sp.GetRequiredService<ReadBatteryInfo>(),
                                                            sp.GetRequiredService<SyncPowerSource>()));

        // The profile switch (Application/ProfileSwitch.cs): target = the service, announcer = the coordinator.
        // Built once, and the SAME instance is assigned back onto the service, so the service's own
        // profile-changing callers (ApplyProfile, SetTurbo, the sweep's transient force/restore) and the
        // controller all use the one use case — which is what makes it impossible for a switch to bypass the
        // lighting claim.
        services.AddSingleton(sp =>
        {
            var svc = sp.GetRequiredService<LaptopService>();
            var sw = new SwitchProfile(sp.GetRequiredService<IProfileTarget>(),
                                       sp.GetRequiredService<LightingCoordinator>());
            svc.ProfileSwitch = sw;
            return sw;
        });

        // The moved use cases. Each owns its dependencies through its constructor, so the UI depends on the
        // ACTION, not on the infrastructure contract behind it.
        services.AddSingleton(sp => new ApplyUndervolt(
            sp.GetRequiredService<IUndervoltTarget>(), sp.GetRequiredService<ITuningGate>()));
        services.AddSingleton(sp => new ApplyGpuOffsets(sp.GetRequiredService<IGpuOffsetsTarget>()));
        services.AddSingleton(sp => new ApplyGpuPower(sp.GetRequiredService<IGpuPowerTarget>()));
        services.AddSingleton(sp => new ApplyCpuPowerOverlay(sp.GetRequiredService<ICpuPowerOverlayTarget>()));
        services.AddSingleton(sp => new ApplyFanCurve(sp.GetRequiredService<IFanAxisTarget>()));
        services.AddSingleton(sp => new ApplyFanSelection(sp.GetRequiredService<IFanAxisTarget>()));
        services.AddSingleton(sp => new ApplyDeclaredSetting(sp.GetRequiredService<IDeclaredSettingTarget>()));

        // The shell/action family (Application/Preferences.cs, HardwareToggles.cs, ProfilePower.cs, ModeApply.cs).
        // Grouped into AppActions so the controller and the assembler take the set as one argument rather than a
        // dozen positional ones.
        services.AddSingleton(sp => new SetTurboToggles(sp.GetRequiredService<IPreferenceStore>()));
        services.AddSingleton(sp => new SetLanguage(sp.GetRequiredService<IPreferenceStore>()));
        services.AddSingleton(sp => new SetClamshell(sp.GetRequiredService<IClamshellTarget>(), sp.GetRequiredService<IPreferenceStore>()));
        services.AddSingleton(sp => new EvaluateClamshell(sp.GetRequiredService<IClamshellTarget>()));
        services.AddSingleton(sp => new ApplyStartupState(sp.GetRequiredService<IStartupStateTarget>(), sp.GetRequiredService<IClamshellTarget>()));
        services.AddSingleton(sp => new SetBatteryToggle(sp.GetRequiredService<IBatteryControlTarget>()));
        services.AddSingleton(sp => new SetBatteryChoice(sp.GetRequiredService<IBatteryControlTarget>()));
        services.AddSingleton(sp => new SetKeyboardBrightness(sp.GetRequiredService<IKeyboardBrightnessTarget>()));
        services.AddSingleton(sp => new SetAutostart(sp.GetRequiredService<IAutostartTarget>()));
        services.AddSingleton(sp => new SetBlueLight(sp.GetRequiredService<IBlueLightTarget>()));
        services.AddSingleton(sp => new SetTurbo(sp.GetRequiredService<ISetTurboTarget>(), sp.GetRequiredService<SwitchProfile>()));
        services.AddSingleton(sp => new TogglePerformance(sp.GetRequiredService<ITogglePerformanceTarget>(),
            sp.GetRequiredService<SetTurbo>(), sp.GetRequiredService<SwitchProfile>()));
        services.AddSingleton(sp => new SetSourceProfile(sp.GetRequiredService<ISourceProfileTarget>()));
        services.AddSingleton(sp => new SyncPowerSource(sp.GetRequiredService<IPowerSourceSyncTarget>()));
        services.AddSingleton(sp => new ApplyCustom(sp.GetRequiredService<IFanDriveTarget>()));
        services.AddSingleton(sp => new AppActions(
            sp.GetRequiredService<ApplyStartupState>(),
            sp.GetRequiredService<SetTurbo>(),
            sp.GetRequiredService<TogglePerformance>(),
            sp.GetRequiredService<SetTurboToggles>(),
            sp.GetRequiredService<SetClamshell>(),
            sp.GetRequiredService<SetAutostart>(),
            sp.GetRequiredService<SetLanguage>(),
            sp.GetRequiredService<SetKeyboardBrightness>(),
            sp.GetRequiredService<EvaluateClamshell>(),
            sp.GetRequiredService<SyncPowerSource>(),
            sp.GetRequiredService<ApplyCustom>(),
            sp.GetRequiredService<SetSourceProfile>(),
            sp.GetRequiredService<SetBatteryToggle>(),
            sp.GetRequiredService<SetBatteryChoice>(),
            sp.GetRequiredService<SetBlueLight>()));

        // The QUERY family (Application/Queries.cs). Each owns its read through its constructor; the record
        // bundles them so the controller and the options assembler take the set as one argument.
        services.AddSingleton(sp => new ReadCurrentProfile(sp.GetRequiredService<ICurrentProfileTarget>()));
        services.AddSingleton(sp => new ReadSelectableProfiles(sp.GetRequiredService<ISelectableProfilesTarget>()));
        services.AddSingleton(sp => new ReadBaseProfile(
            sp.GetRequiredService<ICurrentProfileTarget>(), sp.GetRequiredService<IBaseProfileTarget>()));
        services.AddSingleton(sp => new ReadSourceProfile(sp.GetRequiredService<ISourceProfileReadTarget>()));
        services.AddSingleton(sp => new ReadFanState(sp.GetRequiredService<IFanStateTarget>()));
        services.AddSingleton(sp => new ReadGpuOcState(sp.GetRequiredService<IGpuOcStateTarget>()));
        services.AddSingleton(sp => new ReadCoDomains(sp.GetRequiredService<ICoDomainsTarget>()));
        services.AddSingleton(sp => new ReadCpuPower(sp.GetRequiredService<ICpuPowerStateTarget>()));
        services.AddSingleton(sp => new ReadSensors(sp.GetRequiredService<ISensorsReadTarget>()));
        services.AddSingleton(sp => new ReadBatteryInfo(sp.GetRequiredService<IBatteryReadTarget>()));
        services.AddSingleton(sp => new AppQueries(
            sp.GetRequiredService<ReadCurrentProfile>(),
            sp.GetRequiredService<ReadSelectableProfiles>(),
            sp.GetRequiredService<ReadBaseProfile>(),
            sp.GetRequiredService<ReadSourceProfile>(),
            sp.GetRequiredService<ReadFanState>(),
            sp.GetRequiredService<ReadGpuOcState>(),
            sp.GetRequiredService<ReadCoDomains>(),
            sp.GetRequiredService<ReadCpuPower>(),
            sp.GetRequiredService<ReadSensors>(),
            sp.GetRequiredService<ReadBatteryInfo>()));

        // The controller. Its factory reads the two things that are not services: the desktop lifetime and the
        // autostart flag, and activates the persisted UI language BEFORE the controller builds any view-model
        // (they read their strings through Loc at construction), which is why the read sits in the factory
        // rather than in the controller (docs/state-and-events.md). The profile switch and the lighting
        // coordinator are handed in because both are shared with the service: the switch announces through the
        // coordinator, and the controller drives/attaches the same coordinator for its refresh/reapply state.
        services.AddSingleton(sp =>
        {
            var svc = sp.GetRequiredService<LaptopService>();
            Loc.Use(svc.Language);
            var startMinimized = desktop.Args?.Contains(AppArgs.Startup) ?? false;
            return new AppController(desktop, svc, startMinimized,
                sp.GetRequiredService<SwitchProfile>(),
                sp.GetRequiredService<LightingCoordinator>(),
                sp.GetRequiredService<ApplyUndervolt>(),
                sp.GetRequiredService<ApplyGpuOffsets>(),
                sp.GetRequiredService<ApplyGpuPower>(),
                sp.GetRequiredService<ApplyCpuPowerOverlay>(),
                sp.GetRequiredService<ApplyFanCurve>(),
                sp.GetRequiredService<ApplyFanSelection>(),
                sp.GetRequiredService<ApplyDeclaredSetting>(),
                sp.GetRequiredService<AppActions>(),
                sp.GetRequiredService<AppQueries>(),
                sp.GetRequiredService<ReapplySettings>());
        });

        return services.BuildServiceProvider();
    }
}
