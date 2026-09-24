using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 4b: the battery is ONE domain object whose shape declares, per property, what this machine actually
/// has (Domain/Battery.cs). This file is the wave's acceptance criterion, and why it was rewritten is part of
/// what it records.
///
/// WHAT THE PLAN USED TO ASK FOR, AND WHY IT WAS WITHDRAWN: "a Dell fake and an Acer fake give the same object
/// shape with a different property set". No test in this tree constructs a vendor device — AcerDevice,
/// DellDevice, GenericDevice and DeviceFactory are built by nothing here, and every one of the old ports was
/// created inside a vendor's InitVendor from a live probe (a WMI capability bitmask, a sysfs node that exists
/// and is writable, a BIOS attribute that answers). A test could therefore only have built a fake's shape by
/// hand and asserted it back to itself, while reading as proof about the backends. The wave's real question —
/// "does THIS machine have a limiter?" — is answered by a probe against firmware, and this suite has no
/// firmware. The criterion was withdrawn as unprovable here, not as imprecise.
///
/// WHAT REPLACES IT, in two halves that ARE provable here:
/// <list type="number">
/// <item><see cref="BatteryShapeTests"/> — the SHAPE claim, stated as a fake-level claim: the object can
/// present any subset of properties, and an absent property is absent rather than switched off (including one
/// removed after being attached, which is what Dell/Linux does to the generic limiter).</item>
/// <item><see cref="BatteryRowPresenceTests"/> and <see cref="BatteryTelemetryTests"/> — the BEHAVIOUR claim,
/// taken through the SERVICE: for every present/absent combination the three battery rows appear exactly when
/// the property exists, the readings are shown exactly when telemetry exists, and a write behaves as it did
/// through the old ports (the same value reaching the hardware, and the same message naming the same row).</item>
/// </list>
///
/// THE GAP, NAMED. WHICH properties the real backends attach is covered by nothing here: those attach sites
/// are probes against live hardware, and Dell/Linux's removal of the generic limiter sits in a file the test
/// TFM does not even compile (`*.Linux.cs` files are removed from the Windows build). The condition it sits
/// under — inside <c>if (modes.Count &gt; 0)</c>, so a Dell with no parseable <c>charge_types</c> keeps the
/// generic limiter — is verified by reading the file, not by running it.
/// </summary>
public class BatteryShapeTests
{
    /// <summary>The degenerate object: nothing attached. This is a machine with no battery — and it is also
    /// the state every vendor composes FROM, which is why it must be a legal, fully readable object rather
    /// than something callers have to null-check first.</summary>
    [Fact]
    public void ABatteryWithNothingAttached_HasNothing()
    {
        var battery = new Battery();

        Assert.Null(battery.Telemetry);
        Assert.Null(battery.ChargeLimit);
        Assert.Null(battery.Calibration);
        Assert.Null(battery.ChargeMode);
    }

    /// <summary>Each property is attached on its own, and attaching one leaves the others absent. That is the
    /// shape this wave exists for: "this battery has a charge mode and no limiter" is expressible, which is
    /// what Dell/Windows (a BIOS attribute, no battery WMI) and the generic Linux path (a sysfs limiter, no
    /// charge mode) each need.</summary>
    [Fact]
    public void EachPropertyIsAttachedOnItsOwn()
    {
        var battery = new Battery();
        battery.ChargeMode = new FakeChoicePort("adaptive", "express").AsBatteryChoice();

        Assert.NotNull(battery.ChargeMode);
        Assert.Null(battery.ChargeLimit);
        Assert.Null(battery.Calibration);
        Assert.Null(battery.Telemetry);

        battery.ChargeLimit = new FakeFlagPort().AsBatteryToggle();

        Assert.NotNull(battery.ChargeLimit);
        Assert.Null(battery.Calibration);          // still: attaching a second does not conjure a third
    }

    /// <summary>A property can be REMOVED after it was attached, and that is the fact Dell/Linux states when it
    /// finds the firmware's own charge-mode set: the limiter is absent on that machine, not switched off. The
    /// removal is the same assignment as the attach, so nothing has to be told which one it is.</summary>
    [Fact]
    public void AnAttachedPropertyCanBeRemoved()
    {
        var battery = new Battery();
        battery.ChargeLimit = new FakeFlagPort().AsBatteryToggle();
        Assert.NotNull(battery.ChargeLimit);

        battery.ChargeLimit = null;

        Assert.Null(battery.ChargeLimit);
    }

