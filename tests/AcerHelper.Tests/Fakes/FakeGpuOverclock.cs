using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IGpuOverclock"/>. Unlike fans, the GPU offsets are re-applied on every mode
/// switch, at startup and on resume, so the interesting assertions are "was Set called at all, and with
/// what" — recorded in <see cref="SetCalls"/>. A never-configured mode must still produce <c>Set(0, 0)</c>
/// (the driver zeroes offsets across a reboot; the app is the source of truth).
/// </summary>
public sealed class FakeGpuOverclock : IGpuOverclock
{
    public string Name { get; set; } = "Fake GPU";
    public (int Min, int Max) CoreRange { get; set; } = (-200, 200);
    public (int Min, int Max) MemRange { get; set; } = (-500, 1000);
    public string? LastError { get; set; }
    public bool SetResult { get; set; } = true;

    /// <summary>Every (core, mem) pair handed to <see cref="Set"/>, in order.</summary>
    public List<(int Core, int Mem)> SetCalls { get; } = [];

    public bool Set(int coreMhz, int memMhz)
    {
        SetCalls.Add((coreMhz, memMhz));
        return SetResult;
    }
}

/// <summary>
/// Hand-written <see cref="IGpuPowerEnvelope"/>. The GPU power level is remembered per mode and re-applied on
/// the mode switch, at boot and on resume, so the interesting assertions are "was SetLevel called at all, and
/// with what" — <see cref="SetCalls"/>. The levels are the EC's fixed rows and, like the real port, an
/// out-of-range one is refused without reaching the "hardware" (<see cref="SetCalls"/> stays empty).
/// </summary>
public sealed class FakeGpuPowerEnvelope : IGpuPowerEnvelope
{
    public IReadOnlyList<GpuPowerOption> Levels { get; set; } = AcerPowerRows;

    /// <summary>When false, <see cref="SetLevel"/> records the call and returns false — the EC could not
    /// enqueue it. The write is otherwise "accepted".</summary>
    public bool SetResult { get; set; } = true;

    /// <summary>Every level handed to <see cref="SetLevel"/>, in order, including refused ones.</summary>
    public List<GpuPowerLevel> SetCalls { get; } = [];

    public bool SetLevel(GpuPowerLevel level)
    {
        if (!Levels.Any(o => o.Level == level)) return false;   // refused without reaching the hardware
        SetCalls.Add(level);
        return SetResult;
    }

    /// <summary>The EC's fixed rows, as the real port offers them. Kept here so a test that does not care
    /// about the exact table still gets a realistic one.</summary>
    public static readonly IReadOnlyList<GpuPowerOption> AcerPowerRows =
        [.. GpuPowerLevels.All.Select((lvl, i) => new GpuPowerOption(lvl, $"profile.{lvl}", 108 - i * 10))];
}

/// <summary>
/// Hand-written <see cref="ICpuPower"/>. Follows the FAN contract, not the GPU one: a mode with no stored
/// overlay is left untouched, so "Set was never called" is a meaningful assertion here.
/// </summary>
public sealed class FakeCpuPower : ICpuPower
{
    public IReadOnlyList<ChoiceOption> Modes { get; set; } =
    [
        new("best-efficiency", "Best efficiency"),
        new("balanced", "Balanced"),
        new("best-performance", "Best performance"),
    ];

    /// <summary>The effective overlay's id right now, as <see cref="Current"/> reports it.</summary>
    public string? CurrentId { get; set; }

    public string? LastError { get; set; }
    public bool SetResult { get; set; } = true;

    /// <summary>Every id handed to <see cref="Set"/>, in order, including refused ones.</summary>
    public List<string> SetCalls { get; } = [];

    public string? Current() => CurrentId;

    public bool Set(string id)
    {
        SetCalls.Add(id);
        if (!SetResult) return false;
        CurrentId = id;
        return true;
    }
}
