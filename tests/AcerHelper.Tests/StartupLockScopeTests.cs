using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 5 — how far <c>_state</c> reaches during <see cref="LaptopService.ApplyStartupState"/>.
///
/// The method used to hold the lock across four hardware calls (two powrprof, one gdi32, one NvAPI) — design
/// doc D17, and its Step 5 names this method. It now reads every value it needs inside the lock and makes the
/// calls outside it. Asserted here is the part a fake can see: the two calls that CHANGED hands (clamshell,
/// display tint) happen with the lock released, and the lock is still taken by the ones that kept it.
///
/// The two mode calls (<c>ApplyModeGpuOc</c>, <c>ApplyModeCpuPower</c>) deliberately still write inside a lock —
/// their OWN, one call deep, because each re-reads the current mode under it. <c>held == true</c> for those is
/// the intended state, not a miss: hoisting their argument read out of the lock would let a concurrent writer
/// land first and be overwritten by the now-stale execution. That remaining site is recorded in the plan.
///
/// <see cref="LaptopService.StateHeld"/> is the seam. It is needed because a fake observes the CALL, never the
/// lock state around it — without the seam, "this call happens outside the lock" is not assertable at all, and
/// the change this file exists to pin would be untestable.
/// </summary>
public class StartupLockScopeTests
{
    /// <summary>The two calls the method re-homed, and the lock was released for each. <c>Assert.All</c> rather
    /// than two asserts so a failure names the offending call rather than just its position.</summary>
    [Fact]
    public void TheCallsThatChangedHands_HappenWithTheLockReleased()
    {
        var f = new LaptopServiceFixture(new Settings { Clamshell = true, Bluelight = 3 });
        var probe = new LockObservingTintAndClamshell(f.Service);
        f.Device.Clamshell = probe;
        f.Device.DisplayTint = probe;

        f.Service.ApplyStartupState();

        // Both were reached. Without this, a probe that is simply never called would satisfy "held == false"
        // vacuously — the false green this file exists to avoid.
        Assert.Equal(["clamshell.SetEnabled", "tint.Apply"], probe.Calls.Select(c => c.Call));
        Assert.All(probe.Calls, c => Assert.False(c.Held));
    }

    /// <summary>Nothing configured, nothing touched. Cheap, but it is what keeps the test above from passing
    /// against a method that calls every port unconditionally.</summary>
    [Fact]
    public void WithNothingEnabled_ThePortsAreNotCalled()
    {
        var f = new LaptopServiceFixture(new Settings { Clamshell = false, Bluelight = 0 });
        var probe = new LockObservingTintAndClamshell(f.Service);
        f.Device.Clamshell = probe;
        f.Device.DisplayTint = probe;

        f.Service.ApplyStartupState();

        Assert.Empty(probe.Calls);
    }

    /// <summary>The seam has to answer honestly, or every assertion above passes by reporting <c>false</c>
    /// always — including from inside a lock, which is exactly the condition they are supposed to detect.
    ///
    /// This drives a call from a thread that DOES hold the lock by taking it through a member that holds it
    /// across the port call: <c>ApplyModeCpuPower</c> is that shape today, so it is the honest witness rather
    /// than a synthetic one. Both halves are required — <c>true</c> from inside proves the seam can see a held
    /// lock, and <c>false</c> from outside proves it is not simply always true.</summary>
    [Fact]
    public void TheSeamReportsTheLockHonestly_AndAModeCallStillTakesIt()
    {
        var f = new LaptopServiceFixture(new Settings { CpuPowerModes = { ["balanced"] = "best-efficiency" } });
        f.Power(TestProfiles.All, current: TestProfiles.Balanced);   // -> the current mode key is "balanced"
        var seen = new List<bool>();
        f.Device.CpuPower = new LockObservingCpuPower(f.Service, new FakeCpuPower(), seen);

        Assert.False(f.Service.StateHeld);        // this thread holds nothing

        f.Service.ApplyModeCpuPower();

        Assert.Equal([true], seen);               // reached the port from inside the mode call's own lock
        Assert.False(f.Service.StateHeld);        // ...and released again by the time we look
    }
}

/// <summary>
/// An <see cref="IClamshell"/> and an <see cref="IDisplayTint"/> at once — the only two ports
/// <c>ApplyStartupState</c> re-homed, and both are two-member interfaces, so one wrapper covers them; the test
/// says which row it means by the recorded call name. It records <see cref="LaptopService.StateHeld"/> at the
/// moment of the call, which is the one thing a plain counting fake cannot capture.
/// </summary>
internal sealed class LockObservingTintAndClamshell(LaptopService service) : IClamshell, IDisplayTint
{
    public List<(string Call, bool Held)> Calls { get; } = [];

    // ---- IClamshell ----
    public string Label => "probe";
    public bool Enabled { get; private set; }
    public void SetEnabled(bool value) { Calls.Add(("clamshell.SetEnabled", service.StateHeld)); Enabled = value; }
    public void Evaluate() { }
    public void Dispose() { }

    // ---- IDisplayTint ----
    public int Levels => 5;
    public bool Apply(int level) { Calls.Add(("tint.Apply", service.StateHeld)); return true; }
}

/// <summary>An <see cref="ICpuPower"/> that records whether the lock was held when the write reached it, then
/// delegates to the real fake so the call still has its normal effect (and its own assertions stay available).</summary>
internal sealed class LockObservingCpuPower(LaptopService service, ICpuPower inner, List<bool> seen) : ICpuPower
{
    public string? LastError => inner.LastError;
    public IReadOnlyList<ChoiceOption> Modes => inner.Modes;
    public string? Current() => inner.Current();

    public bool Set(string id)
    {
        seen.Add(service.StateHeld);
        return inner.Set(id);
    }
}