    /// <summary>Telemetry is the fourth member of the same set: optional, and absent on a machine whose OS
    /// reports no battery. The object survives it — a read still answers, with the "unknown" snapshot every
    /// field of which is -1, which is the same answer the service produced while telemetry was a nullable port
    /// (`device.BatteryInfo?.Read() ?? new BatteryInfoSnapshot()`).</summary>
    [Fact]
    public void TelemetryIsOptional_LikeEveryOtherProperty()
    {
        var battery = new Battery();

        var unread = battery.Read();
        Assert.Equal(-1, unread.Percent);
        Assert.Equal(BatteryState.Unknown, unread.State);
        Assert.Equal(-1, unread.HealthPercent);
        Assert.Equal(-1, unread.CycleCount);
        Assert.Null(unread.PowerWatts);   // no battery -> no rate either; null, not -1 (a rate is signed)

        var telemetry = new FakeBatteryTelemetry
        {
            Snapshot = new BatteryInfoSnapshot { Percent = 63, State = BatteryState.Discharging, CycleCount = 12 },
        };
        battery.Telemetry = telemetry.Read;

        var read = battery.Read();
        Assert.Equal(63, read.Percent);
        Assert.Equal(BatteryState.Discharging, read.State);
        Assert.Equal(12, read.CycleCount);
        Assert.Equal(1, telemetry.ReadCount);      // the object reads through the op; it does not cache
    }
}

/// <summary>
/// The SERVICE half of the criterion: which of the three battery rows exist, over every present/absent
/// combination. The rows are built by <c>OptionsAssembler</c> over <c>LaptopService</c> and read
/// <c>Device.Battery</c>, so this is the path the app takes — the port slots the presence tests used to
/// assign are gone, and a row that appears without its property is a row promising a control that cannot work.
/// </summary>
public class BatteryRowPresenceTests
{
    /// <summary>All eight combinations, because the three properties are independent: a machine with a
    /// limiter and no calibration must not gain a calibration row, and one with nothing must gain none.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TheThreeRowsAppearExactlyForThePropertiesTheBatteryHas(bool limit, bool calibration, bool mode)
    {
        var h = new OptionsAssemblerHarness();
        if (limit) h.F.Device.Battery.ChargeLimit = new FakeFlagPort().AsBatteryToggle();
        if (calibration) h.F.Device.Battery.Calibration = new FakeFlagPort().AsBatteryToggle();
        if (mode) h.F.Device.Battery.ChargeMode = new FakeChoicePort("adaptive", "express").AsBatteryChoice();

        var labels = AssemblerRows.Labels(h).ToList();

        Assert.Equal(limit, labels.Contains(Loc.T("Charge limit (~80%)")));
        Assert.Equal(calibration, labels.Contains(Loc.T("Calibration (full cycle)")));
        Assert.Equal(mode, labels.Contains(Loc.T("Charge mode")));
    }

    /// <summary>The write path is the one it was: the value reaches the property, and a refused write reports
    /// through the row. THE NAME CHANGED, deliberately, in the same commit that gave the settings channel its
    /// label: the message is composed from the row the user is looking at, so "Charge limit (~80%)" fails under
    /// its own label instead of under the internal "Battery limit" the interface never showed
    /// (docs/open-decisions.md, «Известные особенности» 5 — the mismatch this test used to pin as shipped).
    ///
    /// The reason in the message is what the property's write hands back, not a <c>LastError</c> read
    /// afterwards — the shape change means the text now provably belongs to THIS write
    /// (docs/open-decisions.md §2). It reads the same, which is the point.</summary>
    [Fact]
    public void ARefusedWrite_StillReportsTheSameName_WithTheReasonOfThisWrite()
    {
        var h = new OptionsAssemblerHarness();
        var limit = new FakeFlagPort { SetResult = false, LastError = "EC refused the write" };
        h.F.Device.Battery.ChargeLimit = limit.AsBatteryToggle();

        AssemblerRows.Toggle(h, "Charge limit (~80%)").OnChange(true);

        Assert.Equal([true], limit.SetCalls);        // the write was attempted, and reached the property
        Assert.Equal("Charge limit (~80%) failed: EC refused the write", Assert.Single(h.RunPosted()));
    }

    /// <summary>A write that lands notifies nothing — the failure branch is one <c>if</c> away from firing on
    /// every write, so it is pinned on this shape too rather than assumed from the old one.</summary>
    [Fact]
    public void ASuccessfulWrite_PostsNothing()
    {
        var h = new OptionsAssemblerHarness();
        var mode = new FakeChoicePort("adaptive", "express");
        h.F.Device.Battery.ChargeMode = mode.AsBatteryChoice();

        AssemblerRows.Choice(h, "Charge mode").OnChange(1);

        Assert.Equal(["express"], mode.SetCalls);
        Assert.Empty(h.Posted);
    }
}

/// <summary>
/// The readings in the Battery card. Their presence is asked of the OBJECT now, in a record a test can build
/// (<see cref="BatterySection.HasInfo"/>) — where it used to be an expression inside <c>AppController</c>, a
/// method no test in this suite can reach. That is a coverage gain, not a refactor: it is what lets the
/// "no battery, no readings" rule be reddened by a mutation at all.
/// </summary>
public class BatteryTelemetryTests
{
    /// <summary>A machine whose OS reports no battery shows no readings; one that reports a battery shows
    /// them, and it is the SAME object in both cases — the flag follows the property, not the object's
    /// existence.</summary>
    [Fact]
    public void TheSectionShowsTheReadings_ExactlyWhenTheMachineReportsABattery()
    {
        var battery = new Battery();
        var section = new BatterySection(battery, null, null, null);

        Assert.False(section.HasInfo);

        battery.Telemetry = new FakeBatteryTelemetry().Read;

        Assert.True(section.HasInfo);
    }

