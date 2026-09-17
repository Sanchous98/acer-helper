namespace AcerHelper.Domain;

/// <summary>Emulated fan-curve controller. Acer has no native fan curves, so in Custom mode the app drives
/// each fan's duty from the curve that fan's <see cref="Fan"/> model carries, evaluated against live temps on
/// the sensor loop. Each fan's own data — its curve, whether that curve is on, its fixed duty — arrives with
/// every <see cref="Step"/>, because the caller reads it live; what stays here is the two <see cref="Fan"/>
/// models and the one rule that belongs to them together.
///
/// WHAT IS LEFT HERE, and why it is not part of the model: the PAIR. Two <see cref="Fan"/> models are held and
/// the one rule that belongs to them together rather than to either one — the deadband is decided on both fans
/// at once, and when it is left BOTH duties are returned, so a fan that did not need to move is written
/// anyway. The pairing follows the port's shape: <see cref="IFanControl.SetCustomSpeeds"/> takes a pair, and
/// the caller writes exactly the pair this returns (LaptopService.ApplyCustom applies it and commits it), so
/// decomposing the decision per fan would change what the EC receives. It is also pinned as today's behaviour
/// in tests/AcerHelper.Tests/FanCurveEngineStepTests.cs. This class never touches the port itself.
///
/// The caller also decides WHICH fan is which: the pair arrives as two fans' settings with no CPU/GPU naming
/// on the model side, and the layer that reads the persisted preset is the one that knows which half goes to
/// which fan (LaptopService.Fans.FanSettingsOf). Nothing in Domain names a fan's identity or the stored shape.
///
/// State advances only when the caller confirms an apply via <see cref="Commit"/>, so a failed hardware write
/// is retried on the next step rather than silently swallowed by the deadband. <see cref="Reset"/> is for any
/// out-of-band change (mode/preset switch), so the deadband can't suppress the first legitimate write.</summary>
public sealed class FanCurveEngine
{
    // A brand-new engine has been told nothing yet, so both fans start on DEFAULT settings (curve off, fixed
    // 0%). That placeholder is unobservable: Step replaces both fans' data before anything asks a fan for a
    // duty, and these fields are private. Step cannot be reached without the pair at all — the data travels
    // with the call, exactly as the preset it is read from did — so there is no path that leaves a fan
    // answering from the placeholder.
    private readonly Fan _cpuFan = new(default);
    private readonly Fan _gpuFan = new(default);

    /// <summary>Clear the hysteresis state so the next <see cref="Step"/> applies unconditionally.</summary>
    public void Reset()
    {
        _cpuFan.Reset();
        _gpuFan.Reset();
    }

    /// <summary>Record the duties the caller actually applied to the hardware (call only on a successful
    /// write, so the deadband references what the fans are really at).</summary>
    public void Commit(int cpu, int gpu)
    {
        _cpuFan.Commit(cpu);
        _gpuFan.Commit(gpu);
    }

    /// <summary>The (cpu, gpu) duty for the live temps, or null when it's within the deadband of the last
    /// committed pair (no change needed). Does NOT update state — the caller applies the result and then calls
    /// <see cref="Commit"/>.
    ///
    /// Each fan is handed its own <see cref="FanSettings"/> here: the caller has just read them, and it is the
    /// caller that knows which of the two is the CPU fan and which is the GPU one. The models are told about a
    /// fan and never about a slot.</summary>
    public (int cpu, int gpu)? Step((FanSettings cpu, FanSettings gpu) fans, SensorSnapshot s)
    {
        _cpuFan.Configure(fans.cpu);
        _gpuFan.Configure(fans.gpu);
        var cpu = _cpuFan.Duty(s.CpuTempC);
        var gpu = _gpuFan.Duty(s.GpuTempC);
        // The deadband, verbatim as it has always been: strictly less than 4 units of movement on EITHER fan
        // suppresses the write, and the test on the CPU's memory is the only one that asks whether a reference
        // pair exists at all (docs/open-decisions.md, "Известные особенности" 3: a state with the CPU
        // committed and the GPU not is unreachable, because Commit writes both memories and Reset clears both).
        if (_cpuFan.LastApplied >= 0 && Math.Abs(cpu - _cpuFan.LastApplied) < 4 && Math.Abs(gpu - _gpuFan.LastApplied) < 4)
            return null;
        return (cpu, gpu);
    }
}
