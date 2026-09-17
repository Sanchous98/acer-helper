namespace AcerHelper.Domain;

/// <summary>Emulated fan-curve controller. Acer has no native fan curves, so in Custom mode the app drives
/// each fan's duty from the curve that fan's <see cref="Fan"/> model carries, evaluated against live temps on
/// the sensor loop.
///
/// WHAT IS LEFT HERE, and why it is not part of the model: the PAIR. Two <see cref="Fan"/> models are held and
/// the one rule that belongs to them together rather than to either one — the deadband is decided on both fans
/// at once, and when it is left BOTH duties are returned, so a fan that did not need to move is written
/// anyway. The pairing follows the port's shape: <see cref="IFanControl.SetCustomSpeeds"/> takes a pair, and
/// the caller writes exactly the pair this returns (LaptopService.ApplyCustom applies it and commits it), so
/// decomposing the decision per fan would change what the EC receives. It is also pinned as today's behaviour
/// in tests/AcerHelper.Tests/FanCurveEngineStepTests.cs. This class never touches the port itself.
///
/// State advances only when the caller confirms an apply via <see cref="Commit"/>, so a failed hardware write
/// is retried on the next step rather than silently swallowed by the deadband. <see cref="Reset"/> is for any
/// out-of-band change (mode/preset switch), so the deadband can't suppress the first legitimate write.</summary>
public sealed class FanCurveEngine
{
    private readonly Fan _cpuFan = new(gpu: false);
    private readonly Fan _gpuFan = new(gpu: true);

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
    /// <see cref="Commit"/>.</summary>
    public (int cpu, int gpu)? Step(FanPreset f, SensorSnapshot s)
    {
        int cpu = _cpuFan.Duty(f, s.CpuTempC);
        int gpu = _gpuFan.Duty(f, s.GpuTempC);
        // The deadband, verbatim as it has always been: strictly less than 4 units of movement on EITHER fan
        // suppresses the write, and the test on the CPU's memory is the only one that asks whether a reference
        // pair exists at all (docs/open-decisions.md, "Известные особенности" 3: a state with the CPU
        // committed and the GPU not is unreachable, because Commit writes both memories and Reset clears both).
        if (_cpuFan.LastApplied >= 0 && Math.Abs(cpu - _cpuFan.LastApplied) < 4 && Math.Abs(gpu - _gpuFan.LastApplied) < 4)
            return null;
        return (cpu, gpu);
    }
}