    /// <summary>The service reads through the object and falls back to the "unknown" snapshot when there is
    /// no telemetry — exactly the answer `device.BatteryInfo?.Read() ?? new BatteryInfoSnapshot()` gave, so a
    /// machine with no battery keeps showing the placeholder it always showed. The read count separates "read
    /// the hardware" from "answered from the fallback".</summary>
    [Fact]
    public void ReadBatteryInfo_ReadsThroughTheObject_AndFallsBackToUnknown()
    {
        var f = new LaptopServiceFixture();

        var absent = f.Service.ReadBatteryInfo();
        Assert.Equal(-1, absent.Percent);
        Assert.Equal(BatteryState.Unknown, absent.State);

        var telemetry = new FakeBatteryTelemetry
        {
            Snapshot = new BatteryInfoSnapshot { Percent = 42, State = BatteryState.Charging, HealthPercent = 88 },
        };
        f.Device.Battery.Telemetry = telemetry.Read;

        var read = f.Service.ReadBatteryInfo();
        Assert.Equal(42, read.Percent);
        Assert.Equal(BatteryState.Charging, read.State);
        Assert.Equal(88, read.HealthPercent);
        Assert.Equal(1, telemetry.ReadCount);
    }
}

/// <summary>
/// The power row the Battery card shows: the live rate, with the label chosen by the reading's SIGN. The two
/// directions are the two facts the user asked for — what the machine DRAWS while on battery, and how fast the
/// battery CHARGES while on AC — and both are the same neutral value (<c>BatteryInfoSnapshot.PowerWatts</c>,
/// Domain/Models.cs). The split is therefore presentation and is decided here, not in the domain, which only
/// states that the battery's power is signed. The row's visibility is part of the claim: a battery that reports
/// no rate shows no row, because a zero would be a made-up measurement.
/// </summary>
public class BatteryPowerTests
{
    private static BatteryViewModel Section() => new(hasInfo: true, limit: null, calibration: null, chargeMode: null);

    /// <summary>On battery the value is negative in the domain, and the row names what the machine is drawing
    /// rather than showing the sign.</summary>
    [Fact]
    public void OnBattery_ShowsTheDraw()
    {
        var vm = Section();

        vm.Update(new BatteryInfoSnapshot { State = BatteryState.Discharging, PowerWatts = -12.3 });

        Assert.True(vm.ShowPower);
        Assert.Equal(Loc.T("Power draw"), vm.PowerLabel);
        Assert.Equal(Loc.T("{0:0.0} W", 12.3), vm.Power);
    }

    /// <summary>On AC the value is positive, and the same row names the charge rate.</summary>
    [Fact]
    public void OnAc_ShowsTheChargePower()
    {
        var vm = Section();

        vm.Update(new BatteryInfoSnapshot { State = BatteryState.Charging, PowerWatts = 45.0 });

        Assert.True(vm.ShowPower);
        Assert.Equal(Loc.T("Charging power"), vm.PowerLabel);
        Assert.Equal(Loc.T("{0:0.0} W", 45.0), vm.Power);
    }

    /// <summary>No rate (null) hides the row rather than showing 0 W, and a later reading puts it back — the
    /// flag follows the value, not the snapshot's existence.</summary>
    [Fact]
    public void NoReportedRate_HidesTheRow()
    {
        var vm = Section();

        vm.Update(new BatteryInfoSnapshot { State = BatteryState.Discharging, PowerWatts = -10.0 });
        Assert.True(vm.ShowPower);

        vm.Update(new BatteryInfoSnapshot { State = BatteryState.Unknown });
        Assert.False(vm.ShowPower);
        Assert.Equal("", vm.Power);

        vm.Update(new BatteryInfoSnapshot { State = BatteryState.Charging, PowerWatts = 30.0 });
        Assert.True(vm.ShowPower);
    }

    /// <summary>A COARSE READING CHURNS NOTHING. The fast path ticks every second, but the OS fuel gauge behind
    /// it is coarse — the same percent/state/rate come back for ten to twenty seconds at a time (measured on the
    /// AN18-61: 17 one-second ticks in 18 s, Rate constant at -40.3 W throughout). Re-applying an unchanged
    /// snapshot must therefore raise no notification at all; otherwise the one-second poll would repaint a value
    /// that did not change and the "faster refresh" would be pure churn. This is what the generated
    /// <c>[ObservableProperty]</c> setters give us, pinned here because the fast cadence depends on it.
    /// MUTATION THAT REDDENS IT: replace a generated setter with an unconditional <c>OnPropertyChanged</c>.</summary>
    [Fact]
    public void AnUnchangedReading_RaisesNoNotification()
    {
        var vm = Section();
        var snapshot = new BatteryInfoSnapshot { Percent = 53, State = BatteryState.Discharging, PowerWatts = -40.3 };
        vm.Update(snapshot);   // the first read lands...

        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;
        vm.Update(snapshot);   // ...and the next second's read of the same coarse value must be silent

        Assert.Equal(0, raised);
    }
}
