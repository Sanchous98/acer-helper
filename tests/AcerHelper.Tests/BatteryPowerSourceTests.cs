using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using AcerHelper.UI.ViewModels;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The power-source row (barrel vs USB-C PD vs battery): the Acer EC HID decode, and the view-model mapping
/// that turns the neutral <see cref="PowerSource"/> enum into a shown row. The read itself is covered where it
/// can be without hardware — the PURE decoder (<c>AcerEcHidController.DecodePowerSource</c>) and the frame bytes
/// (<c>FrameFor</c>), both static and needing none of the transport the constructor opens. The live read on a
/// real charger swap is not testable here: it needs the EC HID device and a cable.
///
/// This is deliberately the same technique <c>AcerEcUsageModeTests</c> uses: constructing
/// <c>AcerEcHidController</c> opens the channel and starts a writer thread, so a test may only reach its static,
/// pure members.
/// </summary>
public class AcerPowerSourceDecodeTests
{
    /// <summary>The request frame is built by the existing <c>FrameFor</c> with the pinned feature/cmd — the ONE
    /// place the encoding is spelled. Feature 0x0000, cmd 0x03, param 0x00, zero-padded to 65. A builder that
    /// moved a byte would move the read's request and the EC would answer something else.
    /// MUTATION THAT REDDENS IT: change the feature or cmd constant, or move a field in <c>FrameFor</c>.</summary>
    [Fact]
    public void ThePowerSourceRequestIsTheMeasuredFrame()
    {
        var send = AcerEcHidController.FrameFor(AcerEcHidController.FeaturePowerStatus,
                                                AcerEcHidController.CmdPowerStatus, param6: 0x00);
        Assert.Equal(65, send.Length);
        Assert.Equal(new byte[] { 0xA0, 0x00, 0xA0, 0x00, 0x00, 0x03, 0x00 }, send[..7]);
        Assert.All(send[7..], b => Assert.Equal(0, b));
    }

    /// <summary>The measured source TYPE byte → the neutral enum. 0x00 = no external power, 0x01 = barrel/DC-in,
    /// 0x04 = USB-C / Power Delivery (docs/power-an18-61.md). A wrong mapping silently shows the wrong charger,
    /// so each point is pinned.
    /// MUTATION THAT REDDENS IT: swap Barrel and UsbC, or map 0x01 to Battery.</summary>
    [Theory]
    [InlineData(0x00, PowerSource.Battery)]
    [InlineData(0x01, PowerSource.Barrel)]
    [InlineData(0x04, PowerSource.UsbC)]
    public void TheMeasuredByteMapsToItsSource(byte b7, PowerSource expected)
    {
        var reply = Accepted();
        reply[7] = b7;

        Assert.Equal(expected, AcerEcHidController.DecodePowerSource(reply));
    }

    /// <summary>The ACK gate: a reply whose byte 2 is not 0xE0 was not accepted by the EC, so byte 7 must not be
    /// trusted — the decoder returns null (the caller hides the row). A stale or garbled frame reads exactly like
    /// a measurement otherwise (see the class header's warning 3).
    /// MUTATION THAT REDDENS IT: drop the <c>reply[2] != 0xE0</c> guard.</summary>
    [Fact]
    public void ANonAcceptedReplyIsNotTrusted()
    {
        var reply = Accepted();
        reply[2] = 0xFF;   // the rejection marker
        reply[7] = 0x01;

        Assert.Null(AcerEcHidController.DecodePowerSource(reply));
    }

    /// <summary>A reply too short to hold byte 7 is not a reading. Guards the index rather than reading past the
    /// array.
    /// MUTATION THAT REDDENS IT: remove the length guard.</summary>
    [Fact]
    public void AShortReplyIsNotTrusted()
    {
        Assert.Null(AcerEcHidController.DecodePowerSource(new byte[7]));   // indices 0..6 only
        Assert.Null(AcerEcHidController.DecodePowerSource([]));
    }

