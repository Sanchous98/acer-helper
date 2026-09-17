using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

public sealed partial class LaptopService
{
    // ---- fans ----

    // The emulated fan-curve controller: one model per fan (Domain/Fan.cs owns the anchors, the default ramp,
    // the interpolation and that fan's last applied duty) plus the paired deadband that decides whether a write
    // happens at all. Custom mode drives the fans through it on the sensor loop; Reset() on any out-of-band
    // change so the deadband can't swallow the first write. Touched by UI-thread SetFan/SetFanCurve and the
    // background ApplyCustom -> guarded by _state.
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

    /// <summary>Set the fan mode + fixed speeds for the CURRENT mode and apply now. Per-fan curve settings are
    /// preserved; in Custom mode a fan's real speed is its curve value when that fan's curve is on, else the
    /// fixed speed set here (see <see cref="ApplyCustom"/>).</summary>
    public void SetFan(FanMode mode, byte cpu, byte gpu)
    {
        lock (_state)
        {
            var f = StoredFan();
            f.Mode = (int)mode; f.Cpu = cpu; f.Gpu = gpu;
            _fanCurve.Reset();
            Save();
            if (mode == FanMode.Custom) ApplyCustom(ReadSensors());
            else ApplyFan(mode, cpu, gpu);
        }
    }

    /// <summary>Turn one fan's curve on/off and store its points, for the CURRENT mode, then apply now.</summary>
    public void SetFanCurve(bool gpu, bool use, int[] points)
    {
        lock (_state)
        {
            var f = StoredFan();
            if (gpu) { f.GpuUseCurve = use; f.GpuCurve = points; }
            else     { f.CpuUseCurve = use; f.CpuCurve = points; }
            _fanCurve.Reset();
            Save();
            ApplyCustom(ReadSensors());
        }
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
            if ((FanMode)f.Mode != FanMode.Custom) ApplyFan((FanMode)f.Mode, (byte)f.Cpu, (byte)f.Gpu);
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
                !Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) || (FanMode)f.Mode != FanMode.Custom)
            { _fanCurve.Reset(); return; }

            if (_fanCurve.Step(f, s) is not { } duty) return;                       // within deadband
            if (ApplyFan(FanMode.Custom, (byte)duty.cpu, (byte)duty.gpu))
                _fanCurve.Commit(duty.cpu, duty.gpu);                               // advance state only on success
        }
    }
}
