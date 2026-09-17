using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The per-mode preset reads the UI build path makes, and the hardware read each of them used to take.
/// <see cref="LaptopService.CurrentFan(PerformanceProfile?)"/>,
/// <see cref="LaptopService.CurrentGpuOc(PerformanceProfile?)"/>,
/// <see cref="LaptopService.CurrentCo(PerformanceProfile?)"/> and
/// <see cref="LaptopService.CurrentCoDomains(PerformanceProfile?)"/> are one pair with
/// <see cref="LaptopService.CurrentModeKey(PerformanceProfile?)"/>: the preset VALUES live in the
/// <c>Settings</c> graph, but each lookup is keyed by the CURRENT mode, and the key comes from the
/// <c>PowerProfiles</c> port — a WMI transaction on Windows. Through the parameterless forms the UI build path
/// (which already holds the profile — <c>AppController</c>'s constructor and <c>RebuildForLanguage</c> read it
/// once and hand it to <c>BuildUi</c>) asked the port again, three times, on the UI thread, while the UI was
/// being built. That is the narrower remainder of the hazard recorded as D16 in docs/hardware-actor-design.md.
///
/// Three halves, and all three are needed — no one of them says anything alone:
/// <list type="number">
/// <item>the overload does NOT read the port (asserted with the fake's read counter, which stands in for the
/// EC round-trip);</item>
/// <item>it produces exactly what the parameterless form produces for the same profile — the change is a REUSE
/// of the caller's read, not a substitution; and</item>
/// <item>it uses the profile it was HANDED, not the one it could have read. Without this, half one is satisfied
/// by an overload that ignores its parameter and half two by one that reads anyway.</item>
/// </list>
///
/// The two callers that hold no profile are deliberately left reading, and the last test pins that they still
/// do — otherwise "the build path stopped reading" would be indistinguishable from "the reads moved elsewhere".
/// </summary>
public class PresetReadsWithProfileTests
{
    /// <summary>Half one: passing the profile means the port is not consulted. Each of the four is a separate
    /// EC round-trip on the real device, which is the whole cost this pair exists to remove.</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    [InlineData("co-domains")]
    public void WithAProfileInHand_NoReaderAsksThePort(string which)
    {
        var f = Setup();
        var cur = f.Service.CurrentProfile();
        var readsBefore = f.Pp!.CurrentCount;

        Read(which, f.Service, cur);

        Assert.Equal(readsBefore, f.Pp.CurrentCount);
    }

    /// <summary>Half two, fan: the same profile through either form lands on the same stored preset, field for
    /// field. The stored values are non-defaults on purpose — a default would make a wrong lookup invisible.</summary>
    [Fact]
    public void CurrentFan_WithTheCurrentProfile_IsWhatTheParameterlessFormReturns()
    {
        var f = Setup();
        f.Store.Settings.FanPresets["balanced"] = new FanPreset
        {
            Mode = (int)FanMode.Custom, Cpu = 42, Gpu = 84,
            CpuUseCurve = true, CpuCurve = [30, 60], GpuCurve = [40, 70],
        };
        var cur = f.Service.CurrentProfile();

        var reused = f.Service.CurrentFan(cur);
        var reading = f.Service.CurrentFan();

        Assert.Equal(reading.Mode, reused.Mode);
        Assert.Equal(reading.Cpu, reused.Cpu);
        Assert.Equal(reading.Gpu, reused.Gpu);
        Assert.Equal(reading.CpuUseCurve, reused.CpuUseCurve);
        Assert.Equal(reading.GpuUseCurve, reused.GpuUseCurve);
        Assert.Equal(reading.CpuCurve, reused.CpuCurve);
        Assert.Equal(reading.GpuCurve, reused.GpuCurve);
        Assert.Equal(42, reused.Cpu);   // ...and it really is the stored one, not the type's default
    }

    /// <summary>Half two, GPU offsets.</summary>
    [Fact]
    public void CurrentGpuOc_WithTheCurrentProfile_IsWhatTheParameterlessFormReturns()
    {
        var f = Setup();
        f.Store.Settings.GpuOcPresets["balanced"] = new GpuOcPreset { Core = 150, Mem = 800 };
        var cur = f.Service.CurrentProfile();

        var reused = f.Service.CurrentGpuOc(cur);
        var reading = f.Service.CurrentGpuOc();

        Assert.Equal(reading.Core, reused.Core);
        Assert.Equal(reading.Mem, reused.Mem);
        Assert.Equal(150, reused.Core);
    }

    /// <summary>Half two, Curve Optimizer — both the preset and the rows the UI renders from it, since
    /// <see cref="LaptopService.CurrentCoDomains(PerformanceProfile?)"/> reaches the preset through the
    /// overload rather than rebuilding the key itself.</summary>
    [Fact]
    public void CurrentCo_AndItsRows_WithTheCurrentProfile_AreWhatTheParameterlessFormsReturn()
    {
        var f = Setup();
        f.Store.Settings.CoPresets["balanced"] = new CoPreset { AllCore = -20 };
        var cur = f.Service.CurrentProfile();

        var reused = f.Service.CurrentCo(cur);
        var reading = f.Service.CurrentCo();
        Assert.Equal(reading.AllCore, reused.AllCore);
        Assert.Equal(-20, reused.AllCore);

        // A CPU without domain control renders one all-core row, so the array is the preset's value.
        Assert.Equal(f.Service.CurrentCoDomains(), f.Service.CurrentCoDomains(cur));
        Assert.Equal(new[] { -20 }, f.Service.CurrentCoDomains(cur));
    }

