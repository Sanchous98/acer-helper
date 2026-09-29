using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE AC GATE AND THE TRANSIENT PERFORMANCE PROFILE, service half. The owner's two rules for the guided sweep
/// only:
/// <list type="number">
/// <item><b>AC is mandatory.</b> On battery the platform can acknowledge a Curve-Optimizer write while
/// SUPPRESSING its effect (the documented <c>0xFD</c> "prerequisites not met" cause), so a probe could "pass"
/// against an offset that was never applied — a false stable verdict. The sweep is refused before any SMU write or
/// profile change; an unknown source fails closed.</item>
/// <item><b>A performance profile is forced for the run and restored after.</b> CO instability lives at the top
/// of the V/F curve, so testing from an energy-saving floor is weaker and risks a false pass. The forced profile
/// is TRANSIENT: it never reaches the remembered per-source slot, and the sweep's proposal is saved to the mode
/// the USER was in, not to Turbo.</item>
/// </list>
///
/// The manual undervolt is NOT gated by any of this — it is untouched by this file.
/// </summary>
public class LaptopServiceUndervoltSweepAcGateTests
{
    private static readonly LogicalCore Core0 = new(0, 0);

    private sealed class FakeAffinity : ICoreAffinity
    {
        public bool ThrowOnTopology { get; set; }
        public bool ThrowOnPin { get; set; }
        public CoreTopology TopologyResult { get; set; } = new([Core0], CoreTopologySource.PhysicalCores, null);
        public CoreTopology Topology()
        {
            if (ThrowOnTopology) throw new InvalidOperationException("topology blew up");
            return TopologyResult;
        }
        public bool PinCurrentThread(LogicalCore core, out string? error)
        {
            if (ThrowOnPin) throw new InvalidOperationException("pin blew up");
            error = null; return true;
        }
        public void UnpinCurrentThread() { }
    }

    /// <summary>Two clusters and the iGPU, so the sweep has something to walk and the iGPU slot must be carried.</summary>
    private static FakeCurveOptimizer TwoClustersAndGpu() => new FakeCurveOptimizer { Range = (-30, 0) }
        .WithDomains(
            new VoltageDomain("Zen 5", "ccd:0") { Cluster = CpuClusterKind.Performance },
            new VoltageDomain("Zen 5c", "ccd:1") { Cluster = CpuClusterKind.Efficiency },
            new VoltageDomain("iGPU", "gfx", Range: (-50, 0), MillivoltsPerCount: 5.0));

    // The single-core phase is off in these AC/profile tests (each is about one pass) so the suite stays fast;
    // the final soak is off too, because otherwise a real service probe would run the default 5/3-minute budgets.
    private static SweepOptions Fast(int? start = null) => new()
    {
        StartCounts = start,
        IncludeSingleCorePhase = false,
        ConfirmSoak = false,
        Load = new CpuLoadOptions
        {
            Width = LoadWidth.Scalar,
            PerCoreBudget = TimeSpan.FromMilliseconds(5),
            TotalBudget = TimeSpan.FromSeconds(5),
            ThermalLimitC = 95,
        },
    };

