using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- fans ----

    // The emulated fan-curve controller: one model per fan (Domain/Fan.cs owns the anchors, the default ramp,
    // the interpolation and that fan's last applied duty) plus the paired deadband that decides whether a write
    // happens at all. Custom mode drives the fans through it on the sensor loop; Reset() on any out-of-band
    // change so the deadband can't swallow the first write. Touched by UI-thread SetFan/SetFanCurve and the
    // background ApplyCustom -> guarded by _state. The model is told a fan's data and never which fan it is:
    // the mapping from the stored preset's two halves to the two fans is FanSettingsOf below.
    private readonly FanCurveEngine _fanCurve = new();

    public bool ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        var fc = device.FanControl;
        if (fc == null) return false;
        bool ok;
        if (mode == FanMode.Custom)
        {
            // The EC honours a manual speed only once the fan is in CUSTOM behaviour; setting the speed alone
            // (without switching behaviour out of Auto) is silently ignored. So put the fans in custom mode
            // first, then push the speeds. (Both Custom and the emulated curve go through here.)
            fc.SetMode(FanMode.Custom);
            ok = fc.SetCustomSpeeds(cpu, gpu);
        }
        else ok = fc.SetMode(mode);
        // No error channel here on purpose: every caller of ApplyFan is void (SetFan, SetFanCurve, ApplyCustom,
        // ApplyModeFan), so there has never been a reader for this failure — and giving it one now would start
        // showing the user a message the app has never shown.
        return ok;
    }

    /// <summary>The stored preset for the current mode, created on first write (user is configuring it).
    /// Caller holds _state.</summary>
    private FanPreset StoredFan() => GetOrAdd(Settings.FanPresets, CurrentModeKey());

    /// <summary>The two fans' own settings, read out of their halves of the stored preset. THIS IS WHERE A
    /// FAN'S IDENTITY IS DECIDED: the schema keeps both fans in one object and its halves are named Cpu and
    /// Gpu, so "this one is the GPU fan" is a fact this layer establishes and the domain models are never told
    /// — Domain/Fan.cs has no CPU/GPU flag to set. It and <see cref="AxisStateOf"/> are the ONLY readers of the
    /// frozen field layout, and both live in this file, so a reader of the other half cannot be mistaken for
    /// this one; it was the seam that let the stored container move to Infrastructure without touching Domain,
    /// and it is what keeps Domain naming no stored shape now that the container sits beside this file.
    ///
    /// WHAT CROSSES IS THE MODEL'S OWN TYPE, and constructing it is what brings the stored values inside the
    /// domain's rules: <see cref="FanSettings"/> refuses a curve that is not one duty% per anchor and clamps the
    /// fixed speed into the duty range, so neither a curve nor a speed a hand-edited file can hold reaches the
    /// EC by passing through here. The array this hands over is a COPY — the stored preset's array is not
    /// aliased into the model, which is the other half of the same rule (and the reason
    /// <see cref="IFanAxisTarget.Stored"/> no longer hands the model's array back to a caller). Caller holds
    /// _state or passes a snapshot it owns.</summary>
    private static (FanSettings cpu, FanSettings gpu) FanSettingsOf(FanPreset f)
        => (new FanSettings(f.CpuUseCurve, f.CpuCurve, f.Cpu),
            new FanSettings(f.GpuUseCurve, f.GpuCurve, f.Gpu));

    /// <summary>The behaviour a stored preset is in — THE ONE READING of the persisted <c>int</c>, so no other
    /// member of this class casts it.
    ///
    /// AN UNDEFINED STORED VALUE IS READ AS <see cref="FanMode.Auto"/>, and that is a decision rather than a
    /// fallback. A stored <c>int</c> that names no <see cref="FanMode"/> (a hand-edited file, or one written by
    /// an older build that knew a value this one does not) describes no behaviour the app configured, so what it
    /// must NOT do is travel on: the Windows port shifts the behaviour into the WMI argument it sends the EC, so
    /// a stored 7 was written to the hardware as byte 7. Auto is the answer because it is the one mode
    /// <see cref="FanCapability"/> guarantees exists (<c>Auto is always implied</c>), because it is the
    /// behaviour the firmware is in when the app has configured nothing — which is exactly the state an
    /// unrecognised stored value describes — and because every port maps it. Refusing the write instead would
    /// have to report a mode to the UI anyway, since <see cref="FanAxisState.Mode"/> has no "unknown", and would
    /// leave a preset the user CAN see in the file with no behaviour at all.</summary>
    private static FanMode FanModeOf(FanPreset f) => FanModes.FromStored(f.Mode) ?? FanMode.Auto;

    /// <summary>The same reading as <see cref="FanSettingsOf"/>, for a whole preset: the axis's state in the
    /// DOMAIN's vocabulary (<see cref="FanAxisState"/>, Domain/AxisState.cs), which is what a use case in
    /// Application may receive — it may not name the stored preset. The mode is read here too, and it is the
    /// one field the UI used to cast for itself: the stored form keeps it as an <c>int</c> because that is what
    /// <c>settings.json</c> holds, and this is where that stops being the rest of the tree's business.
    ///
    /// It takes a preset that EXISTS; a mode with no stored preset is the caller's own null to pass on, which
    /// the fan axis reports as "nothing to show".</summary>
    internal static FanAxisState AxisStateOf(FanPreset f)
    {
        var (cpu, gpu) = FanSettingsOf(f);
        return new FanAxisState(FanModeOf(f), cpu, gpu);
    }

    /// <summary>Set the fan mode + fixed speeds for the CURRENT mode and apply now. Per-fan curve settings are
    /// preserved; in Custom mode a fan's real speed is its curve value when that fan's curve is on, else the
    /// fixed speed set here (see <see cref="ApplyCustom"/>).
    ///
    /// WHAT IS STATED HERE AND WHAT MOVED. Which fans a selection touches, and that it must not clobber the curves
    /// — the rule — is <see cref="ApplyFanSelection"/> (Application), and the state it edits crosses as
    /// <see cref="FanAxisState"/>. What stays is the graph write, the deadband and the EC, reached through
    /// <see cref="IFanAxisTarget.ReplaceSelection"/>, which is the member below.</summary>
    public void SetFan(FanMode mode, byte cpu, byte gpu)
        => ApplyFanSelection.Run(mode, cpu, gpu, this);

    /// <summary>Turn one fan's curve on/off and store its points, for the CURRENT mode, then apply now. The rule
    /// — which fan's half an edit names, and that the other half and the mode survive it — is
    /// <see cref="ApplyFanCurve"/> (Application); the write is <see cref="IFanAxisTarget.ReplaceCurve"/>.</summary>
    public void SetFanCurve(bool gpu, bool use, int[] points)
        => ApplyFanCurve.Run(gpu, use, points, this);

    // ---- the fan edit contract (Application/FanAxis.cs) ----

    /// <summary>The current mode's preset in the domain's vocabulary, creating it if the user has never
    /// configured this mode — which is what both edit paths mean by reading it. NOT a snapshot, and deliberately:
    /// the two use cases hand the whole state straight back, so a copy would re-point the preset's curve arrays at
    /// the copy on every edit, including the edit that changed no array (<see cref="IFanAxisTarget.Stored"/>).</summary>
    FanAxisState IFanAxisTarget.Stored()
    {
        lock (_state) return AxisStateOf(StoredFan());
    }

    /// <summary>The curve edit: store, clear the deadband, save, and drive the curves — all in one hold, which is
    /// the shape both edit paths have always had and one of the three deliberate holds
    /// (docs/open-decisions.md §3: a background <c>ApplyCustom</c> must not be able to land between its
    /// <c>Step</c> and its <c>Commit</c>, and a deadband cleared outside this hold could be re-armed by one).
    ///
    /// NOTHING REACHES THE EC WHEN THE MODE IS NOT CUSTOM, and that is not a shortcut: <see cref="ApplyCustom"/>'s
    /// own guard returns before it touches the port, so a curve edited in Auto or Max produces no EC traffic at
    /// all — and pushing the mode here instead would be a write this path has never made.</summary>
    void IFanAxisTarget.ReplaceCurve(FanAxisState state)
    {
        lock (_state)
        {
            FileFan(state);
            _fanCurve.Reset();
            Save();
            ApplyCustom(ReadSensors());
        }
    }

    /// <summary>The selection edit: the same store, deadband clear and save, then the EC is asked for the mode —
    /// or, when the new selection IS Custom, driven from the curves instead, because the EC honours a manual speed
    /// only once the fans are already in Custom behaviour and would silently ignore the speeds otherwise.
    ///
    /// THE <c>(byte)</c> CASTS ARE TOTAL, which is what changed here. They are casts of the state's own
    /// <see cref="FanSettings.FixedDuty"/>, and that field is clamped at construction, so no
    /// <see cref="FanAxisState"/> can carry a fixed duty outside 0..100 — where they used to be legal casts of
    /// ANY <c>int</c> the file happened to hold. The clamp sits with the invariant rather than at this call site
    /// so that no caller of this member has anything to remember to do.
    ///
    /// WHAT IS AND IS NOT OBSERVABLE HERE, measured rather than assumed: <c>ApplyFan</c> discards the two speeds
    /// unless the mode is Custom, and the Custom arm above goes through the curve evaluation instead, so these
    /// bytes never reached the wire out of range even before the fix. The defect was a bypass that a clamp one
    /// call further down happened to cover; what the fix makes observable is the stored preset, which no longer
    /// keeps a 300 nothing can act on (pinned by
    /// <c>LaptopServiceApplyModeGraphTests.AFixedSpeedOutsideTheDutyRange_IsBroughtInsideIt_WhenThePresetIsWritten</c>).</summary>
    void IFanAxisTarget.ReplaceSelection(FanAxisState state)
    {
        lock (_state)
        {
            FileFan(state);
            _fanCurve.Reset();
            Save();
            if (state.Mode == FanMode.Custom) ApplyCustom(ReadSensors());
            else ApplyFan(state.Mode, (byte)state.Cpu.FixedDuty, (byte)state.Gpu.FixedDuty);
        }
    }

    /// <summary>Write a whole <see cref="FanAxisState"/> into the stored preset, keeping the stored INSTANCE —
    /// a second write must re-use the preset rather than replace it, so a preset the model already holds a copy of
    /// in its dictionary is not swapped for a new object (LaptopServicePresetTests' "A SECOND WRITE re-uses the
    /// stored preset").
    ///
    /// The fixed speeds are written WITHOUT the byte cast the port takes: the stored field is an <c>int</c>
    /// (settings.json is hand-editable) and a value read back out of it must survive an edit that did not name it.
    /// The cast belongs where the EC is asked, which is the two call sites above — and both of those are total
    /// because the state they cast was built by <see cref="FanSettings"/>, which clamps.
    ///
    /// The arrays are assigned BY REFERENCE FROM THE STATE, which is still a reference assignment and is no
    /// longer an alias of anything the caller can reach: the state's curve was copied by
    /// <see cref="FanSettings"/>'s constructor, so what lands in the preset is a fresh array that belongs to the
    /// preset. The stored curve is therefore never the UI's array and never the model's — a later edit to one of
    /// them cannot rewrite the user's file, and the file's own array cannot be rewritten by a caller that held
    /// the state it was read from.</summary>
    private void FileFan(FanAxisState state)
    {
        var f = StoredFan();
        f.Mode = (int)state.Mode;
        f.Cpu = state.Cpu.FixedDuty; f.Gpu = state.Gpu.FixedDuty;
        f.CpuUseCurve = state.Cpu.UseCurve; f.GpuUseCurve = state.Gpu.UseCurve;
        f.CpuCurve = state.Cpu.Curve; f.GpuCurve = state.Gpu.Curve;
    }

    /// <summary>The fan preset for the current mode, or defaults if none is saved yet (not stored). A SNAPSHOT:
    /// the caller cannot reach the stored instance through it, so a view-model that keeps the value cannot
    /// rewrite the user's settings behind <c>_state</c>'s back.</summary>
    public FanPreset CurrentFan()
    {
        lock (_state)
            return Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) ? f.Snapshot() : new FanPreset();
    }

    /// <summary>As <see cref="CurrentFan()"/> but reusing an already-read current profile, so a caller that has
    /// just read it pays no second EC round-trip for the key. Same pair, and for the same reason, as
    /// <see cref="CurrentModeKey(PerformanceProfile?)"/> and <see cref="LightsForCurrentMode(PerformanceProfile?)"/>.
    ///
    /// The lock keeps its exact former scope: the key is derived from the caller's profile and the preset is
    /// looked up under one <c>_state</c> hold, which is the pairing docs/open-decisions.md §3 protects. What is
    /// gone is only the port read — the parameterless form still reads it under the lock, deliberately.</summary>
    public FanPreset CurrentFan(PerformanceProfile? cur)
    {
        lock (_state)
            return Settings.FanPresets.TryGetValue(CurrentModeKey(cur), out var f) ? f.Snapshot() : new FanPreset();
    }

    /// <summary>Apply the current mode's saved fan preset on a mode change. Auto/Max are pushed immediately;
    /// Custom is left to <see cref="ApplyCustom"/> (the refresh loop) so per-fan curves track temperature.
    /// Returns the preset so the UI reflects it, or null if this mode has none (fans left untouched) — a
    /// SNAPSHOT, like <see cref="CurrentFan"/>, and for the same reason.</summary>
    public FanPreset? ApplyModeFan()
    {
        lock (_state)
        {
            if (!Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f)) return null;
            _fanCurve.Reset();
            var mode = FanModeOf(f);
            if (mode != FanMode.Custom)
            {
                // The SPEEDS come from the model's reading of the preset and not from the stored ints, for the
                // reason the mode does: a stored 300 cast to a byte is 44. FanSettingsOf is what clamps, and it
                // is asked here rather than the raw fields read, so this path cannot be the one that forgets.
                var (cpu, gpu) = FanSettingsOf(f);
                ApplyFan(mode, (byte)cpu.FixedDuty, (byte)gpu.FixedDuty);
            }
            return f.Snapshot();
        }
    }

    /// <summary>Drive the fans in Custom mode: each fan uses its curve value (mapped from the live temp) when
    /// its curve is on, otherwise its fixed speed. Applied with a deadband so the fans don't hunt. A no-op
    /// outside Custom mode. Called every refresh (reuses the already-read sensors — no extra hardware access).</summary>
    public void ApplyCustom(SensorSnapshot s)
    {
        lock (_state)
        {
            if (device.FanControl == null ||
                !Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) || FanModeOf(f) != FanMode.Custom)
            { _fanCurve.Reset(); return; }

            if (_fanCurve.Step(FanSettingsOf(f), s) is not { } duty) return;        // within deadband
            if (ApplyFan(FanMode.Custom, (byte)duty.cpu, (byte)duty.gpu))
                _fanCurve.Commit(duty.cpu, duty.gpu);                               // advance state only on success
        }
    }
}