    /// <summary>An unmapped, non-zero code is <see cref="PowerSource.Unknown"/> — "on a source, not one we can
    /// name" — never a guess. Unknown hides the row (see the view-model test), so this is the fail-closed path.
    /// MUTATION THAT REDDENS IT: map <c>_</c> to a concrete source.</summary>
    [Theory]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0xFF)]
    public void AnUnmappedCodeIsUnknownNotAGuess(byte b7)
    {
        var reply = Accepted();
        reply[7] = b7;

        Assert.Equal(PowerSource.Unknown, AcerEcHidController.DecodePowerSource(reply));
    }

    /// <summary>The measured success marker on an otherwise zero frame.</summary>
    private static byte[] Accepted()
    {
        var reply = new byte[65];
        reply[0] = 0xA0;
        reply[2] = 0xE0;
        return reply;
    }
}

/// <summary>
/// <see cref="BatteryViewModel"/>'s power-source row: the enum → label mapping, and the visibility rule ("shown
/// only when the device exposes the property AND the read is not Unknown"). Mirrors how the live power row's
/// label/visibility is pinned.
/// </summary>
public class BatteryPowerSourceRowTests
{
    private static BatteryViewModel Section(bool withSource) =>
        new(hasInfo: true, limit: null, calibration: null, chargeMode: null,
            powerSource: withSource ? () => PowerSource.Unknown : null);

    /// <summary>A machine whose battery exposes no power-source op has no row and says so
    /// (<see cref="BatteryViewModel.HasPowerSource"/> is false), which is what stops the app building the slow
    /// schedule at all.
    /// MUTATION THAT REDDENS IT: make HasPowerSource ignore the op.</summary>
    [Fact]
    public void WithoutTheProperty_ThereIsNoSourceRow()
    {
        var vm = Section(withSource: false);

        Assert.False(vm.HasPowerSource);
        Assert.False(vm.ShowSource);

        vm.SetPowerSource(PowerSource.Barrel);   // even a value cannot conjure the row on a machine without the op
        Assert.False(vm.ShowSource);
    }

    /// <summary>Each named source gets the fixed label and its own translated value, and the row shows. The
    /// values are the neutral localization keys.
    /// MUTATION THAT REDDENS IT: map Barrel to the USB-C string.</summary>
    [Theory]
    [InlineData(PowerSource.Barrel, "bat.source_ac")]
    [InlineData(PowerSource.UsbC, "bat.source_usbc")]
    [InlineData(PowerSource.Battery, "bat.source_battery")]
    public void EachNamedSourceGetsItsLabelAndShows(PowerSource source, string key)
    {
        var vm = Section(withSource: true);

        vm.SetPowerSource(source);

        Assert.True(vm.ShowSource);
        Assert.Equal(Loc.T("bat.power_source"), vm.SourceLabel);
        Assert.Equal(Loc.T(key), vm.Source);
    }

    /// <summary>Unknown hides the row and clears it: a failed read (or the app starting before the first read
    /// lands) must not show a made-up source. A later real reading puts the row back — the flag follows the
    /// value, not the op's existence.
    /// MUTATION THAT REDDENS IT: show the row for Unknown.</summary>
    [Fact]
    public void UnknownHidesTheRow()
    {
        var vm = Section(withSource: true);

        vm.SetPowerSource(PowerSource.Barrel);
        Assert.True(vm.ShowSource);

        vm.SetPowerSource(PowerSource.Unknown);
        Assert.False(vm.ShowSource);
        Assert.Equal("", vm.Source);
        Assert.Equal("", vm.SourceLabel);

        vm.SetPowerSource(PowerSource.UsbC);
        Assert.True(vm.ShowSource);
    }

