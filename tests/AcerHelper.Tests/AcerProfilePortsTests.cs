using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The two Acer profile-port decorators — the pair that sits between <c>LaptopService</c> and whichever profile
/// port the machine actually has.
///
/// They are covered here rather than left to the hardware because they were moved out of
/// <c>AcerDevice.Linux.cs</c> for exactly this reason: the test project targets <c>net10.0-windows</c> while the
/// app excludes <c>**/*.Linux.cs</c> from that TFM, so policy left in the Linux file is unreachable by the suite
/// at all. What stayed behind there is the part that needs a Linux box — the <c>/sys/class/power_supply</c> walk
/// and the hidraw controller — and both now arrive here as delegates.
///
/// <see cref="EcSyncedProfiles"/> is the one that matters most: it is what makes a profile switch move the power
/// envelope on an EC-HID model. Before it wrapped the generic port too, that wiring sat behind the Linuwu-Sense
/// module gate, so a machine with mainline <c>acer-wmi</c> and no module opened the EC controller and then never
/// consulted it — the envelope stayed put through every profile change.
/// </summary>
public class AcerProfilePortsTests
{
    // ---- EcSyncedProfiles: the envelope follows the profile switch ----

    /// <summary>The whole point of the decorator, in one assertion: the profile's <see cref="ProfileKind"/> — not
    /// its id, not its index — reaches the envelope operation, and the switch itself still reaches the inner
    /// port. The kind is what <c>AcerEcHidController.ModeFor</c> maps, so passing the wrong member here silently
    /// picks a different power limit.</summary>
    [Fact]
    public void TheEnvelopeIsDrivenWithTheProfileKind()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        var kinds = new List<ProfileKind>();
        var port = new EcSyncedProfiles(inner, k => { kinds.Add(k); return true; });

        Assert.True(port.Set(TestProfiles.Turbo));