    /// <summary>A fixture with the sweep adapters, the SOURCE set explicitly (null = no reading ever, i.e.
    /// unknown) and the given affinity. The policy-bearing port is opt-in.</summary>
    private static LaptopServiceFixture Setup(bool? onAc, ICoreAffinity? affinity = null, bool turboOnAc = true)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = TwoClustersAndGpu();
        f.Device.CoreAffinity = affinity ?? new FakeAffinity();
        f.Device.Sensors = new FakeSensors { Snapshot = new SensorSnapshot { CpuTempC = 40 } };
        // A port that carries the Acer per-source policy (Turbo/Performance AC-only), forwarding to the fake so
        // every Set is recorded on f.Pp.
        f.Device.PowerProfiles = new SourcePoledProfiles(f.Pp!, turboOnAc);
        if (onAc == true) f.SyncPowerSource.Run(new BatteryInfoSnapshot { State = BatteryState.Charging });
        else if (onAc == false) f.SyncPowerSource.Run(new BatteryInfoSnapshot { State = BatteryState.Discharging });
        return f;
    }

    /// <summary>A port with the Acer-style per-source policy over a canonical fake: on AC Quiet/Balanced/
    /// Performance/Turbo, on battery Eco/Balanced — or, when <paramref name="turboOnAc"/> is false, nothing
    /// above Balanced even on AC (the "no turbo/performance available" case).</summary>
    private sealed class SourcePoledProfiles(IPowerProfiles inner, bool turboOnAc)
        : IPowerProfiles, IProfileTraits, IProfileAvailability
    {
        public string? LastError => inner.LastError;
        public IReadOnlyList<PerformanceProfile> All => inner.All;
        public IReadOnlyList<PerformanceProfile> Selectable() => inner.Selectable();
        public PerformanceProfile? Current() => inner.Current();
        public bool Set(PerformanceProfile profile) => inner.Set(profile);
        public ProfileTraits Traits(PerformanceProfile profile) => ProfileTraits.Of(inner, profile);
        public IReadOnlyList<PerformanceProfile> AvailableOn(bool onAc)
            => inner.All.Where(p => TestProfiles.TraitsOf(p).Kind switch
            {
                ProfileKind.Balanced => true,
                ProfileKind.Eco => !onAc,
                ProfileKind.Quiet => onAc,
                ProfileKind.Performance => onAc && turboOnAc,
                ProfileKind.Turbo => onAc && turboOnAc,
                _ => false,
            }).ToList();
    }

    // ---- the AC gate ----

    /// <summary>ON BATTERY IT IS REFUSED: no SMU write, no profile change, no domains, and a stop that is not a
    /// stability verdict. The refusal happens before the flag is even set, so nothing can leak.</summary>
    [Fact]
    public void OnBatteryTheSweepIsRefusedBeforeAnyWriteOrProfileChange()
    {
        var f = Setup(onAc: false);
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.NotOnAc, result.Stop);
        Assert.False(result.HasProposal);
        Assert.Empty(result.Domains);
        Assert.Empty(co.SetDomainsCalls);              // no SMU write
        Assert.Empty(co.SetCalls);
        Assert.Empty(f.Pp!.SetCalls);                  // no profile change
        Assert.False(f.Service.TuningInProgress);      // the flag was never taken
    }

    /// <summary>AN UNKNOWN SOURCE IS REFUSED TOO (fail closed): without a reading, AC cannot be proven, and a
    /// sweep must not start on an unproven source.</summary>
    [Fact]
    public void AnUnknownPowerSourceIsRefusedToo()
    {
        var f = Setup(onAc: null);
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.NotOnAc, result.Stop);
        Assert.Empty(co.SetDomainsCalls);
        Assert.Empty(f.Pp!.SetCalls);
        Assert.False(f.Service.TuningInProgress);
    }

    /// <summary>ON AC IT RUNS.</summary>
    [Fact]
    public void OnAcTheSweepRuns()
    {
        var f = Setup(onAc: true);
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.NotEqual(SweepStop.NotOnAc, result.Stop);
        Assert.True(result.HasProposal);
        Assert.NotEmpty(co.SetDomainsCalls);
    }

    /// <summary>The live source is exposed for the UI gate: true/false/null exactly as read.</summary>
    [Fact]
    public void OnAcExposesTheUnknownSourceAsNull()
    {
        Assert.Null(Setup(onAc: null).Service.OnAc);
        Assert.False(Setup(onAc: false).Service.OnAc);
        Assert.True(Setup(onAc: true).Service.OnAc);
    }

    /// <summary>ON USB-C PD THE SWEEP IS REFUSED, exactly as on the battery. The OS reports "AC" (it is
    /// charging), so the refusal can only come from the typed EC adapter reading demoting the source — the
    /// owner's rule ("заблокировать автодаунвольт" on USB-C). Nothing is written and no profile is changed.
    /// MUTATION: drop the USB-C demotion in RecomputeOnAc and this runs.</summary>
    [Fact]
    public void OnUsbCTheSweepIsRefusedBeforeAnyWriteOrProfileChange()
    {
        var f = Setup(onAc: true);                      // the OS says AC...
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;
        f.Service.SetPowerAdapter(PowerSource.UsbC);    // ...but the EC channel types it as USB-C PD

        Assert.False(f.Service.OnAc);                   // the effective source is battery-like

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.NotOnAc, result.Stop);
        Assert.False(result.HasProposal);
        Assert.Empty(result.Domains);
        Assert.Empty(co.SetDomainsCalls);
        Assert.Empty(co.SetCalls);
        Assert.Empty(f.Pp!.SetCalls);
        Assert.False(f.Service.TuningInProgress);
    }

    /// <summary>The barrel charger still runs the sweep, even after a USB-C reading — the demotion is not
    /// sticky.</summary>
    [Fact]
    public void OnBarrelTheSweepRuns_EvenAfterAUsbCReading()
    {
        var f = Setup(onAc: true);
        f.Service.SetPowerAdapter(PowerSource.UsbC);
        f.Service.SetPowerAdapter(PowerSource.Barrel);

        Assert.True(f.Service.OnAc);
        Assert.NotEqual(SweepStop.NotOnAc,
            f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None).Stop);
    }

    // ---- the transient performance profile ----

    /// <summary>A recorder for the profile switch's announcement, so a test can assert the sweep's force AND its
    /// restore are announced through the use case (the light claim the sweep used to bypass).</summary>
    private sealed class RecordingAnnouncer : AcerHelper.Application.IProfileAnnouncer
    {
        public List<PerformanceProfile> Announced { get; } = [];
        public void OnProfileApplied(PerformanceProfile applied) => Announced.Add(applied);
    }

    /// <summary>THE SWEEP'S FORCE AND ITS RESTORE ARE ANNOUNCED — the whole of the double-flash fix, at the
    /// service boundary. A forced-and-restored sweep announces the forced profile and then the pre-sweep one,
    /// exactly once each, because both go through the switch use case the lighting claim is part of. Before this
    /// slice the sweep wrote the port directly (<c>ApplyProfileTransient</c>) and announced nothing, so the ~1 s
    /// refresh pass repainted the palette the firmware had already flashed — the second blink the owner saw.</summary>
    [Fact]
    public void AForcedSweepAnnouncesTheForceAndTheRestore()
    {
        var f = Setup(onAc: true);
        f.Service.SetCoDomains([-5, -6, -7]);
        var announcer = new RecordingAnnouncer();
        f.Service.ProfileAnnouncer = announcer;

        f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        // The forced Turbo, then the restored Balanced — one announcement each, and no third (the refresh pass is
        // suppressed by the announcer's own pending claim, not by this use case counting anything).
        Assert.Equal(2, announcer.Announced.Count);
        Assert.Equal(TestProfiles.Turbo, announcer.Announced[0]);
        Assert.Equal(TestProfiles.Balanced, announcer.Announced[1]);
    }

    /// <summary>ON SUCCESS: Turbo is applied BEFORE the first probe and the PREVIOUS profile is restored after.
    /// The first SMU write is the first probe, so "Turbo before the first probe" is exact.</summary>
    [Fact]
    public void OnAcWithTurboTheProfileIsSwitchedBeforeTheProbeAndRestoredAfterSuccess()
    {
        var f = Setup(onAc: true);
        var co = (FakeCurveOptimizer)f.Device.CurveOptimizer!;
        f.Service.SetCoDomains([-5, -6, -7]);          // establish the user's mode, and a base to restore

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.Completed, result.Stop);
        Assert.Equal(["turbo", "balanced"], f.Pp!.SetCallIds);   // forced, then the previous restored
        Assert.Equal(TestProfiles.Balanced, f.Pp.CurrentProfile); // the machine is back where it started
        Assert.NotEmpty(co.SetDomainsCalls);
    }

    /// <summary>ON A CANCELLED RUN the profile is still restored — the finally rides every exit path.</summary>
    [Fact]
    public void ACancelledRunRestoresTheProfile()
    {
        var f = Setup(onAc: true);
        f.Service.SetCoDomains([-5, -6, -7]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = f.Service.RunUndervoltSweep(Fast(), null, cts.Token);

        Assert.Equal(SweepStop.Cancelled, result.Stop);
        Assert.Equal(["turbo", "balanced"], f.Pp!.SetCallIds);
        Assert.Equal(TestProfiles.Balanced, f.Pp.CurrentProfile);
    }

    /// <summary>ON THE FIRST ERROR the profile is still restored. The first error is produced through the REAL
    /// service path by an affinity whose pin throws, which the runner classifies as a faulted core and the sweep
    /// as a first failure — not a manufactured verdict.</summary>
    [Fact]
    public void AFirstErrorRunRestoresTheProfile()
    {
        var f = Setup(onAc: true, affinity: new FakeAffinity { ThrowOnPin = true });
        f.Service.SetCoDomains([-5, -6, -7]);

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal(SweepStop.FirstError, result.Stop);
        Assert.Equal(2, result.Domains.Count);                  // each cluster is swept independently
        Assert.NotNull(result.Domains[0].FirstFailing);
        Assert.Equal(["turbo", "balanced"], f.Pp!.SetCallIds);
        Assert.Equal(TestProfiles.Balanced, f.Pp.CurrentProfile);
    }

    /// <summary>WHEN THE SWEEP THROWS the profile is still restored and the throw propagates. The affinity's
    /// Topology throws, which the service's try/finally cannot swallow.</summary>
    [Fact]
    public void AThrowingRunRestoresTheProfile()
    {
        var f = Setup(onAc: true, affinity: new FakeAffinity { ThrowOnTopology = true });
        f.Service.SetCoDomains([-5, -6, -7]);

        Assert.Throws<InvalidOperationException>(() =>
            f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None));

        Assert.Equal(["turbo", "balanced"], f.Pp!.SetCallIds);
        Assert.Equal(TestProfiles.Balanced, f.Pp.CurrentProfile);
    }

    /// <summary>THE USER'S REMEMBERED MODE IS UNTOUCHED. The forced Turbo is a transient port write only: the
    /// remembered per-source slot keeps its base id and Turbo flag, and nothing is persisted for it.</summary>
    [Fact]
    public void TheRememberedPerSourceSlotIsUnchangedAfterAForcedProfileSweep()
    {
        var f = Setup(onAc: true);
        f.Service.ApplyProfile(TestProfiles.Balanced);            // remember Balanced as the base for AC
        f.Store.Settings.TurboToggles = true;                    // even with the Turbo switch enabled...
        f.Store.Settings.OnAc.BaseId = "balanced";
        f.Store.Settings.OnAc.Turbo = false;
        var savesBefore = f.Store.SaveCount;

        f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);  // the slot did not move to Turbo
        Assert.False(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(savesBefore, f.Store.SaveCount);            // and the forced profile never persisted
    }

    /// <summary>THE PROPOSAL SAVES TO THE USER'S REAL MODE, not to the forced Turbo. The result carries the mode
    /// key snapshotted before the profile was forced; with the live profile left sitting in Turbo, the save still
    /// writes the user's mode and never creates a "turbo" preset.</summary>
    [Fact]
    public void SaveTargetsTheSnapshottedUserModeNotTheForcedTurbo()
    {
        var f = Setup(onAc: true);
        f.Service.SetCoDomains([-5, -6, -7]);                  // user's mode = balanced
        var result = f.Service.RunUndervoltSweep(Fast(start: -3), null, CancellationToken.None);

        Assert.Equal("balanced", result.ProposedModeKey);

        f.Pp!.CurrentProfile = TestProfiles.Turbo;             // as if the save were reached with Turbo live
        var (ok, error) = f.Service.SaveUndervoltSweep(result);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(result.Domains[0].Proposed, f.Store.Settings.CoPresets["balanced"].Domains["ccd:0"]);
        Assert.DoesNotContain("turbo", f.Store.Settings.CoPresets.Keys);
    }

    /// <summary>NO TURBO/PERFORMANCE ON THIS SOURCE: the sweep still runs and the profile is NEVER touched.</summary>
    [Fact]
    public void WithoutAnAvailablePerformanceProfileTheSweepRunsAndLeavesTheProfileAlone()
    {
        var f = Setup(onAc: true, turboOnAc: false);           // AC offers only Quiet/Balanced
        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.True(result.HasProposal);
        Assert.Empty(f.Pp!.SetCalls);                           // the profile was not changed at all
    }

    /// <summary>A machine with NO power profiles runs the sweep without changing anything.</summary>
    [Fact]
    public void WithNoPowerProfilesTheSweepRunsAndTouchesNothing()
    {
        var f = Setup(onAc: true);
        f.Device.PowerProfiles = null;

        var result = f.Service.RunUndervoltSweep(Fast(), null, CancellationToken.None);

        Assert.True(result.HasProposal);
    }
}

