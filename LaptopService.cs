using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

/// <summary>
/// Application facade / use-case layer. The UI talks only to this and to the Domain model;
/// this talks only to Domain feature ports (<see cref="IDevice"/>) and the settings store.
/// All orchestration (profile cycling/toggling, persistence of changes) lives here, never in
/// the UI or in Infrastructure.
///
/// Split across partial files by feature, because one 814-line file was the only place this
/// layer could be read. This part holds identity and the shared infrastructure every other
/// part depends on — the fields, the <c>_state</c> lock and its <see cref="Save"/> companion,
/// the scalar accessors the UI is allowed to read, <see cref="ApplyStartupState"/>, the
/// per-mode preset helper, and teardown. The rest:
/// <list type="bullet">
/// <item><c>LaptopService.Lighting.cs</c> — the LampArray bridge and the per-mode light zones.</item>
/// <item><c>LaptopService.Profiles.cs</c> — performance profiles, Turbo, power source.</item>
/// <item><c>LaptopService.Fans.cs</c> — the fan presets and the fan-curve engine.</item>
/// <item><c>LaptopService.Tuning.cs</c> — GPU overclock, CPU power mode, Curve Optimizer.</item>
/// <item><c>LaptopService.Toggles.cs</c> — one-shot hardware toggles, device flags, language.</item>
/// </list>
/// The split is a MOVE, not a reorganisation: no method body changed and the lock instance and
/// its coverage are the same object they were, so the concurrency contract below is unchanged.
/// The names are suffixed <c>.Profiles.cs</c> rather than split on the class per feature because
/// the build already selects files by suffix (<c>*.Windows.cs</c>/<c>*.Linux.cs</c> in
/// AcerHelper.csproj) — none of these collide with that, and none may ever be renamed into it.
/// </summary>
public sealed partial class LaptopService : IDisposable
{
    // Explicit fields instead of a primary constructor. A primary constructor's parameters are in scope only in
    // the part that declares them, so while one was in use this class could not be split across partial files
    // (LaptopService.*.cs) — which is the point of the change. The field names are deliberately the former
    // parameter names, so not one call site in the file moved.
    private readonly IDevice device;
    private readonly ISettingsStore store;
    private readonly ILampArrayTransport? lampArray;

    public LaptopService(IDevice device, ISettingsStore store, ILampArrayTransport? lampArray = null)
    {
        this.device = device;
        this.store = store;
        this.lampArray = lampArray;
        // Assigned in the body rather than as `Settings { get; } = store.Load()`. Field and property initializers
        // run BEFORE the body, so as an initializer this read `store` while it was still null. No initializer in
        // this class reads Settings, so loading it here rather than there is observably the same order.
        Settings = store.Load();
    }

    /// <summary>The connected device. The UI reads its (nullable) feature ports to decide which
    /// sections to show; it must route all mutations through this service's methods.</summary>
    public IDevice Device => device;
    public Settings Settings { get; }
    public string? LastError { get; private set; }

    // Guards ALL access to the mutable Settings graph (its collections + scalars), the per-source slots,
    // _onAc, the fan-curve engine, and Save() — because these are now touched from TWO threads: the UI thread
    // (user actions: slider/profile/toggle) AND the background refresh pass (AppController offloads the 3s poll
    // and its re-applies off the UI thread so a stalled ACPI-EC can't freeze the UI). Dictionary<> is not
    // thread-safe and Save() serializes the whole graph, so a concurrent mutation would throw / tear without
    // this. Re-entrant (System.Threading.Lock), so the nested calls below (SetFan->ApplyCustom, SetTurbo->
    // ApplyProfile, SyncPowerSource->SeedSlotFromHardware->Save, ...) don't self-deadlock. Lock order is always
    // _state -> WMI Gate (this layer takes _state first, then calls a port that takes Gate); the WMI layer never
    // calls back here, so the reverse never happens -> no deadlock.
    private readonly Lock _state = new();

    private void Save() { lock (_state) store.Save(Settings); }

    // Locked reads of the few Settings SCALARS the UI needs, so it never reaches into the graph itself. The
    // invariant above forbids unguarded Settings access, and AppController reads these on the UI thread AND on the
    // background pass; a bool/int cannot tear today, but the read that WOULD tear the moment one of them stops
    // being a scalar is exactly the read this removes (TogglePerformance already does it this way, with a local).
    // `public Settings Settings { get; }` stays exposed, so this is convention, not a compile-time guarantee.
    public bool TurboToggles { get { lock (_state) return Settings.TurboToggles; } }
    public AppLanguage Language { get { lock (_state) return Settings.Language; } }
    public int Bluelight { get { lock (_state) return Settings.Bluelight; } }

    /// <summary>Re-apply persisted state that the OS doesn't remember on its own.</summary>
    public void ApplyStartupState()
    {
        bool dynamicLighting;
        lock (_state)
        {
            if (Settings.Clamshell) device.Clamshell?.SetEnabled(true);
            if (Settings.Bluelight > 0) device.DisplayTint?.Apply(Settings.Bluelight);
            ApplyModeGpuOc();     // GPU clock offsets reset to 0 on boot/driver-reload -> re-apply the current mode's
            ApplyModeCpuPower();  // enforce the current profile's CPU power mode (if the user set one for it)
            // Read here rather than at its use below, so this is a guarded read of the graph like every other one
            // in this file. Not a race in practice — this runs before the coordinator, the UI and the 3s pass
            // exist, so there is no writer yet — but the invariant declared above admits no unguarded access.
            dynamicLighting = Settings.DynamicLighting;
        }

        // Re-apply the current mode's CPU undervolt. Deliberately OUTSIDE the _state lock and off the caller's (UI)
        // thread: unlike the GPU offsets above (a fast NvAPI call), an SMU mailbox transaction waits on the
        // machine-wide PCI access lock that HWiNFO/Ryzen Master/RyzenAdj also take, so it can block for seconds —
        // holding _state across it would stall the background refresh pass and, through it, the UI.
        if (device.CurveOptimizer != null)
            _ = Task.Run(() => { try { ApplyModeCo(); } catch { /* stays stock */ } });

        // Bring the virtual LampArray back up if the user left it on. Off the caller's (UI) thread on purpose:
        // publishing it creates a PnP device node and waits for the driver to start — up to a few seconds on a
        // cold boot. Failure is not surfaced here; the Options row reads the bridge's real state when shown.
        if (dynamicLighting && LampArray is { } la)
            _ = Task.Run(() => { try { la.Enable(); } catch { /* stays off */ } });
    }

    /// <summary>Look up <paramref name="key"/> in <paramref name="map"/>, creating and inserting a default
    /// instance when it is absent — the per-mode "preset, created on first write" idiom the Stored* and
    /// per-mode accessors above all share.</summary>
    private static T GetOrAdd<T>(Dictionary<string, T> map, string key) where T : new()
    {
        if (!map.TryGetValue(key, out var value)) map[key] = value = new T();
        return value;
    }

    private bool Run<T>(T? svc, Func<T, bool> set, Func<T, string?> err) where T : class
    {
        if (svc == null) return false;
        bool ok;
        try { ok = set(svc); }
        catch { ok = false; }
        if (!ok) LastError = err(svc);
        return ok;
    }

    public void Dispose()
    {
        // Tear the virtual LampArray down BEFORE the device: it stops its worker and removes the PnP node, so
        // Windows doesn't keep offering a lighting device this process no longer backs.
        try { _lampArray?.Dispose(); } catch { /* best-effort teardown */ }
        device.Dispose();
    }
}