        Assert.Equal([ProfileKind.Turbo], kinds);
        Assert.Equal(["turbo"], inner.SetCallIds);
    }

    /// <summary>Every kind, not just the one above — the delegate must not be skipped or short-circuited for any
    /// of them. A decorator that only forwarded, say, Turbo would pass the test above and quietly do nothing for
    /// the four profiles a user switches between in practice.</summary>
    [Fact]
    public void EveryProfileKindReachesTheEnvelope()
    {
        var kinds = new List<ProfileKind>();
        var port = new EcSyncedProfiles(new FakePowerProfiles(TestProfiles.All), k => { kinds.Add(k); return true; });

        foreach (var p in TestProfiles.All) port.Set(p);

        Assert.Equal(TestProfiles.All.Select(p => p.Kind), kinds);
    }

    /// <summary>A failing inner write is forwarded as-is. The decorator must not report the envelope's fate
    /// instead of the profile's: the caller uses this bool to decide whether to show an error, and the envelope
    /// write is enqueue-only, so it can never be the authority on whether the profile switch worked.</summary>
    [Fact]
    public void TheInnerResultIsForwardedUnchanged_NotTheEnvelopes()
    {
        var inner = new FakePowerProfiles(TestProfiles.All) { SetResult = false };
        var port = new EcSyncedProfiles(inner, _ => true);   // envelope says yes, inner says no

        Assert.False(port.Set(TestProfiles.Eco));
        Assert.Equal(["eco"], inner.SetCallIds);             // and the inner port was still reached
    }

    /// <summary>Windows' semantics, kept on Linux: a controller that cannot take the mode must not fail the
    /// profile switch (see <c>AcerDevice.Windows.SetProfile</c> — the EC write is enqueued, and its result is
    /// discarded). Asserted because the tempting "fix" is to fail the switch when the envelope write fails, which
    /// would make every profile click on a model with an absent or wedged EC report an error.</summary>
    [Fact]
    public void ADecliningEnvelopeDoesNotFailTheSwitch()
    {
        var port = new EcSyncedProfiles(new FakePowerProfiles(TestProfiles.All), _ => false);

        Assert.True(port.Set(TestProfiles.Balanced));
    }

    /// <summary>No EC channel on this model: the port has to behave exactly as it would unwrapped. This is the
    /// case that catches an unconditional call — writing <c>applyEnvelope!(…)</c> instead of
    /// <c>applyEnvelope?.(…)</c> turns every profile switch on a model without the EC device into a
    /// <c>NullReferenceException</c>, which on a machine the owner does not have is invisible until a user
    /// reports it.</summary>
    [Fact]
    public void WithNoEcChannel_TheInnerPortBehavesAsIfUnwrapped()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        var port = new EcSyncedProfiles(inner, null);

        Assert.True(port.Set(TestProfiles.Quiet));
        Assert.Equal(["quiet"], inner.SetCallIds);
    }

    /// <summary>The read side is pass-through, including <c>LastError</c>. The UI builds its profile list and its
    /// error text from these, so a decorator that swallowed one would hide a real failure.</summary>
    [Fact]
    public void TheReadSideIsForwardedUnchanged()
    {
        var inner = new FakePowerProfiles(TestProfiles.All,
                                          selectable: [TestProfiles.Eco],
                                          current: TestProfiles.Eco) { LastError = "boom" };
        var port = new EcSyncedProfiles(inner, _ => true);

        Assert.Equal(TestProfiles.All.Select(p => p.Id), port.All.Select(p => p.Id));
        Assert.Equal(["eco"], port.Selectable().Select(p => p.Id));
        Assert.Equal("eco", port.Current()!.Id);
        Assert.Equal("boom", port.LastError);
    }

    // ---- BatteryGatedProfiles: what the EC refuses on battery is greyed out ----

    /// <summary>On battery the EC rejects everything but balanced/low-power (EOPNOTSUPP from the driver), so the
    /// selectable list drops the rest. Only <c>balanced</c> survives here because <see cref="TestProfiles"/> — the
    /// set the real Acer port uses — has no <c>low-power</c> entry; the filter is by id, so that list is what
    /// decides.</summary>
    [Fact]
    public void OnBattery_TheSelectableListKeepsOnlyTheBatterySafeIds()
    {
        var port = new BatteryGatedProfiles(new FakePowerProfiles(TestProfiles.All), () => false);

        Assert.Equal(["balanced"], port.Selectable().Select(p => p.Id));
    }

    /// <summary>Unplugged is not the normal case: on AC nothing is filtered.</summary>
    [Fact]
    public void OnAc_TheSelectableListIsUnfiltered()
    {
        var port = new BatteryGatedProfiles(new FakePowerProfiles(TestProfiles.All), () => true);

        Assert.Equal(["quiet", "eco", "balanced", "performance", "turbo"], port.Selectable().Select(p => p.Id));
    }

    /// <summary>The filter is limited to <c>Selectable</c>, and that matters in one specific situation: the
    /// machine can be sitting on Turbo while unplugged. <c>All</c> still has to describe every profile the
    /// hardware exposes (the UI lists them) and <c>Current</c> still has to report Turbo, or the app would show
    /// the wrong active mode — the state the user needs to see most.</summary>
    [Fact]
    public void OnlySelectableIsFiltered_AllAndCurrentStayWhole()
    {
        var inner = new FakePowerProfiles(TestProfiles.All, current: TestProfiles.Turbo);
        var port = new BatteryGatedProfiles(inner, () => false);

        Assert.Equal(TestProfiles.All.Select(p => p.Id), port.All.Select(p => p.Id));
        Assert.Equal("turbo", port.Current()!.Id);
        Assert.Equal(["balanced"], port.Selectable().Select(p => p.Id));
    }

    /// <summary>The gate is on the list, not on the write — this decorator shapes what the UI offers, it is not
    /// an enforcement point. Pinned deliberately: the tempting change is to reject writes here, which would move
    /// the refusal away from the hardware that actually decides it and turn a greyed-out entry into a failed
    /// click for the paths that don't consult <c>Selectable</c> first.</summary>
    [Fact]
    public void SetIsNotGated_OnlyTheSelectableListIs()
    {
        var inner = new FakePowerProfiles(TestProfiles.All);
        var port = new BatteryGatedProfiles(inner, () => false);

        Assert.True(port.Set(TestProfiles.Turbo));
        Assert.Equal(["turbo"], inner.SetCallIds);
    }

    /// <summary>The AC probe is asked again on every call rather than cached at construction — the machine can be
    /// plugged and unplugged while the app runs, and a cached answer would leave the profile list frozen in
    /// whichever state it started in.</summary>
    [Fact]
    public void TheAcProbeIsConsultedOnEveryCall()
    {
        var onAc = true;
        var port = new BatteryGatedProfiles(new FakePowerProfiles(TestProfiles.All), () => onAc);

        Assert.Equal(5, port.Selectable().Count);

        onAc = false;

        Assert.Equal(["balanced"], port.Selectable().Select(p => p.Id));
    }
}