/// <summary>The guided-sweep card's AC gate: the Start button is disabled off AC, and a click is refused with a
/// message rather than being allowed to reach the confirmation or the run.</summary>
public class UndervoltSweepViewModelAcGateTests
{
    private static SweepDomain D(int index = 0) => new(index, $"ccd:{index}", "Zen 5", -5, 0, 2.5);

    private static SweepResult Result() => new(
        [new SweepDomainResult(D(), -3, -4, new LogicalCore(0, 0), 3, true, true, 1, null)],
        SweepStop.FirstError, 10, null);

    private static UndervoltSweepViewModel Vm(
        bool? onAc,
        Func<IProgress<SweepProgress>?, CancellationToken, Task<SweepResult>>? run = null,
        List<string>? reported = null,
        Func<Task<bool>>? confirm = null)
        => new([D()], TimeSpan.FromMinutes(1),
               confirm ?? (() => Task.FromResult(true)),
               run ?? ((_, _) => Task.FromResult(Result())),
               _ => Task.FromResult<(bool, IReadOnlyList<int>?, string?)>((true, null, null)),
               _ => { }, _ => { }, _ => { }, () => { }, s => reported?.Add(s), Eventually.Sync, onAc);

    [Fact]
    public async Task OnBatteryTheStartIsRefusedWithoutConfirmingOrRunning()
    {
        var ran = false;
        var confirmed = false;
        var reported = new List<string>();
        var vm = Vm(onAc: false,
            run: (_, _) => { ran = true; return Task.FromResult(Result()); },
            reported: reported,
            confirm: () => { confirmed = true; return Task.FromResult(true); });

        Assert.False(vm.CanStart);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(ran);
        Assert.False(confirmed);                             // the refusal is before the consent prompt
        Assert.False(vm.HasResult);
        Assert.False(vm.IsRunning);
        Assert.Contains(reported, t => t.Contains("charger"));
    }

