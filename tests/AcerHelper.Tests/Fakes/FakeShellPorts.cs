using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// Hand-written ports for the app-level rows <c>OptionsViewModel</c> adds on top of the assembler's: autostart,
/// clamshell, and the plain (non-RGB) keyboard-backlight brightness. Same idea as <see cref="FakeFlagPort"/> —
/// the real port's two-way shape plus a counter, so "did building this row ask the hardware?" is an assertion
/// rather than an implementation detail.
///
/// The three differ in exactly the way wave 6 cares about, and the counters are here to keep that difference
/// visible: only <see cref="FakeAutostart.IsEnabled"/> is a real device call (it shells out to schtasks.exe and
/// waits up to 5 s, Autostart.Windows.cs:181), so it is the one whose read is DEFERRED. The other two answer
/// from a field the port already holds, and deferring a field read would only make the row spend longer showing
/// a value it had all along.
/// </summary>
public sealed class FakeAutostart : IAutostart
{
    public string Label { get; set; } = "Start with Windows";

    /// <summary>What <see cref="IsEnabled"/> reports.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many times <see cref="IsEnabled"/> was called. Zero means the row deferred the read —
    /// which is the whole point of the row's <c>Prime</c>.</summary>
    public int IsEnabledCalls { get; private set; }

    /// <summary>Every value handed to <see cref="SetEnabled"/>, in order.</summary>
    public List<bool> SetCalls { get; } = [];

    /// <summary>How many times <see cref="EnsureCurrent"/> was called. Startup heals a stale task definition
    /// through it (<c>AppController</c>, next to <c>ApplyStartupState</c>), so it must NOT be reachable from
    /// the row: the row's toggle writes, it does not re-register.</summary>
    public int EnsureCurrentCalls { get; private set; }

    public bool IsEnabled()
    {
        IsEnabledCalls++;
        return Enabled;
    }

    public bool SetEnabled(bool enable)
    {
        SetCalls.Add(enable);
        Enabled = enable;
        return true;
    }

    public void EnsureCurrent() => EnsureCurrentCalls++;
}

/// <summary>Hand-written <see cref="IClamshell"/>. <see cref="Enabled"/> counts its READS as well as its
/// writes, because that property is the real port's shape too: the port keeps the answer in a field
/// (Clamshell.cs:24), so the row builds from it inline and needs no prime — the counter is what lets a test
/// say so instead of assuming it.</summary>
public sealed class FakeClamshell : IClamshell
{
    private bool _enabled;

    public string Label { get; set; } = "Stay awake when lid closed (docked, on AC)";

    public bool Enabled
    {
        get { EnabledReads++; return _enabled; }
        set => _enabled = value;
    }

    /// <summary>How many times <see cref="Enabled"/> was read. One after the row is built; still one after a
    /// prime, because a clamshell row has no read to defer.</summary>
    public int EnabledReads { get; private set; }

    /// <summary>Every value handed to <see cref="SetEnabled"/>, in order.</summary>
    public List<bool> SetCalls { get; } = [];

    public int EvaluateCalls { get; private set; }
    public bool Disposed { get; private set; }

    public void SetEnabled(bool value)
    {
        SetCalls.Add(value);
        _enabled = value;
    }

    public void Evaluate() => EvaluateCalls++;

    public void Dispose() => Disposed = true;
}

/// <summary>Hand-written <see cref="IKeyboardBrightness"/>: discrete levels, a counted read and a counted
/// write, for the lighting window's plain-backlight row.</summary>
public sealed class FakeKeyboardBrightness(int maxLevel = 2) : IKeyboardBrightness
{
    public string? LastError { get; set; }

    public int MaxLevel { get; set; } = maxLevel;

    /// <summary>What <see cref="Get"/> reports. 0 is also "off".</summary>
    public int Level { get; set; }

    /// <summary>How many times <see cref="Get"/> was called.</summary>
    public int GetCount { get; private set; }

    /// <summary>Every level handed to <see cref="Set"/>, in order.</summary>
    public List<int> SetCalls { get; } = [];

    /// <summary>When false, <see cref="Set"/> records the call, leaves <see cref="Level"/> unchanged and
    /// returns false — the EC-refused case.</summary>
    public bool SetResult { get; set; } = true;

    public int Get()
    {
        GetCount++;
        return Level;
    }

    public bool Set(int level)
    {
        SetCalls.Add(level);
        if (!SetResult) return false;
        Level = level;
        return true;
    }
}
