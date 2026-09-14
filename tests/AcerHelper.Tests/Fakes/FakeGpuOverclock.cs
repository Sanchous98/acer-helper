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
