using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The three things a fan EDIT does beyond writing the stored preset — and they are here rather than beside the
/// stub tests because a stub cannot see any of them: they are properties of the graph and of the EC, not of the
/// rule.
///
/// WHY THESE EXIST AT ALL. The family's use cases are pinned through their contracts with a stub
/// (<c>AppliedEditUseCasesTests</c>), which is what the split was for. But a stub stops at the contract: whether
/// an edit reaches the hardware at all, whether the deadband it clears really was cleared, and whether the array
/// it files is the caller's or a copy are all on the far side, and when the edit paths moved into
/// <see cref="IFanAxisTarget"/>'s implementation each of them became a line that could be deleted with the WHOLE
/// suite staying green — measured, not supposed: removing the curve pass from <c>ReplaceCurve</c>, removing the
/// deadband reset from it, and making the read hand back a snapshot each left 964/964 passing before these tests
/// were written.
///
/// The names say what HAPPENS. Nothing here asserts what the app ought to do; each records a property the two
/// edit paths have always had.
/// </summary>
public class FanEditWiringTests
{
    /// <summary>A machine in Custom mode with both fans on fixed speeds — no curve and no sensor influence, so
    /// every duty below is a literal rather than a sample of a temperature ramp. The preset is seeded directly
    /// rather than through a Set method, so the arrangement does not depend on what is being measured.</summary>
    private static (LaptopServiceFixture F, FakeFanControl Fan) Arranged()
    {
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset { Mode = (int)FanMode.Custom, Cpu = 77, Gpu = 88 };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        var fan = new FakeFanControl();
        f.Device.FanControl = fan;
        return (f, fan);
    }

    /// <summary>A curve edit DRIVES THE FANS. Storing the new curve is only half of what the edit means — the
    /// other half is that the curve takes effect on this refresh rather than on the next temperature change, which
    /// is why the two edit paths call the curve pass at all. A curve edit that only filed the preset would look
    /// completely correct from the settings file and leave the user's fans running the old ramp.
    ///
    /// Both duties are the stored fixed speeds: each fan's curve is OFF, so the curve pass drives it at its fixed
    /// speed whatever the temperature — which is what makes the expectation below a value rather than a sample.
    ///
    /// MUTATION THAT REDDENS IT: deleting <c>ApplyCustom(ReadSensors())</c> from
    /// <c>IFanAxisTarget.ReplaceCurve</c> (the whole suite stayed green under it before this test).</summary>
    [Fact]
    public void ACurveEditDrivesTheFansFromTheCurves()
    {
        var (f, fan) = Arranged();

        f.Service.SetFanCurve(gpu: false, use: false, points: [1, 2, 3, 4, 5]);

        Assert.Equal([(77, 88)], fan.SpeedCalls);
        Assert.Equal([FanMode.Custom], fan.ModeCalls);
    }

    /// <summary>An edit CLEARS THE DEADBAND, so the fans are driven even though nothing about them moved. The
    /// deadband's whole job is to suppress a write when the duty has not changed — which is exactly what an edit
    /// to the OTHER fan produces, and exactly the write the user is waiting for. Without the reset the first edit
    /// of a session would be silently swallowed until the temperature happened to move the duty far enough.
    ///
    /// The control is the first call's own write: the speeds are cleared before the second edit, so an empty list
    /// at the end cannot be satisfied by an edit that drove the fans earlier in the test.
    ///
    /// MUTATION THAT REDDENS IT: deleting <c>_fanCurve.Reset()</c> from <c>IFanAxisTarget.ReplaceCurve</c> (the
    /// whole suite stayed green under it before this test).</summary>
    [Fact]
    public void AnEditClearsTheDeadband_SoTheFansAreDrivenAgainEvenThoughNothingMoved()
    {
        var (f, fan) = Arranged();
        f.Service.SetFanCurve(gpu: false, use: false, [1, 2, 3, 4, 5]);   // establishes a committed duty
        Assert.Equal([(77, 88)], fan.SpeedCalls);                         // control: the pass really writes
        fan.SpeedCalls.Clear();

        f.Service.SetFanCurve(gpu: true, use: false, [1, 2, 3, 4, 5]);    // the OTHER fan's half

        Assert.Equal([(77, 88)], fan.SpeedCalls);
    }

    /// <summary>A curve edit FILES THE CALLER'S ARRAY, by reference, and leaves the other half's array alone as
    /// well. This is not tidiness: the array is the UI's own curve, handed over verbatim by both edit paths and
    /// never copied on the way in (nothing here validates its length either — the fan model is the thing that
    /// tolerates a null, short or over-long one), so a read that handed back a snapshot would re-point the stored
    /// preset's arrays at copies on EVERY edit, including the edit that named no array at all.
    ///
    /// The untouched half is asserted with <c>Assert.Same</c> too, and that is the half a snapshot would break
    /// first: a copy of the GPU curve is invisible in any value comparison, because it holds the same numbers.
    ///
    /// MUTATION THAT REDDENS IT: <c>IFanAxisTarget.Stored</c> returning <c>AxisStateOf(StoredFan().Snapshot())</c>
    /// (the whole suite stayed green under it before this test).</summary>
    [Fact]
    public void ACurveEditFilesTheVeryArrayTheCallerPassed_AndLeavesTheOtherHalfsAlone()
    {
        var gpuCurve = new[] { 40, 50, 60, 70, 80 };
        var settings = new Settings();
        settings.FanPresets["balanced"] = new FanPreset
        {
            Mode = (int)FanMode.Max, Cpu = 42, Gpu = 84, GpuCurve = gpuCurve,
        };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);
        f.Device.FanControl = new FakeFanControl();
        var points = new[] { 1, 2, 3, 4, 5 };

        f.Service.SetFanCurve(gpu: false, use: true, points);

        var stored = f.Store.Settings.FanPresets["balanced"];
        Assert.Same(points, stored.CpuCurve);
        Assert.Same(gpuCurve, stored.GpuCurve);
    }
}
