using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>
/// The hardware-facing service the UI talks to: it owns the connected device, the settings graph and the store
/// that reads and writes it, and it talks to the Domain feature ports (<see cref="IDevice"/>) on the machine's
/// behalf. All orchestration (profile cycling/toggling, persistence of changes) lives here, never in the UI.
///
/// IT IS INFRASTRUCTURE BY THE OWNER'S RULING, and the reason is worth keeping beside the class: the name says
/// what it is — a service over hardware and the form settings are kept in, not a use case — and it is also what
/// names the persisted container (FanPreset, CoPreset, …) and calls the store that builds it. What stays in
/// Application is the PLAN of a re-apply (<c>Application/ReapplyPlan.cs</c>), which this class's
/// <see cref="Reconciler"/> executes; the contracts Application declares and the UI names live in
/// <c>Application/DynamicLighting.cs</c>.
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
    private readonly IDynamicLightingFactory? dynamicLightingFactory;

    public LaptopService(IDevice device, ISettingsStore store, IReadOnlyList<SettingDeclaration> declaredSettings,
                         IDynamicLightingFactory? dynamicLightingFactory = null)
    {
        this.device = device;
        this.store = store;
        this.dynamicLightingFactory = dynamicLightingFactory;
        Reconciler = new HardwareReconciler(this);
        // Assigned in the body rather than as `Settings { get; } = store.Load(...)`. Field and property initializers
        // run BEFORE the body, so as an initializer this read `store` while it was still null. No initializer in
        // this class reads Settings, so loading it here rather than there is observably the same order.
        //
        // ...and the settings this machine declares are handed to the model that holds and switches them
        // (Domain/Settings.cs) THROUGH ITS CONSTRUCTOR: the store builds the model with this set, so the set is
        // fixed before the model exists rather than installed into it afterwards. They arrive as a constructor
        // argument rather than off the device because they no longer live on IDevice: the backend's probe finds
        // them, and the composition root is what carries them here (see DeviceFactory.Create).
        //
        // The device's list is read HERE, at construction, and the model copies it — so a backend that kept
        // declaring after this line would be declaring into nothing. Production already had that order (a vendor
        // declares inside InitVendor, before the service exists); the tests' fakes declare before the fixture
        // builds this, on the same terms.
        Settings = store.Load(declaredSettings);
    }

    /// <summary>The one operation that re-applies volatile state, shared by every site that needs it: this
    /// class's own <see cref="ApplyStartupState"/>, <c>AppController</c>'s refresh pass and
    /// <c>LightingCoordinator</c>'s resume handler. It holds no state of its own — it is one instance so that
    /// there is one place that knows HOW to re-apply, not to share anything (see
    /// <see cref="HardwareReconciler"/>).</summary>
    internal HardwareReconciler Reconciler { get; }

    /// <summary>The connected device. The UI reads its (nullable) feature ports to decide which
    /// sections to show; it must route all mutations through this service's methods.</summary>
    public IDevice Device => device;

    /// <summary>The mutable settings graph. <c>internal</c> as a SIGNPOST, not a fence — and the difference
    /// matters, so it is stated rather than implied. This is ONE assembly: <c>AcerHelper.csproj</c> compiles
    /// Domain/, Application/, Infrastructure/, UI/ and Bootstrap/ together, so <c>internal</c> is visible to
    /// every one of them and stops nothing inside this repo. What it does is take the property off the public
    /// surface, so it stops reading as an invitation, and it makes the eventual assembly split — the only thing
    /// that WOULD enforce a layer boundary, see the plan's Wave 1 note — mechanical instead of a redesign.
    ///
    /// Honest limit: the accessors that hand back a LIVE reference into this graph still let a caller mutate
    /// settings without holding the lock — both forms of <c>LightsForCurrentMode</c> and <c>EnsureLightZone</c>,
    /// and nothing else beyond the property itself. The six accessors that used to be in this list —
    /// <c>CurrentFan</c>, <c>ApplyModeFan</c>, <c>CurrentGpuOc</c>, <c>ApplyModeGpuOc</c>, <c>CurrentCo</c>,
    /// <c>ApplyModeCo</c> — now hand back <c>Snapshot()</c>, a copy sharing nothing mutable with the stored
    /// instance. The two lighting ones are left live ON PURPOSE, and that is a recorded decision rather than an
    /// omission (docs/open-decisions.md §4). They are the widest door in the tree and the reason it stays open
    /// is that writing into the graph is the feature there: <c>LightingViewModel</c> keeps the returned
    /// dictionary and writes into it in place, and <c>EnsureLightZone</c> is that path's structural insert —
    /// the one that must share _state with Save(), or a "collection modified" throws mid-serialization. Sealing
    /// those means returning copies, which is a redesign of the lighting path, not a visibility change.</summary>
    internal Settings Settings { get; }

    /// <summary>Run a hardware write and report BOTH halves of its outcome: whether it succeeded, and — when it
    /// did not — the port's own reason.
    ///
    /// This replaces the "write a shared <c>LastError</c> field, let the caller read it afterwards" idiom, which
    /// <c>AppController</c> and <c>OptionsAssembler</c> read from six places and NEVER under <c>_state</c>: the
    /// field was the one value read across threads without a lock, and the failure it produced was not a torn
    /// read but a WRONG one — a caller picking up the error of a different call (docs/open-decisions.md §2).
    /// A returned value cannot be someone else's.
    ///
    /// The rule that keeps this from being a behaviour change: a channel is given ONLY where a reader exists
    /// today. The points that are silent now stay silent — <c>ApplyFan</c>, <c>ApplyStoredMode</c> (when reached
    /// from the refresh loop) and <c>ApplyModeCo</c> have no reader, and wiring one up would start showing the
    /// user messages the app has never shown, which is a feature, not a refactor.</summary>
    private static (bool ok, string? error) Attempt(Func<bool> write, Func<string?> reason)
    {
        bool ok;
        try { ok = write(); } catch { ok = false; }   // a port that throws is a failed write, not a crash
        return (ok, ok ? null : reason());
    }

    /// <summary>The same rule for a write whose op hands back both halves itself — the battery's properties,
    /// which take their reason from the call rather than from a field read after it (Domain/Battery.cs). There
    /// is nothing to fetch on failure, so the reason is absent: only a throw can reach the catch below, and a
    /// throw carries none — the same (false, null) the port shape produces when its LastError was never set.
    /// Kept here beside its sibling so "a throwing write is a failed write, not a crash" is stated once per
    /// shape rather than in each caller.</summary>
    private static (bool ok, string? error) Attempt(Func<(bool ok, string? error)> write)
    {
        try { return write(); } catch { return (false, null); }
    }

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
    // `Settings` is `internal`, which in a single-assembly project is a signpost rather than a fence — see the
    // note on the property for exactly what it does and does not cover. The lock, not the visibility, is what
    // makes these three safe.
    public bool TurboToggles { get { lock (_state) return Settings.TurboToggles; } }
    public AppLanguage Language { get { lock (_state) return Settings.Language; } }
    public int Bluelight { get { lock (_state) return Settings.Bluelight; } }

    /// <summary>Test seam: true when the calling thread already holds <c>_state</c>. Exists so a test can assert
    /// that a hardware call happens OUTSIDE the lock — a property no fake can otherwise observe, because a fake
    /// sees the call, not the lock state around it. <c>System.Threading.Lock</c> is re-entrant and answers this
    /// per-thread, so a port called from inside a lock this thread already holds sees <c>true</c>, and one called
    /// from a thread holding nothing sees <c>false</c>.</summary>
    internal bool StateHeld => _state.IsHeldByCurrentThread;

    /// <summary>Re-apply persisted state that the OS doesn't remember on its own.</summary>
    public void ApplyStartupState()
    {
        // Every hardware call here is made OUTSIDE _state, and every value it needs is read inside it. The lock
        // exists to guard the Settings graph and the slots, not to serialise hardware (design doc D17: this method
        // runs on the UI thread, and it used to hold _state across four hardware calls — two powrprof, one gdi32,
        // one NvAPI — so any other thread's _state acquirer waited behind all four).
        //
        // Hoisting the CALLS rather than their arguments is what makes this safe, and the distinction is the whole
        // point: the mode axes re-read the current mode under their own _state acquisition (ApplyModeGpuOc takes
        // one, ApplyModeCpuPower keeps its own), so moving them out of this outer lock cannot leave them applying
        // a stale mode — it only shortens the hold. (Hoisting the *argument* of a hardware write is a different
        // change and not safe in general: it lets a concurrent writer land first and be overwritten by the stale
        // execution. That is why ApplyCustom and ApplyModeCpuPower's own cp.Set are left alone — see
        // docs/open-decisions.md §3.)
        //
        // The ordering these four had relative to each other is preserved: they are still sequential on this
        // thread, and "clamshell takeover before the option rows read it" (its own comment below) still holds.
        bool clamshell, dynamicLighting;
        int bluelight;
        lock (_state)
        {
            clamshell = Settings.Clamshell;
            bluelight = Settings.Bluelight;
            // Read here rather than at its use below, so this is a guarded read of the graph like every other one
            // in this file. Not a race in practice — this runs before the coordinator, the UI and the 3s pass
            // exist, so there is no writer yet — but the invariant declared above admits no unguarded access.
            dynamicLighting = Settings.DynamicLighting;
        }

        if (clamshell) device.Clamshell?.SetEnabled(true);
        if (bluelight > 0) device.DisplayTint?.Apply(bluelight);

        // The volatile axes, from the one place that knows how to re-apply them: the GPU offsets (the driver
        // zeroed them at boot) and the CPU power overlay are written on THIS thread, and the Curve Optimizer is
        // handed to the pool, because its SMU mailbox transaction waits on a machine-wide PCI lock that other
        // tuning tools also take and can block for seconds — holding _state (or the UI thread) across it would
        // stall the background refresh pass and, through it, the UI. The schedule and the per-axis threads are
        // the reconciler's; this site names only the moment.
        //
        // A throw from either synchronous axis escapes this method to its caller, which is the AppController
        // constructor. That gap is old — it used to be the same two writes by hand — and it is recorded rather
        // than closed: docs/domain-refactoring-plan.md §7 (see also §5, wave 2).
        Reconciler.Reapply(ReapplyTrigger.Startup);

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

    public void Dispose()
    {
        // Tear the virtual LampArray down BEFORE the device: it stops its worker and removes the PnP node, so
        // Windows doesn't keep offering a lighting device this process no longer backs.
        try { _lampArray?.Dispose(); } catch { /* best-effort teardown */ }
        device.Dispose();
    }
}
