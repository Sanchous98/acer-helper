using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>
/// The hardware-facing service the UI talks to: it owns the machine composition built (<see cref="Device"/>), the
/// settings graph and the store that reads and writes it, and it talks to the Domain feature ports on the
/// machine's behalf. All orchestration (profile cycling/toggling, persistence of changes) lives here, never in
/// the UI.
///
/// IT IS INFRASTRUCTURE BY THE OWNER'S RULING, and the reason is worth keeping beside the class: the name says
/// what it is — a service over hardware and the form settings are kept in, not a use case — and it is also what
/// names the persisted container (FanPreset, CoPreset, …) and calls the store that builds it. Application holds
/// the re-apply use case (<c>Application/ReapplyPlan.cs</c>: the plan and, since the outcome stopped being built
/// out of the container, the loop that walks it), and this class's <see cref="Reconciler"/> implements the
/// contract that loop drives — it is where the axis writes and the preset translation live. The other contracts
/// Application declares live in <c>Application/</c> beside it, one per applied axis plus the lighting door and
/// the declared-setting shapes (<c>FanAxis.cs</c>, <c>GpuOffsets.cs</c>, <c>CpuPowerOverlay.cs</c>,
/// <c>Undervolt.cs</c>, <c>DeclaredSetting.cs</c>, <c>LightZone.cs</c>).
///
/// Split across partial files by feature, because one 814-line file was the only place this
/// layer could be read. This part holds identity and the shared infrastructure every other
/// part depends on — the fields, the <c>_state</c> lock and its <see cref="Save"/> companion,
/// the scalar accessors the UI is allowed to read, <see cref="ApplyStartupState"/>, the
/// per-mode preset helper, and teardown. The rest:
/// <list type="bullet">
/// <item><c>LaptopService.Lighting.cs</c> — the per-mode light zones and the door the UI edits them through.</item>
/// <item><c>LaptopService.Profiles.cs</c> — performance profiles, Turbo, power source.</item>
/// <item><c>LaptopService.Fans.cs</c> — the fan presets and the fan-curve engine.</item>
/// <item><c>LaptopService.Tuning.cs</c> — GPU overclock, CPU power mode, Curve Optimizer.</item>
/// <item><c>LaptopService.Toggles.cs</c> — one-shot hardware toggles, device flags, language.</item>
/// <item><c>LaptopService.Cardwire.cs</c> — the app's own GPU access through cardwire, the one capability here
/// that is not a device this machine's firmware owns.</item>
/// </list>
/// The split is a MOVE, not a reorganisation: no method body changed and the lock instance and
/// its coverage are the same object they were, so the concurrency contract below is unchanged.
/// The names are suffixed <c>.Profiles.cs</c> rather than split on the class per feature because
/// the build already selects files by suffix (<c>*.Windows.cs</c>/<c>*.Linux.cs</c> in
/// AcerHelper.csproj) — none of these collide with that, and none may ever be renamed into it.
///
/// WHAT IT IMPLEMENTS. Five of Application's contracts, one per thing the owner's model applies
/// (Application/FanAxis.cs, GpuOffsets.cs, CpuPowerOverlay.cs, Undervolt.cs, DeclaredSetting.cs), plus the
/// re-apply's (<see cref="HardwareReconciler"/>, which is this class's own executor). All five are implemented
/// EXPLICITLY, so not one member is added to this class's public surface — the surface the retired analysis
/// measured as too wide, 62 members plus a constructor, a count kept in docs/open-decisions.md's note on that
/// retirement — and a caller reaches them only by naming the use case. The use cases own the rules (which half of a fan an edit touches, what an absent preset means, what
/// is remembered before it is written); this class owns the graph and the ports, which is why the rules could move
/// and the graph could not.
///
/// THE SIXTH IS THE LIGHTING, and it is implemented the other way round: the door is taken by
/// <see cref="LightsForCurrentMode()"/>, which RETURNS the contract (<see cref="ILightZoneMode"/>) rather than
/// this class implementing its members, because the door has to carry the mode key and this class has no single
/// lighting mode to be. What that member used to hand out — the stored zone dictionary itself, edited in place
/// by the UI — is what the owner overruled; see Infrastructure/Composition/LaptopService.Lighting.cs.
/// </summary>
public sealed partial class LaptopService : IDisposable,
    IFanAxisTarget, IGpuOffsetsTarget, ICpuPowerOverlayTarget, IUndervoltTarget, IDeclaredSettingTarget
{
    // Explicit fields instead of a primary constructor. A primary constructor's parameters are in scope only in
    // the part that declares them, so while one was in use this class could not be split across partial files
    // (LaptopService.*.cs) — which is the point of the change. The field names are deliberately the former
    // parameter names, so not one call site in the file moved.
    private readonly Device device;
    private readonly ISettingsStore store;

    public LaptopService(Device device, ISettingsStore store)
    {
        this.device = device;
        this.store = store;
        Reconciler = new HardwareReconciler(this);
        // Assigned in the body rather than as `Settings { get; } = store.Load(...)`. Field and property initializers
        // run BEFORE the body, so as an initializer this read `store` while it was still null. No initializer in
        // this class reads Settings, so loading it here rather than there is observably the same order.
        //
        // ...and the settings this machine declares are handed to the model that holds and switches them
        // (Infrastructure/Composition/Settings.cs) THROUGH ITS CONSTRUCTOR: the store builds the model with this set, so the set is
        // fixed before the model exists rather than installed into it afterwards. The set is read OFF THE MACHINE
        // — the backend's probe is what found it, and a machine holds what was declared about it — so nothing has
        // to carry the list here beside the device, which is what DeviceFactory's old tuple was for.
        //
        // The machine's list is read HERE, at construction, and the model copies it — so a backend that kept
        // declaring after this line would be declaring into nothing. Production already had that order (a vendor
        // declares inside InitVendor, before the service exists); the tests' fakes declare before the fixture
        // builds this, on the same terms.
        Settings = store.Load(device.DeclaredSettings);
    }

    /// <summary>The one operation that re-applies volatile state, shared by every site that needs it: this
    /// class's own <see cref="ApplyStartupState"/>, <c>AppController</c>'s refresh pass and
    /// <c>LightingCoordinator</c>'s resume handler. It holds no state of its own — it is one instance so that
    /// there is one place that knows HOW TO WRITE each axis, not to share anything. What a re-apply IS (which
    /// axes, in what order, on what thread) is Application's and is read from there (see
    /// <see cref="HardwareReconciler"/> and Application's <c>ReapplySettings</c>).</summary>
    internal HardwareReconciler Reconciler { get; }

    /// <summary>The machine composition built. The UI reads its (nullable) feature ports to decide which
    /// sections to show; it must route all mutations through this service's methods.</summary>
    public Device Device => device;

    /// <summary>The mutable settings graph. <c>internal</c> as a SIGNPOST, not a fence — and the difference
    /// matters, so it is stated rather than implied. This is ONE assembly: <c>AcerHelper.csproj</c> compiles
    /// Domain/, Application/, Infrastructure/, UI/ and Bootstrap/ together, so <c>internal</c> is visible to
    /// every one of them and stops nothing inside this repo. What it does is take the property off the public
    /// surface, so it stops reading as an invitation, and it makes the eventual assembly split — the only thing
    /// that WOULD enforce a layer boundary, see the plan's Wave 1 note — mechanical instead of a redesign.
    ///
    /// NO ACCESSOR HANDS OUT A LIVE REFERENCE INTO IT ANY MORE, which is worth stating because the list that used
    /// to be here was the honest exception to that claim. The six fan/GPU/CPU/Co accessors hand back
    /// <c>Snapshot()</c>; the two lighting ones — both forms of <c>LightsForCurrentMode</c> and
    /// <c>EnsureLightZone</c> — used to hand back the stored zone dictionary itself, which
    /// <c>LightingViewModel</c> kept and wrote into in place, and that was a RECORDED decision
    /// (docs/open-decisions.md §4) rather than an oversight. The owner overruled it, and the per-mode lighting
    /// now crosses as <see cref="ILightZoneMode"/> with <see cref="LightZoneState"/> values
    /// (Infrastructure/Composition/LaptopService.Lighting.cs); <c>EnsureLightZone</c> is gone with it.
    /// So what is left beyond the property itself is the <c>internal</c> keyword — a signpost, as above.</summary>
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
    /// user messages the app has never shown, which is a feature, not a refactor.
    ///
    /// <paramref name="reason"/> is fetched ONLY from a write that RETURNED. A port that THROWS reports no reason
    /// — deliberately, because the reason it would be read from is a field the port owns and assigns at the end
    /// of a call that runs to completion, so a throw leaves it holding an EARLIER call's words. Reporting those
    /// is the exact failure docs/open-decisions.md §2 exists to remove ("a reader picking up ANOTHER call's
    /// error"), and this wrapper was the one place that still did it after <c>FlagSetting.Write</c> and
    /// <c>ChoiceSetting.Write</c> (Domain/DeclaredSetting.cs) were corrected. A refusal that RETURNED keeps its
    /// reason, which is how every real transport in this tree reports (see the measurement on
    /// <c>FlagSetting.Write</c>).</summary>
    private static (bool ok, string? error) Attempt(Func<bool> write, Func<string?> reason)
    {
        bool ok;
        try { ok = write(); }
        catch { return (false, null); }   // a throw assigns nothing: LastError still belongs to an earlier call
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
        bool clamshell;
        int bluelight;
        lock (_state)
        {
            clamshell = Settings.Clamshell;
            bluelight = Settings.Bluelight;
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
    }

    /// <summary>Look up <paramref name="key"/> in <paramref name="map"/>, creating and inserting a default
    /// instance when it is absent — the per-mode "preset, created on first write" idiom the Stored* and
    /// per-mode accessors above all share, and the one the lighting door's mode bucket is created with
    /// (LaptopService.Lighting.cs, <c>LightZoneMode.Bucket</c>).</summary>
    private static T GetOrAdd<T>(Dictionary<string, T> map, string key) where T : new()
    {
        if (!map.TryGetValue(key, out var value)) map[key] = value = new T();
        return value;
    }

    public void Dispose()
    {
        // Let an in-flight blue-light apply finish before the machine is torn down. The apply writes the
        // compositor's own configuration, and the tint link's teardown — its IDisposable, reached through
        // device.Dispose() below — restores the user's values out of that same configuration and verifies the
        // release against the compositor. Left to interleave, a roll-back could land after the restore and leave
        // the markers gone with a tint still applied, which is the one thing the link promises never to leave
        // behind. Until the apply moved off the UI thread this could not happen — both ran on the thread that is
        // now exiting — so the wait is what keeps the new schedule from opening the hole. Bounded, and a timeout
        // is not an error here: teardown goes on either way.
        try { _tintApplies.Drain(TintDrainTimeout); } catch { /* best-effort teardown */ }

        device.Dispose();
    }
}