    [Fact]
    public async Task AnUnknownSourceRefusesTheStart()
    {
        var ran = false;
        var vm = Vm(onAc: null, run: (_, _) => { ran = true; return Task.FromResult(Result()); });

        Assert.False(vm.CanStart);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(ran);
    }

    [Fact]
    public async Task OnAcTheStartRuns()
    {
        var vm = Vm(onAc: true);

        Assert.True(vm.CanStart);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
    }

    /// <summary>The live source can flip the start button without a rebuild.</summary>
    [Fact]
    public void SetOnAcMovesTheStartButton()
    {
        var vm = Vm(onAc: false);
        Assert.False(vm.CanStart);

        vm.SetOnAc(true);
        Assert.True(vm.CanStart);

        vm.SetOnAc(null);                                     // unplugged/unknown -> fails closed
        Assert.False(vm.CanStart);
    }

    /// <summary>The start hint states the AC requirement — so it is visible before the click, and explains why the
    /// button is greyed on battery — but NOT the procedure, which the confirmation spells out.</summary>
    [Fact]
    public void TheStartHintNamesTheAcRequirement()
    {
        var vm = Vm(onAc: true);

        Assert.Contains("original charger", vm.StartHint);
        Assert.DoesNotContain("performance profile", vm.StartHint);
    }

    /// <summary>The refusal stop is rendered as a charger message, never as a stability outcome.</summary>
    [Fact]
    public void TheRefusalStopTextIsTranslated()
        => Assert.True(Loc.Ru("uv.refused_ac") is not null);
}