    /// <summary>A machine with the op set its op from the device (<c>Battery.PowerSource</c>) — the shape
    /// <c>MainViewModel</c> builds it with — so this pins that the flag is about the op, not a caller's bool.
    /// MUTATION THAT REDDENS IT: hard-code HasPowerSource true.</summary>
    [Fact]
    public void HasPowerSourceFollowsTheOp()
    {
        Assert.True(Section(withSource: true).HasPowerSource);
        Assert.False(Section(withSource: false).HasPowerSource);
    }
}

/// <summary>
/// The power-source cadence: SLOWER than the 1 Hz battery poll, and off the full three-second pass, because the
/// read is an EC HID transaction rather than the OS gauge. Pins the relationship the separation exists for — the
/// same shape <c>BatteryPollScheduleTests</c> uses for the fast/slow split.
/// </summary>
public class AcerPowerSourceScheduleTests
{
    /// <summary>The production cadence is five seconds (see the class doc for why).
    /// MUTATION THAT REDDENS IT: change the constant.</summary>
    [Fact]
    public void ThePeriodIsFiveSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(5), PowerSourceSchedule.Period);

    /// <summary>THE WHOLE POINT: the power-source read must NOT run at the battery poll's 1 Hz. It is heavier
    /// (a vendor transport transaction) and its value changes only on a physical plug event, so a slower cadence
    /// is both cheaper and sufficient. This also keeps it off the full pass's three-second grid, so the app does
    /// not re-issue a heavy read at the profile envelope's own rate.
    /// MUTATION THAT REDDENS IT: set the period to the battery poll's one second (or below the full pass's
    /// three).</summary>
    [Fact]
    public void ThePowerSourcePeriodIsSlowerThanTheBatteryPollAndTheFullPass()
    {
        Assert.True(PowerSourceSchedule.Period > BatteryPollSchedule.Period,
            "the power-source read must not ride the 1 Hz OS battery poll");
        Assert.True(PowerSourceSchedule.Period > PollSchedule.Period,
            "the power-source read must not ride the full three-second EC/WMI pass");
    }

    /// <summary>The read REPEATS — a one-shot startup read cannot track a later plug/unplug for a process that
    /// stays up for days.
    /// MUTATION THAT REDDENS IT: arm the timer once instead of periodically.</summary>
    [Fact]
    public void TheTimerKeepsTicking()
    {
        var calls = 0;
        using var poll = new PowerSourceSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 3, budgetMs: 3000),
                    $"the period must repeat — three ticks within 3 s of a 20 ms period, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>A FAILING READ IS NOT A STOPPED SCHEDULE, and it delivers nothing: a transient EC stall must not
    /// freeze the row on a stale source, and the next interval is the retry. A failed read leaves the row as the
    /// last good value left it, which is what the app wants (a lost tick should not blank a valid reading).
    /// MUTATION THAT REDDENS IT: let the tick's exception escape <c>PeriodicSchedule</c>.</summary>
    [Fact]
    public void AFailingReadDoesNotStopTheSchedule()
    {
        var calls = 0;
        using var poll = new PowerSourceSchedule(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new InvalidOperationException("transient EC stall");
        }, TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 4, budgetMs: 3000),
                    $"two failing reads must not stop the schedule — a successful tick must still follow, saw {Volatile.Read(ref calls)}");
    }
}

/// <summary>Every literal the power-source row shows has a Russian entry, so the Russian build never silently
/// falls back to English. The literals are neutral keys.</summary>
public class BatteryPowerSourceLocalizationTests
{
    /// <summary>MUTATION: delete one of these rows from Localization/Strings.ru.resx.</summary>
    [Theory]
    [InlineData("bat.power_source")]
    [InlineData("bat.source_ac")]
    [InlineData("bat.source_usbc")]
    [InlineData("bat.source_battery")]
    public void EveryPowerSourceLiteralIsTranslated(string key)
        => Assert.True(Loc.Ru(key) is not null,
                       "the power-source row shows a literal with no entry in Localization/Strings.ru.resx, so the "
                       + "Russian build shows it in English:\n  " + key);
}