    /// <summary>Half three: the argument is USED. A profile deliberately not the current one must select the
    /// preset — otherwise the overload could quietly be the parameterless form behind a different signature, and
    /// the zero-read test above would still pass while nothing was amortized on the real machine. This is the
    /// "reuse, not substitution" assertion: a substitution would key off the hardware's profile and land on
    /// Balanced's values here.</summary>
    [Theory]
    [InlineData("fan")]
    [InlineData("gpu-oc")]
    [InlineData("co")]
    public void WithAProfileThatIsNotTheCurrentOne_ItKeysOffThatProfile(string which)
    {
        var f = Setup();
        f.Store.Settings.FanPresets["performance"]    = new FanPreset   { Cpu = 42 };
        f.Store.Settings.GpuOcPresets["performance"]  = new GpuOcPreset { Core = 150 };
        f.Store.Settings.CoPresets["performance"]     = new CoPreset    { AllCore = -20 };
        var readsBefore = f.Pp!.CurrentCount;

        var value = which switch
        {
            "fan"    => f.Service.CurrentFan(TestProfiles.Performance).Cpu,
            "gpu-oc" => f.Service.CurrentGpuOc(TestProfiles.Performance).Core,
            "co"     => f.Service.CurrentCo(TestProfiles.Performance).AllCore,
            _        => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        var expected = which switch { "fan" => 42, "gpu-oc" => 150, "co" => -20, _ => 0 };
        Assert.Equal(expected, value);
        Assert.Equal(readsBefore, f.Pp.CurrentCount);   // and it did not read to find that out
    }

    /// <summary>An unreadable profile is not an error: the overload keys off "default", exactly as the
    /// parameterless form does when the port answers null. The UI hits this on a device with no profiles.</summary>
    [Fact]
    public void WithNoReadableProfile_ItKeysOffDefault_WithoutReading()
    {
        var f = SetupWith(current: null);
        f.Store.Settings.FanPresets["default"] = new FanPreset { Cpu = 55 };
        var cur = f.Service.CurrentProfile();          // the caller's read — counted, and the last one expected
        var readsBefore = f.Pp!.CurrentCount;

        var fan = f.Service.CurrentFan(cur);

        Assert.Equal(55, fan.Cpu);
        Assert.Equal(readsBefore, f.Pp.CurrentCount);
    }

    /// <summary>The scope, pinned: the two callers that hold NO profile still read, because neither can hand the
    /// overload one without changing its own contract — <c>LaptopService.ApplyModeGpuOc</c> is driven by the
    /// mode-change axis schedule, and <c>HardwareReconciler.Reapply</c>'s Curve-Optimizer reflect is reached from
    /// the pool thread with nothing but the service in hand. If a later wave removes those reads, this test is
    /// the one that must be updated deliberately rather than the zero-read test silently becoming the only one.</summary>
    [Fact]
    public void TheCallersThatHoldNoProfile_StillRead()
    {
        var f = Setup();

        var before = f.Pp!.CurrentCount;
        _ = f.Service.ApplyModeGpuOc();
        Assert.Equal(before + 1, f.Pp.CurrentCount);   // through the parameterless CurrentGpuOc, under _state

        before = f.Pp.CurrentCount;
        _ = f.Service.CurrentCoDomains();
        Assert.Equal(before + 1, f.Pp.CurrentCount);   // the mode-change reflect read, still on the caller's thread

        before = f.Pp.CurrentCount;
        f.Service.Reconciler.Reapply(ReapplyTrigger.ModeChange);
        Assert.True(f.Pp.CurrentCount > before, "the mode-change path still reads the live profile");
    }

    // The device: a five-profile port reporting Balanced, a Curve Optimizer with no domains (the all-core path,
    // so the rows are the stored number). No fan/GPU port: neither reader touches one.
    private static LaptopServiceFixture Setup() => SetupWith(TestProfiles.Balanced);

    /// <param name="current">What the port reports as active; null is the device with no profiles at all.</param>
    private static LaptopServiceFixture SetupWith(PerformanceProfile? current)
    {
        var f = LaptopServiceFixture.WithProfiles(current: current);
        f.Device.CurveOptimizer = new FakeCurveOptimizer();
        return f;
    }

    /// <summary>Invoke one reader's overload, so the theory cases stay one assertion each. The values are
    /// deliberately discarded: what is being measured here is the port traffic, not the preset.</summary>
    private static void Read(string which, LaptopService svc, PerformanceProfile? cur)
    {
        switch (which)
        {
            case "fan":        _ = svc.CurrentFan(cur);        break;
            case "gpu-oc":     _ = svc.CurrentGpuOc(cur);      break;
            case "co":         _ = svc.CurrentCo(cur);         break;
            case "co-domains": _ = svc.CurrentCoDomains(cur);  break;
            default: throw new ArgumentOutOfRangeException(nameof(which));
        }
    }
}
