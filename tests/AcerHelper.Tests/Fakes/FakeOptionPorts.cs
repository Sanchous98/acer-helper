using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written transports for <c>OptionsAssembler</c>: each is the real one's two-way shape (a write that can
/// fail, a read the test controls) plus the failure modes a fake needs to let a test CHOOSE — a refused write
/// (<see cref="FakeFlagPort.SetResult"/>/<see cref="FakeChoicePort.SetResult"/>), a write that THROWS, and
/// (for the flag port) a READ that throws, which the ports survive by different paths (see
/// <see cref="FakeThrowingPowerProfiles"/>).
///
/// Each fake is one of the two TRANSPORT shapes (Domain/Ports.cs: <see cref="IFlagPort"/>, and
/// <see cref="IChoicePort"/>) and is DECLARED as a setting through <see cref="FakeDevice.Declare(string,
/// IFlagPort, bool)"/>, which pairs it with the backend key a real one comes with — so a test says which row it
/// means by that key (or by the row's label) rather than by a port type that no longer exists.
///
/// The BATTERY is the exception: its properties are ops on a domain object rather than ports
/// (Domain/Battery.cs), so those two fakes are handed over as an op pair instead of being declared —
/// see <see cref="FakeFlagPort.AsBatteryToggle"/> and <see cref="FakeChoicePort.AsBatteryChoice"/>, which hand
/// over the SAME state, read count and refused-write behaviour the port interface exposes.
///
/// Reads are COUNTED: the Options rows hand the user a <c>Read</c> delegate the view-model calls after every
/// write, so "did the row ask the hardware again?" is an assertion, not an implementation detail.
/// </summary>
public sealed class FakeFlagPort : IFlagPort
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

    /// <summary>When true, <see cref="Get"/> counts the attempt and throws instead of answering — the EC
    /// refusing the read rather than reporting a value. Distinct from <see cref="State"/>=false, which is a
    /// real answer ("off") that a row must SHOW; a throw must leave the row on its placeholder, because the
    /// port never said anything.</summary>
    public bool ThrowOnGet { get; set; }

    public string? LastError { get; set; }

    /// <summary>Number of <see cref="Get"/> calls. The row reads once when it is built (its
    /// <c>Initial</c>), so a readback test asserts on the count AFTER that.</summary>
    public int GetCount { get; private set; }

    /// <summary>Every value handed to <see cref="Set"/>, in order, including refused ones.</summary>
    public List<bool> SetCalls { get; } = [];

    public bool Get()
    {
        GetCount++;
        if (ThrowOnGet) throw new InvalidOperationException("FakeFlagPort: the read blew up");
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

    /// <summary>This fake as an on/off property of the battery object. The state, the read count and the
    /// refused/throwing write are the same ones the port interface exposes — the property is only a different
    /// way of being handed over. The reason is reported the way the port shape reported it: absent when the
    /// write took, <see cref="LastError"/> when it did not (the service's <c>Attempt</c> does exactly that).</summary>
    public BatteryToggle AsBatteryToggle()
        => new(Get, on => { var ok = Set(on); return (ok, ok ? null : LastError); });
}

/// <summary>
/// Hand-written <see cref="IChoicePort"/> for the dropdown rows. Every id doubles as its display name, because
/// no Options assertion depends on the option LABELS — only on which id the row maps to which dropdown INDEX
/// (<c>ChoiceSetting.IndexOf</c>), which is the value the view-model puts back into the combo box.
/// </summary>
public sealed class FakeChoicePort : IChoicePort
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

    /// <summary>When true, <see cref="Set"/> throws instead of returning — the same failure path
    /// <see cref="FakeFlagPort.ThrowOnSet"/> models, on the choice shape (a declared choice declares its own
    /// refusal through <c>ChoiceSetting.Write</c>, so both halves of that class need a port that can blow up).</summary>
    public bool ThrowOnSet { get; set; }

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
        if (ThrowOnSet) throw new InvalidOperationException("FakeChoicePort: the write blew up");
        if (!SetResult) return false;
        CurrentId = id;
        return true;
    }

    /// <summary>This fake as the battery's charge-mode property (see
    /// <see cref="FakeFlagPort.AsBatteryToggle"/> for why a property is handed over as ops).</summary>
    public BatteryChoice AsBatteryChoice()
        => new(Options, Get, id => { var ok = Set(id); return (ok, ok ? null : LastError); });
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

    /// <summary>How long this port takes to answer, for the one property a fake is otherwise unable to stand in
    /// for: that a level change is SLOW — the real KWin link waits out the compositor's 2000 ms quick-adjust walk
    /// before it can verify a change made while the filter is already on (KwinTint.Linux.cs, <c>Commit</c>), and
    /// the whole reason the apply left the UI thread is that wait. Zero (the default) keeps every other test
    /// synchronous.</summary>
    public TimeSpan ApplyDelay { get; set; }

    public bool Apply(int level)
    {
        if (ApplyDelay > TimeSpan.Zero) Thread.Sleep(ApplyDelay);
        ApplyCalls.Add(level);
        return ApplyResult;
    }
}

/// <summary>
/// Hand-written <see cref="IPowerProfiles"/> whose <see cref="Set"/> ALWAYS THROWS. <see cref="FakePowerProfiles"/>
/// can refuse a write but cannot blow up, and the profile rows are the ones whose setter is not a bare
/// <c>SetFlag</c>/<c>SetChoice</c> call — so this is the fake that proves a throwing write on that shape is
/// still reported instead of escaping the row.
///
/// WHAT IT DOES NOT DO, corrected by measurement: it does not reach <c>OptionsAssembler.RunSet</c>'s own
/// <c>catch</c>. <c>Attempt</c> absorbs the throw one layer below (Infrastructure/Composition/LaptopService.cs),
/// reached
/// through <c>SetSourceProfile</c> -> <c>ApplyStoredMode</c> -> <c>Attempt(() =&gt; pp.Set(…))</c>, and the same is
/// true of the flag and choice ports. The reason is visible in the message rather than in the trace:
/// <c>RunSet</c>'s catch discards the reason, and a message that carries one therefore did not come from it.
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
