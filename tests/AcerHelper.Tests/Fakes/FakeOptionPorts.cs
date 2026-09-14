using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written ports for <c>OptionsAssembler</c>: each is the real port's two-way shape (a write that can
/// fail, a read the test controls) plus the failure modes a fake needs to let a test CHOOSE — a refused write
/// (<see cref="FakeFlagPort.SetResult"/>/<see cref="FakeChoicePort.SetResult"/>) and, separately, a write that
/// THROWS, which the ports survive by different paths (see <see cref="FakeThrowingPowerProfiles"/>).
///
/// <see cref="FakeFlagPort"/> implements every on/off feature port at once, and <see cref="FakeChoicePort"/>
/// every pick-one-of-N port, because in Features/Ports.cs each of them is exactly <see cref="IFlagPort"/> /
/// <see cref="IChoicePort"/> and nothing more — so one fake is dropped into whichever slot the row under test
/// reads, and a test says which row it means by its label rather than by its port type.
///
/// Reads are COUNTED: the Options rows hand the user a <c>Read</c> delegate the view-model calls after every
/// write, so "did the row ask the hardware again?" is an assertion, not an implementation detail.
/// </summary>
public sealed class FakeFlagPort : ILcdOverdrive, IKeyboardBacklight, IFnLock,
                                   IBatteryChargeLimit, IBatteryCalibration
{
    /// <summary>What <see cref="Get"/> reports. Mutate it to act like the hardware changed under the app.</summary>
    public bool State { get; set; }

    /// <summary>When false, <see cref="Set"/> records the call, leaves <see cref="State"/> unchanged and
    /// returns false — the EC-refused case.</summary>
    public bool SetResult { get; set; } = true;

    /// <summary>When true, <see cref="Set"/> throws instead of returning. A port that throws is a different
    /// failure path from one that returns false (see <see cref="FakeThrowingPowerProfiles"/>), and the row
    /// must survive it either way.</summary>
    public bool ThrowOnSet { get; set; }

    public string? LastError { get; set; }

    /// <summary>Number of <see cref="Get"/> calls. The row reads once when it is built (its
    /// <c>Initial</c>), so a readback test asserts on the count AFTER that.</summary>
    public int GetCount { get; private set; }

    /// <summary>Every value handed to <see cref="Set"/>, in order, including refused ones.</summary>
    public List<bool> SetCalls { get; } = [];

    public bool Get()
    {
        GetCount++;
        return State;
    }

    public bool Set(bool on)
    {
        SetCalls.Add(on);
        if (ThrowOnSet) throw new InvalidOperationException("FakeFlagPort: the write blew up");
        if (!SetResult) return false;
        State = on;
        return true;
    }
}

/// <summary>
/// Hand-written <see cref="IChoicePort"/> for the dropdown rows. Every id doubles as its display name, because
/// no Options assertion depends on the option LABELS — only on which id the row maps to which dropdown INDEX
/// (OptionsAssembler's <c>IndexOf</c>), which is the value the view-model puts back into the combo box.
/// </summary>
public sealed class FakeChoicePort : IUsbCharging, IKeyboardBacklightTimeout, IBatteryChargeMode
{
    public FakeChoicePort(params string[] ids)
        => Options = [.. ids.Select(id => new ChoiceOption(id, id))];

    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <summary>What <see cref="Get"/> reports — the id the hardware is "in" right now, or null for
    /// "unreadable". Set it directly to arrange a readback scenario.</summary>
    public string? CurrentId { get; set; }

    /// <summary>When false, <see cref="Set"/> records the call, leaves <see cref="CurrentId"/> unchanged and
    /// returns false — the EC-refused case.</summary>
    public bool SetResult { get; set; } = true;

    public string? LastError { get; set; }

    /// <summary>Number of <see cref="Get"/> calls (the row reads once when it is built).</summary>
    public int GetCount { get; private set; }

    /// <summary>Every id handed to <see cref="Set"/>, in order, including refused ones.</summary>
    public List<string> SetCalls { get; } = [];

    public string? Get()
    {
        GetCount++;
        return CurrentId;
    }

    public bool Set(string id)
    {
        SetCalls.Add(id);
        if (!SetResult) return false;
        CurrentId = id;
        return true;
    }
}

/// <summary>Hand-written <see cref="IDisplayTint"/>: discrete blue-light levels and a recorded
/// <see cref="Apply"/>, so a test can tell a pick that reached the hardware from one that only moved a
/// dropdown. <c>Levels == 0</c> is the "this display has no tint control" case that suppresses the row.</summary>
public sealed class FakeDisplayTint(int levels = 5) : IDisplayTint
{
    public int Levels { get; set; } = levels;

    /// <summary>Every level handed to <see cref="Apply"/>, in order.</summary>
    public List<int> ApplyCalls { get; } = [];

    public bool ApplyResult { get; set; } = true;

    public bool Apply(int level)
    {
        ApplyCalls.Add(level);
        return ApplyResult;
    }
}

/// <summary>
/// Hand-written <see cref="IPowerProfiles"/> whose <see cref="Set"/> ALWAYS THROWS. It exists for one path:
/// <see cref="FakePowerProfiles"/> can refuse a write but cannot blow up, and a throwing write reaches
/// <c>OptionsAssembler.RunSet</c>'s own <c>catch</c> only from here — the flag and choice ports never get that
/// far, because <c>LaptopService.Run</c> catches their throw one layer below RunSet (LaptopService.cs:119-127),
/// while a profile write travels from <c>ApplyProfile</c> straight to the port (LaptopService.Profiles.cs:46).
///
/// Everything else is the canonical five-profile device, and <see cref="Current"/> reports nothing so a write
/// is never short-circuited as "already in that profile" before it can throw.
/// </summary>
public sealed class FakeThrowingPowerProfiles : IPowerProfiles
{
    public string? LastError { get; set; }

    public IReadOnlyList<PerformanceProfile> All { get; } = TestProfiles.All;

    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    /// <summary>Always null: the hardware reports nothing, so every profile a test picks looks like a change.</summary>
    public PerformanceProfile? Current() => null;

    /// <summary>Every profile handed to <see cref="Set"/>, i.e. the writes that threw.</summary>
    public List<PerformanceProfile> SetCalls { get; } = [];

    public bool Set(PerformanceProfile profile)
    {
        SetCalls.Add(profile);
        throw new InvalidOperationException("FakeThrowingPowerProfiles: the write blew up");
    }
}
