using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="LaptopService.TogglePerformance"/> — the performance hotkey — and through it the
/// profile-cycling rule <c>NextSelectable</c> (`LaptopService.Profiles.cs` `NextSelectable`).
///
/// NOT COVERED DIRECTLY: <c>NextSelectable</c> is <c>private static</c>, so InternalsVisibleTo (which
/// grants access to <c>internal</c> only) does not reach it. Every assertion below therefore goes through
/// <see cref="LaptopService.TogglePerformance"/>, its only caller, with <c>TurboToggles</c> false so the
/// cycle branch is the one taken. The fake <see cref="FakePowerProfiles.Set"/> returns true, so
/// <c>ApplyProfile</c> cannot mask the choice by failing — the returned profile IS the chosen one.
///
/// This matters because the plan names <c>NextSelectable</c> as a direct target and it is a silent-failure
/// site: a wrong answer applies a real profile to the EC, with no exception and no log line.
/// </summary>
public class LaptopServiceProfileCycleTests
{
    private static (PerformanceProfile? Landed, LaptopServiceFixture F) Toggle(
        PerformanceProfile? current,
        IEnumerable<PerformanceProfile>? selectable = null,
        IEnumerable<PerformanceProfile>? all = null,
        Settings? settings = null)
    {
        var f = LaptopServiceFixture.WithProfiles(settings ?? new Settings(), all, selectable, current);
        return (f.Service.TogglePerformance(), f);
    }

    // ---- the cycle order over the full selectable set, and the wrap at the end of the list ----

    [Theory]
    [InlineData("quiet", "eco")]
    [InlineData("eco", "balanced")]
    [InlineData("balanced", "performance")]
    [InlineData("performance", "turbo")]
    [InlineData("turbo", "quiet")]        // wraps: the step is (start + 1) % Count
    public void CyclesToTheNextProfileInAllOrder_AndWrapsAtTheEnd(string currentId, string expectedId)
    {
        var (landed, f) = Toggle(TestProfiles.ById(currentId));

        Assert.Equal(TestProfiles.ById(expectedId), landed);
        Assert.Equal([expectedId], f.Pp!.SetCallIds);      // exactly one write, and it is the chosen profile
    }

    /// <summary>Five toggles from any start return to it. Pins that the cycle is closed and that the
    /// start index is recomputed from the live current profile each time (not carried in the service).</summary>
    [Fact]
    public void FiveToggles_ReturnToTheStartingProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var seen = new List<string>();
        for (var i = 0; i < 5; i++) seen.Add(f.Service.TogglePerformance()!.Id);

        Assert.Equal(["performance", "turbo", "quiet", "eco", "balanced"], seen);
    }

    /// <summary>NOTE: with nothing readable as current, <c>start</c> stays 0 and the first candidate is
    /// <c>all[1]</c> — the hotkey SKIPS the first profile rather than entering the ring at it. Derived from
    /// the code (`LaptopService.Profiles.cs` `NextSelectable` — the `start` index scan), not from the prose doc.</summary>
    [Fact]
    public void WithNoReadableCurrentProfile_StartsAtTheSecondProfile_NotTheFirst()
    {
        var (landed, _) = Toggle(current: null);

        Assert.Equal(TestProfiles.Eco, landed);            // NOT Quiet, which sits at index 0
    }

    /// <summary>NOTE: a current profile that is not in <c>All</c> (a stale reading, a vendor id the list
    /// has since dropped) has the same effect — the search for its index finds nothing, <c>start</c> stays
    /// 0, and the cycle restarts from the second profile. It does not fall back to "stay put".</summary>
    [Fact]
    public void WithACurrentProfileThatIsNotInAll_AlsoStartsAtTheSecondProfile()
    {
        var (landed, _) = Toggle(new PerformanceProfile("ghost", "Ghost"));

        Assert.Equal(TestProfiles.Eco, landed);
    }

    // ---- Turbo / non-selectable profiles are skipped by id membership, not by kind ----

    [Fact]
    public void SkipsProfilesThatAreNotInSelectable()
    {
        // Turbo on battery: still in All (the UI shows it greyed), absent from Selectable.
        var (landed, _) = Toggle(TestProfiles.Quiet,
                                 selectable: [TestProfiles.Quiet, TestProfiles.Balanced, TestProfiles.Performance]);

        Assert.Equal(TestProfiles.Balanced, landed);       // Eco AND Turbo were both stepped over
    }

    [Fact]
    public void SkipsConsecutiveNonSelectableProfiles_AndWrapsPastThem()
    {
        var (landed, _) = Toggle(TestProfiles.Performance, selectable: [TestProfiles.Quiet]);

        Assert.Equal(TestProfiles.Quiet, landed);          // Turbo and the head of the list are all skipped
    }

    /// <summary>The <c>Ok</c> predicate is <c>sel.Count == 0 || sel.Any(...)</c> (`LaptopService.Profiles.cs` `NextSelectable` — the local `Ok`),
    /// so an EMPTY selectable list means "every profile is selectable", not "none is". A device that
    /// reports no subset must not have its hotkey freeze.</summary>
    [Fact]
    public void AnEmptySelectableList_MeansEverythingIsSelectable()
    {
        var (landed, _) = Toggle(TestProfiles.Balanced, selectable: []);

        Assert.Equal(TestProfiles.Performance, landed);
    }

    // ---- the degenerate sets ----

    [Fact]
    public void ASingleProfile_CyclesToItself_AndReappliesIt()
    {
        var f = LaptopServiceFixture.WithProfiles(all: [TestProfiles.Balanced], current: TestProfiles.Balanced);

        Assert.Equal(TestProfiles.Balanced, f.Service.TogglePerformance());
        Assert.Equal(["balanced"], f.Pp!.SetCallIds);      // the write still happens on every press
    }

    [Fact]
    public void WithNoProfilesAtAll_TheHotkeyReturnsNull_AndNeverWrites()
    {
        var f = LaptopServiceFixture.WithProfiles(all: []);

        Assert.Null(f.Service.TogglePerformance());
        Assert.Empty(f.Pp!.SetCalls);
        Assert.Equal(0, f.Store.SaveCount);
    }

    [Fact]
    public void WithNoPowerProfilesPort_TheHotkeyReturnsNull()
    {
        var f = new LaptopServiceFixture();

        Assert.Null(f.Service.TogglePerformance());
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>NOTE: <c>Selectable</c> non-empty but DISJOINT from <c>All</c> is the one shape the loop
    /// cannot answer. It never matches, the loop exhausts, and the fallback <c>return current ?? all[0]</c>
    /// (`LaptopService.Profiles.cs` `NextSelectable`) hands back a profile that is NOT selectable — which the hotkey then applies
    /// to the hardware. Silent: the caller gets a profile back and no error.</summary>
    [Fact]
    public void ASelectableListDisjointFromAll_FallsBackToTheCurrentProfile_WhichIsNotSelectable()
    {
        var (landed, f) = Toggle(TestProfiles.Balanced,
                                 selectable: [new PerformanceProfile("alien", "Alien")]);

        Assert.Equal(TestProfiles.Balanced, landed);
        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
        Assert.DoesNotContain(landed!, f.Pp.Selectable());   // the applied profile is not in Selectable()
    }

    /// <summary>...and with no readable current profile the same fallback yields <c>all[0]</c>, again
    /// without checking it is selectable.</summary>
    [Fact]
    public void ASelectableListDisjointFromAll_WithNoCurrent_AppliesTheFirstProfile()
    {
        var (landed, _) = Toggle(current: null,
                                 selectable: [new PerformanceProfile("alien", "Alien")]);

        Assert.Equal(TestProfiles.Quiet, landed);
    }

    // ---- side effects of the cycle: the live source's remembered base is rewritten, and persisted ----

    [Fact]
    public void Cycling_RemembersTheChosenProfileAsTheBases_AndClearsTheTurboFlag()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "quiet", Turbo = true } },
            current: TestProfiles.Balanced);

        f.Service.TogglePerformance();

        // ApplyProfile writes the AC slot because Slot is the AC slot while _onAc is still unknown.
        Assert.Equal("performance", f.Store.Settings.OnAc.BaseId);
        Assert.False(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void Cycling_WritesTheBatterySlotOnceTheSourceIsKnownToBeBattery()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Discharging });

        f.Service.TogglePerformance();

        Assert.Equal("performance", f.Store.Settings.OnBattery.BaseId);
        Assert.Equal("", f.Store.Settings.OnAc.BaseId);      // the AC slot is left alone
    }

    [Fact]
    public void AFailedSet_ReturnsNull_AndRemembersNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Pp!.SetResult = false;
        f.Pp.LastError = "EC refused";

        Assert.Null(f.Service.TogglePerformance());
        Assert.Equal("", f.Store.Settings.OnAc.BaseId);       // ApplyProfile stores only after a successful Set
        Assert.Equal(0, f.Store.SaveCount);

        // The reason comes back FROM the call rather than being parked in a field for whoever reads it last, and
        // the hotkey drops it — its caller shows no message. So the propagation claim is asserted on the path
        // that HAS a reader: a direct ApplyProfile, which is what the profile row uses.
        Assert.Equal("EC refused", f.Service.ApplyProfile(TestProfiles.Performance).error);
    }
}

/// <summary>
/// <see cref="LaptopService.TogglePerformance"/>'s other branch: with <c>Settings.TurboToggles</c> on the
/// hotkey becomes a Turbo SWITCH over a remembered base (<see cref="LaptopService.SetTurbo"/>), not a
/// cycle. The base id is preserved so switching Turbo off has somewhere to return to.
/// </summary>
public class LaptopServiceTurboToggleTests
{
    private static LaptopServiceFixture Turboable(Settings settings,
                                                  PerformanceProfile? current,
                                                  IEnumerable<PerformanceProfile>? selectable = null,
                                                  IEnumerable<PerformanceProfile>? all = null)
        => LaptopServiceFixture.WithProfiles(settings, all ?? TestProfiles.All, selectable, current);

    [Fact]
    public void On_AppliesTurboOverTheCurrentBase_AndCapturesThatBase()
    {
        var f = Turboable(new Settings { TurboToggles = true }, TestProfiles.Balanced);

        Assert.Equal(TestProfiles.Turbo, f.Service.TogglePerformance());
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);   // captured, so Off has a target
        Assert.True(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void Off_ReturnsToTheRememberedBase_AndClearsTheTurboFlag()
    {
        var f = Turboable(new Settings
                          {
                              TurboToggles = true,
                              OnAc = new ProfileMemory { BaseId = "performance", Turbo = true },
                          },
                          TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Performance, f.Service.TogglePerformance());
        Assert.Equal(["performance"], f.Pp!.SetCallIds);
        Assert.Equal("performance", f.Store.Settings.OnAc.BaseId);
        Assert.False(f.Store.Settings.OnAc.Turbo);
    }

    [Fact]
    public void Off_FallsBackToBalanced_WhenTheRememberedBaseNoLongerExists()
    {
        var f = Turboable(new Settings
                          {
                              TurboToggles = true,
                              OnAc = new ProfileMemory { BaseId = "ghost", Turbo = true },
                          },
                          TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Balanced, f.Service.TogglePerformance());
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);   // and the stale id is replaced
    }

    [Fact]
    public void On_WithNoTurboProfileInAll_DoesNothing()
    {
        var f = Turboable(new Settings { TurboToggles = true },
                          TestProfiles.Balanced,
                          all: [TestProfiles.Quiet, TestProfiles.Balanced, TestProfiles.Performance]);

        Assert.Null(f.Service.TogglePerformance());
        Assert.Empty(f.Pp!.SetCalls);                          // nothing was attempted, so there is no failure to report
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>With no readable current profile there is nothing to capture, so the base stays empty.
    /// NOTE the knock-on: <see cref="LaptopService.CurrentModeKey()"/> returns the Turbo id itself while
    /// the base is empty, so the per-mode presets bucket under "turbo" rather than under the mode the
    /// machine will return to when Turbo is switched off.</summary>
    [Fact]
    public void On_WithNoReadableCurrent_DoesNotCaptureABase()
    {
        var f = Turboable(new Settings { TurboToggles = true }, current: null);

        Assert.Equal(TestProfiles.Turbo, f.Service.TogglePerformance());
        Assert.Equal("", f.Store.Settings.OnAc.BaseId);
        Assert.True(f.Store.Settings.OnAc.Turbo);
        Assert.Equal("turbo", f.Service.CurrentModeKey());
    }

    [Fact]
    public void SetTurboOn_WhenAlreadyInTurbo_KeepsTheBaseAndReappliesTurbo()
    {
        var f = Turboable(new Settings
                          {
                              TurboToggles = true,
                              OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                          },
                          TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Turbo, f.Service.SetTurbo(true).applied);
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);              // re-applied, not skipped
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);  // a Turbo current captures nothing
    }

    [Fact]
    public void Off_WithNoResolvableBase_ReturnsNull_AndWritesNothing()
    {
        var f = Turboable(new Settings { TurboToggles = true },
                          TestProfiles.Turbo,
                          all: [TestProfiles.Turbo]);            // a profile set with no non-Turbo entry

        Assert.Null(f.Service.TogglePerformance());
        Assert.Empty(f.Pp!.SetCalls);
        Assert.Equal(0, f.Store.SaveCount);
    }

    [Fact]
    public void Off_PropagatesAFailedSet_AndLeavesTheTurboFlagSet()
    {
        var f = Turboable(new Settings
                          {
                              TurboToggles = true,
                              OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                          },
                          TestProfiles.Turbo);
        f.Pp!.SetResult = false;
        f.Pp.LastError = "EC refused";

        Assert.Null(f.Service.TogglePerformance());
        Assert.True(f.Store.Settings.OnAc.Turbo);              // ApplyProfile returns before clearing it
        // No reason to assert: this path is the Turbo switch, whose caller (the hotkey) shows nothing, so the
        // failure is dropped here by design — the flag staying set is what the assertion above pins.
    }

    /// <summary>The setting itself is read fresh on every press, so flipping it switches the hotkey's
    /// behaviour without rebuilding anything.</summary>
    [Fact]
    public void TurningTurboTogglesOnAtRuntime_SwitchesTheHotkeyFromCyclingToTheTurboSwitch()
    {
        var f = Turboable(new Settings { TurboToggles = false }, TestProfiles.Balanced);

        Assert.Equal(TestProfiles.Performance, f.Service.TogglePerformance());   // cycles

        f.Service.SetTurboToggles(true);

        Assert.Equal(TestProfiles.Turbo, f.Service.TogglePerformance());         // switches
        Assert.Equal(["performance", "turbo"], f.Pp!.SetCallIds);
    }
}

/// <summary>
/// <see cref="LaptopService.CurrentModeKey()"/> and its overload — the key that buckets EVERY per-mode
/// preset (fans, lighting, GPU offsets, Curve Optimizer, CPU power). A wrong key means a profile's
/// settings are silently read from, and written to, another mode's slot.
/// </summary>
public class LaptopServiceModeKeyTests
{
    [Fact]
    public void WithNoPowerProfilesPort_TheKeyIsDefault()
    {
        var f = new LaptopServiceFixture();

        Assert.Equal("default", f.Service.CurrentModeKey());
        Assert.Equal("default", f.Service.CurrentModeKey(null));
    }

    [Fact]
    public void WithAnUnreadableCurrentProfile_TheKeyIsDefault()
    {
        var f = LaptopServiceFixture.WithProfiles(current: null);

        Assert.Null(f.Service.CurrentProfile());
        Assert.Equal("default", f.Service.CurrentModeKey());
        Assert.Equal("default", f.Service.CurrentModeKey(f.Service.CurrentProfile()));
    }

    /// <summary>Every non-Turbo kind buckets by its own id — the kind is not consulted at all.</summary>
    [Theory]
    [InlineData("quiet")]
    [InlineData("eco")]
    [InlineData("balanced")]
    [InlineData("performance")]
    public void ANonTurboProfile_KeysByItsOwnId(string id)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.ById(id));

        Assert.Equal(id, f.Service.CurrentModeKey());
    }

    /// <summary>The other half of "a non-Turbo profile keys by its own id": it keeps its own id even with the
    /// Turbo switch ON and a base remembered, so the sharing branch belongs to a Turbo current profile ALONE.
    /// That conjunction — the switch AND the kind — is the whole rule (<c>LaptopService.Profiles.cs</c>
    /// <see cref="LaptopService.ModeKeyFor"/>), and this is the case that moves if the kind clause is dropped:
    /// the presets of the mode the machine is actually in would then be read from, and written to, the
    /// remembered base's slot instead.
    ///
    /// The state is one the slot and the hardware can genuinely disagree about, because they are written by
    /// different things: the slot records what the APP last did, the current profile reports what the
    /// HARDWARE last did — a profile changed outside the app, or a failed <c>Attempt</c> in
    /// <c>ApplyStoredMode</c>, leaves the remembered base naming a mode the machine is not in.
    ///
    /// <c>expected</c> is written out as a LITERAL rather than read back from the fixture, and the last
    /// assertion states the point mechanically: the key is NOT the remembered base.</summary>
    [Theory]
    [InlineData("quiet", "performance", "quiet")]
    [InlineData("balanced", "performance", "balanced")]
    [InlineData("performance", "quiet", "performance")]
    [InlineData("eco", "balanced", "eco")]
    public void ANonTurboProfile_WithTheTurboSwitchOn_StillKeysByItsOwnId(string currentId, string baseId,
                                                                         string expected)
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = baseId, Turbo = false },
                      },
            current: TestProfiles.ById(currentId));

        var key = f.Service.CurrentModeKey();

        Assert.Equal(expected, key);
        Assert.NotEqual(baseId, key);          // the remembered base was not shared
    }

    /// <summary>Turbo used as a plain selectable profile is a mode of its own: the flag is off, so the
    /// key is the Turbo id even though a base is remembered.</summary>
    [Fact]
    public void Turbo_WithTurboTogglesOff_KeysByTheTurboId()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "balanced" } },
            current: TestProfiles.Turbo);

        Assert.Equal("turbo", f.Service.CurrentModeKey());
    }

    /// <summary>Turbo as a SWITCH shares its base's key — Turbo is not a standalone mode then, so the
    /// presets the user configured for Balanced are the ones that apply while Turbo sits over it.</summary>
    [Fact]
    public void Turbo_WithTurboTogglesOn_KeysByTheRememberedBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.Equal("balanced", f.Service.CurrentModeKey());
    }

    [Theory]
    [InlineData("")]          // never captured (Turbo engaged with nothing readable as current)
    public void Turbo_WithNoRememberedBase_KeysByTheTurboId(string baseId)
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = baseId, Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.Equal("turbo", f.Service.CurrentModeKey());
    }

    /// <summary>NOTE: the guard is only <c>BaseId.Length &gt; 0</c> — the id is never checked against
    /// <c>All</c>. A remembered base for a profile this machine no longer exposes therefore becomes the
    /// key verbatim, and that mode's presets are written under a name no profile can ever select again.</summary>
    [Fact]
    public void Turbo_WithAStaleRememberedBase_KeysByThatStaleId()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "ghost", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.DoesNotContain(f.Pp!.All, p => p.Id == "ghost");
        Assert.Equal("ghost", f.Service.CurrentModeKey());
    }

    /// <summary>The key follows the LIVE power source: <c>Slot</c> is the battery slot once
    /// <see cref="LaptopService.SyncPowerSource"/> has seen a discharge, so the same Turbo state keys
    /// differently on AC and on battery.</summary>
    [Fact]
    public void TheKeyFollowsThePowerSourceSlot()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "performance", Turbo = true },
                          OnBattery = new ProfileMemory { BaseId = "quiet", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.Equal("performance", f.Service.CurrentModeKey());            // unknown source reads as AC

        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Discharging });

        Assert.Equal("quiet", f.Service.CurrentModeKey());
    }

    /// <summary>NOTE the asymmetry, derived from <c>Slot</c> (`LaptopService.Profiles.cs` `Slot`): on a fresh service
    /// <c>_onAc</c> is null, so the AC slot is the live one and a remembered mode for BATTERY alone has no
    /// effect on the key. The machine may well be on battery — the first reading is what tells the service.</summary>
    [Fact]
    public void BeforeAnyPowerSourceReading_TheAcSlotIsTheLiveOne()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnBattery = new ProfileMemory { BaseId = "quiet", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.Equal("turbo", f.Service.CurrentModeKey());                  // not "quiet"
    }

    /// <summary>The overload exists so a refresh pass can read the hardware profile once. It must agree with
    /// the no-argument form for whatever the port currently reports — and THAT AGREEMENT is all this test
    /// pins: both sides go through the one rule (<c>LaptopService.Profiles.cs</c>
    /// <see cref="LaptopService.ModeKeyFor"/>), so a change to it moves both answers together and every row
    /// below stays green.
    ///
    /// The rows are therefore NOT the rule's coverage, however much the table looks like a key table. The rule
    /// itself is pinned by the tests in this class that expect a LITERAL key —
    /// <see cref="ANonTurboProfile_WithTheTurboSwitchOn_StillKeysByItsOwnId"/>,
    /// <see cref="Turbo_WithTurboTogglesOn_KeysByTheRememberedBase"/>,
    /// <see cref="Turbo_WithTurboTogglesOff_KeysByTheTurboId"/>,
    /// <see cref="Turbo_WithNoRememberedBase_KeysByTheTurboId"/> — and, for the cross-product of the rule's
    /// four inputs, directly by <c>ModeKeyTests</c>. This one is kept for the delegation alone.</summary>
    [Theory]
    [InlineData("quiet", false, "")]
    [InlineData("balanced", true, "performance")]
    [InlineData("balanced", false, "quiet")]
    [InlineData("turbo", true, "balanced")]
    [InlineData("turbo", true, "")]
    [InlineData("turbo", false, "balanced")]
    public void TheTwoOverloadsAgree(string currentId, bool turboToggles, string baseId)
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = turboToggles,
                          OnAc = new ProfileMemory { BaseId = baseId, Turbo = turboToggles },
                      },
            current: TestProfiles.ById(currentId));

        var fresh = f.Service.CurrentModeKey();
        var reused = f.Service.CurrentModeKey(f.Service.CurrentProfile());

        Assert.Equal(fresh, reused);
    }

    /// <summary>...and the overload is genuinely independent of the port: it answers from the profile it
    /// is handed, so a caller may key a mode it has already read.</summary>
    [Fact]
    public void TheOverloadAnswersFromTheProfileItIsHanded()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.Equal("performance", f.Service.CurrentModeKey(TestProfiles.Performance));
        Assert.Equal("default", f.Service.CurrentModeKey(null));
        Assert.Equal("balanced", f.Service.CurrentModeKey());        // the port is untouched
    }
}

/// <summary>
/// <see cref="LaptopService.BaseProfile()"/> — which profile the UI shows as selected. While Turbo is
/// engaged the current profile is Turbo, which is not what the user picked, so the answer comes from the
/// remembered base instead.
/// </summary>
public class LaptopServiceBaseProfileTests
{
    [Fact]
    public void WithNoPowerProfilesPort_ThereIsNoBase()
    {
        Assert.Null(new LaptopServiceFixture().Service.BaseProfile());
        Assert.Null(new LaptopServiceFixture().Service.BaseProfile(null));
    }

    [Fact]
    public void ANonTurboCurrentProfile_IsItsOwnBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "quiet" } },
            current: TestProfiles.Performance);

        Assert.Equal(TestProfiles.Performance, f.Service.BaseProfile());
    }

    [Fact]
    public void WithTurboCurrent_TheRememberedBaseWins()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "performance" } },
            current: TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Performance, f.Service.BaseProfile());
    }

    /// <summary>An unreadable current profile is treated exactly like Turbo: the remembered base is the
    /// answer, so a failed EC read cannot blank the UI's selection.</summary>
    [Fact]
    public void WithNoReadableCurrent_TheRememberedBaseIsUsed()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "quiet" } },
            current: null);

        Assert.Equal(TestProfiles.Quiet, f.Service.BaseProfile());
    }

    [Theory]
    [InlineData("")]          // nothing remembered yet
    [InlineData("ghost")]     // remembered, but no longer in All
    public void WithNothingResolvable_ItFallsBackToTheBalancedProfile(string baseId)
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = baseId, Turbo = true } },
            current: TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Balanced, f.Service.BaseProfile());
    }

    [Fact]
    public void WithNoBalancedProfile_ItFallsBackToTheFirstNonTurboProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(
            all: [TestProfiles.Turbo, TestProfiles.Quiet, TestProfiles.Eco],
            current: TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Quiet, f.Service.BaseProfile());
    }

    [Fact]
    public void WithOnlyTurboProfiles_ThereIsNoBase()
    {
        var f = LaptopServiceFixture.WithProfiles(all: [TestProfiles.Turbo], current: TestProfiles.Turbo);

        Assert.Null(f.Service.BaseProfile());
    }

    /// <summary>NOTE: the remembered-base lookup filters by ID ONLY — there is no kind check
    /// (`LaptopService.Profiles.cs` `BaseProfile`). A slot holding the Turbo profile's own id therefore resolves to Turbo,
    /// which is reachable whenever a Turbo profile is picked directly (<c>ApplyProfile</c> stores any id)
    /// while the Turbo switch is on. The fallbacks behind it would have answered Balanced.</summary>
    [Fact]
    public void ASlotPointingAtTheTurboProfile_ResolvesToTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "turbo" } },
            current: TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Turbo, f.Service.BaseProfile());
    }

    [Fact]
    public void TheTwoOverloadsAgree_AndTheOverloadIgnoresThePort()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "performance" } },
            current: TestProfiles.Turbo);

        Assert.Equal(f.Service.BaseProfile(), f.Service.BaseProfile(f.Service.CurrentProfile()));
        Assert.Equal(TestProfiles.Quiet, f.Service.BaseProfile(TestProfiles.Quiet));   // the port still says Turbo
        Assert.Equal(TestProfiles.Performance, f.Service.BaseProfile(null));
    }

    [Fact]
    public void TheBaseFollowsTheLivePowerSource()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          OnAc = new ProfileMemory { BaseId = "performance", Turbo = true },
                          OnBattery = new ProfileMemory { BaseId = "quiet", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        Assert.Equal(TestProfiles.Performance, f.Service.BaseProfile());

        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Discharging });

        Assert.Equal(TestProfiles.Quiet, f.Service.BaseProfile());
    }
}

/// <summary>
/// <see cref="LaptopService.SourceProfile"/> / <see cref="LaptopService.SetSourceProfile"/> — the per-source
/// remembered mode, and <see cref="LaptopService.SyncPowerSource"/>, which applies it when the machine
/// changes source. Every path here ends in an EC write or a deliberate non-write.
/// </summary>
public class LaptopServicePowerSourceTests
{
    private static readonly BatteryInfoSnapshot OnAc = new() { State = BatteryState.Charging };
    private static readonly BatteryInfoSnapshot OnBattery = new() { State = BatteryState.Discharging };

    // ---- SourceProfile: the read side ----

    [Fact]
    public void WithNoPowerProfilesPort_ThereIsNoSourceProfile()
    {
        var f = new LaptopServiceFixture();

        Assert.Null(f.Service.SourceProfile(true));
        Assert.Null(f.Service.SourceProfile(false));
        Assert.False(f.Service.SetSourceProfile(true, TestProfiles.Balanced).ok);
    }

    [Fact]
    public void OnAFreshInstall_NeitherSourceHasAProfile()
    {
        var f = LaptopServiceFixture.WithProfiles();

        Assert.Null(f.Service.SourceProfile(true));
        Assert.Null(f.Service.SourceProfile(false));
    }

    [Fact]
    public void EachSourceReportsItsOwnRememberedProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(settings: new Settings
        {
            OnAc = new ProfileMemory { BaseId = "performance" },
            OnBattery = new ProfileMemory { BaseId = "quiet" },
        });

        Assert.Equal(TestProfiles.Performance, f.Service.SourceProfile(true));
        Assert.Equal(TestProfiles.Quiet, f.Service.SourceProfile(false));
    }

    [Fact]
    public void ARememberedIdThatNoLongerExists_ReadsAsNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "ghost" } });

        Assert.Null(f.Service.SourceProfile(true));
    }

    /// <summary>With the Turbo switch OFF, a stale Turbo flag in the slot is ignored: the slot means the
    /// BASE profile then, and Turbo is an ordinary selectable mode stored as a base of its own.</summary>
    [Fact]
    public void WithTurboTogglesOff_AStaleTurboFlagIsIgnored()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true } });

        Assert.Equal(TestProfiles.Balanced, f.Service.SourceProfile(true));
    }

    /// <summary>With the switch ON the slot means "Turbo over this base", so the Turbo profile is what the
    /// user is told the source is set to — no matter which profile the flag was over.</summary>
    [Fact]
    public void WithTurboTogglesOn_ATurboSlotReportsTheTurboProfile()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      });

        Assert.Equal(TestProfiles.Turbo, f.Service.SourceProfile(true));
    }

    /// <summary>NOTE: the Turbo profile is reported even when the device does not currently offer it
    /// (absent from <c>Selectable</c>, e.g. on battery) — unlike <c>ApplyStoredMode</c>, which refuses to
    /// apply a non-selectable Turbo. So the UI can name a mode the hardware would not accept.</summary>
    [Fact]
    public void WithTurboTogglesOn_ATurboSlotReportsTurboEvenWhenTurboIsNotSelectable()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            selectable: [TestProfiles.Quiet, TestProfiles.Balanced]);

        Assert.DoesNotContain(TestProfiles.Turbo, f.Service.SelectableProfiles());
        Assert.Equal(TestProfiles.Turbo, f.Service.SourceProfile(true));
    }

    [Fact]
    public void WithTurboTogglesOn_AndNoTurboProfileAtAll_TheSlotFallsBackToItsBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            all: [TestProfiles.Quiet, TestProfiles.Balanced, TestProfiles.Performance]);

        Assert.Equal(TestProfiles.Balanced, f.Service.SourceProfile(true));
    }

    // ---- SetSourceProfile: the write side ----

    [Fact]
    public void SettingTheLiveSource_StoresAndAppliesImmediately()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.True(f.Service.SetSourceProfile(true, TestProfiles.Performance).ok);

        Assert.Equal("performance", f.Store.Settings.OnAc.BaseId);
        Assert.False(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(["performance"], f.Pp!.SetCallIds);
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>The AC slot is the live one before any reading (<c>_onAc</c> is null), so the very first
    /// pick on a fresh install applies rather than waiting for a source change.</summary>
    [Fact]
    public void TheAcSlotIsLiveBeforeAnyBatteryReading()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Quiet);

        f.Service.SetSourceProfile(true, TestProfiles.Balanced);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
    }

    /// <summary>Setting the source the machine is NOT on must persist without touching the hardware: the
    /// stored mode is applied later, when the source actually becomes live.</summary>
    [Fact]
    public void SettingTheOtherSource_StoresWithoutApplying()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.True(f.Service.SetSourceProfile(false, TestProfiles.Quiet).ok);

        Assert.Equal("quiet", f.Store.Settings.OnBattery.BaseId);
        Assert.Empty(f.Pp!.SetCallIds);                  // nothing written
        Assert.Equal(1, f.Store.SaveCount);              // but persisted
    }

    [Fact]
    public void SettingTheLiveSourceToWhatItAlreadyIs_WritesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        Assert.True(f.Service.SetSourceProfile(true, TestProfiles.Balanced).ok);

        // ApplyStoredMode short-circuits on "already in that profile": the firmware re-flashes the
        // keyboard palette on every Set, so re-applying the same mode would blink the keyboard for nothing.
        Assert.Empty(f.Pp!.SetCallIds);
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);
    }

    [Fact]
    public void OnceTheSourceIsKnownToBeBattery_TheBatterySlotIsTheLiveOne()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(OnBattery);

        f.Service.SetSourceProfile(false, TestProfiles.Performance);

        Assert.Equal(["performance"], f.Pp!.SetCallIds);
        Assert.Equal("performance", f.Store.Settings.OnBattery.BaseId);
    }

    [Fact]
    public void AFailedApply_ReturnsFalse_AndPropagatesTheError()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Pp!.SetResult = false;
        f.Pp.LastError = "EC refused";

        var r = f.Service.SetSourceProfile(true, TestProfiles.Performance);

        Assert.False(r.ok);
        Assert.Equal("EC refused", r.error);                          // the reason travels WITH the result
        Assert.Equal("performance", f.Store.Settings.OnAc.BaseId);   // the choice is still remembered
    }

    // ---- Turbo stored as base + flag ----

    [Fact]
    public void PickingTurboWithTheSwitchOn_SeedsBalancedAsTheBase_ThenAppliesBaseAndTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            current: TestProfiles.Quiet);

        Assert.True(f.Service.SetSourceProfile(true, TestProfiles.Turbo).ok);

        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);   // seeded: Turbo is not a base
        Assert.True(f.Store.Settings.OnAc.Turbo);
        // Two writes: establish the base we sit over, then Turbo. NOTE that is two EC writes (and so two
        // keyboard-palette flashes) even though the user made one choice.
        Assert.Equal(["balanced", "turbo"], f.Pp!.SetCallIds);
    }

    [Fact]
    public void PickingTurboWithTheSwitchOn_OverAnAlreadyActiveBase_SkipsTheBaseWrite()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            current: TestProfiles.Balanced);

        Assert.True(f.Service.SetSourceProfile(true, TestProfiles.Turbo).ok);

        Assert.Equal(["turbo"], f.Pp!.SetCallIds);                // the base is already on
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);
    }

    [Fact]
    public void PickingTurboWithTheSwitchOn_KeepsAnAlreadyRememberedBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "performance" },
                      },
            current: TestProfiles.Performance);

        f.Service.SetSourceProfile(true, TestProfiles.Turbo);

        Assert.Equal("performance", f.Store.Settings.OnAc.BaseId);   // NOT re-seeded to Balanced
        Assert.True(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);
    }

    /// <summary>With the switch OFF, Turbo is an ordinary profile: it is stored as the base itself, which
    /// is what lets the UI highlight it as the selected mode.</summary>
    [Fact]
    public void PickingTurboWithTheSwitchOff_StoresItAsAPlainBase()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        f.Service.SetSourceProfile(true, TestProfiles.Turbo);

        Assert.Equal("turbo", f.Store.Settings.OnAc.BaseId);
        Assert.False(f.Store.Settings.OnAc.Turbo);
        Assert.Equal(["turbo"], f.Pp!.SetCallIds);
    }

    /// <summary>NOTE: when the device does not offer Turbo right now, <c>ApplyStoredMode</c> refuses to
    /// apply it (`LaptopService.Profiles.cs` `ApplyStoredMode` — the `wantTurbo` gate) and applies only the base — yet the slot still records
    /// <c>Turbo = true</c>. SourceProfile then reports Turbo while the hardware sits on Balanced, so the
    /// UI names a mode the machine is not in, with no error anywhere.</summary>
    [Fact]
    public void PickingTurboWithTheSwitchOn_WhenTurboIsNotSelectable_LeavesTheHardwareOnTheBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            selectable: [TestProfiles.Quiet, TestProfiles.Balanced, TestProfiles.Performance],
            current: TestProfiles.Quiet);

        Assert.True(f.Service.SetSourceProfile(true, TestProfiles.Turbo).ok);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);          // Turbo was never written
        Assert.Equal(TestProfiles.Balanced, f.Service.CurrentProfile());
        Assert.True(f.Store.Settings.OnAc.Turbo);              // ...but the slot says Turbo
        Assert.Equal(TestProfiles.Turbo, f.Service.SourceProfile(true));
    }

    // ---- SyncPowerSource: seeding and restoring ----

    [Fact]
    public void AnUnknownBatteryState_ChangesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = BatteryState.Unknown });

        Assert.Equal(0, f.Store.SaveCount);
        Assert.Empty(f.Pp!.SetCalls);
    }

    /// <summary>A source seen for the first time is SEEDED from the hardware, never forced: a fresh
    /// install must remember what the machine was already on.</summary>
    [Theory]
    [InlineData(BatteryState.Charging, "performance")]
    [InlineData(BatteryState.Idle, "performance")]        // Idle counts as AC
    public void TheFirstReadingOfASource_SeedsTheSlotFromTheHardware(BatteryState state, string expectedBase)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Performance);

        f.Service.SyncPowerSource(new BatteryInfoSnapshot { State = state });

        Assert.Equal(expectedBase, f.Store.Settings.OnAc.BaseId);
        Assert.False(f.Store.Settings.OnAc.Turbo);
        Assert.Empty(f.Pp!.SetCalls);                     // seeded, not applied
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void TheFirstDischarge_SeedsTheBatterySlot()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Quiet);

        f.Service.SyncPowerSource(OnBattery);

        Assert.Equal("quiet", f.Store.Settings.OnBattery.BaseId);
        Assert.Equal("", f.Store.Settings.OnAc.BaseId);
    }

    [Fact]
    public void SeedingWhileTheHardwareIsInTurbo_RemembersBalancedAsTheBase()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);   // the base under Turbo is unknown
        Assert.True(f.Store.Settings.OnAc.Turbo);
        Assert.Empty(f.Pp!.SetCalls);
    }

    [Fact]
    public void SeedingWhileTheHardwareIsInTurbo_WithNoBalancedProfile_UsesTheFirstNonTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            all: [TestProfiles.Turbo, TestProfiles.Quiet],
            current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal("quiet", f.Store.Settings.OnAc.BaseId);
    }

    [Fact]
    public void SeedingWhileTheHardwareIsInTurbo_WithNoOtherProfile_LeavesTheBaseEmpty()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { TurboToggles = true },
            all: [TestProfiles.Turbo],
            current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal("", f.Store.Settings.OnAc.BaseId);
        Assert.True(f.Store.Settings.OnAc.Turbo);
    }

    [Fact]
    public void AnUnreadableCurrentProfile_SeedsNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(current: null);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal("", f.Store.Settings.OnAc.BaseId);
        Assert.Equal(0, f.Store.SaveCount);
    }

    [Fact]
    public void ARepeatedReadingOfTheSameSource_IsANoOp()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Service.SyncPowerSource(OnAc);

        f.Service.SyncPowerSource(OnAc);
        f.Service.SyncPowerSource(OnAc);

        Assert.Equal(1, f.Store.SaveCount);
        Assert.Empty(f.Pp!.SetCalls);
    }

    [Fact]
    public void ReturningToASourceWithARememberedMode_AppliesIt()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "performance" } },
            current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal(["performance"], f.Pp!.SetCallIds);
    }

    /// <summary>The common case — the same mode on both sources — must not write at all: each Acer
    /// profile Set re-flashes the keyboard/lightbar palette.</summary>
    [Fact]
    public void ReturningToASourceAlreadyInItsRememberedMode_WritesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings { OnAc = new ProfileMemory { BaseId = "balanced" } },
            current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnAc);

        Assert.Empty(f.Pp!.SetCalls);
    }

    [Fact]
    public void ReturningToATurboSource_WhenAlreadyInTurbo_WritesNothing()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnAc);

        Assert.Empty(f.Pp!.SetCalls);
    }

    [Fact]
    public void ReturningToATurboSource_FromAnotherProfile_EstablishesTheBaseThenTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Quiet);

        f.Service.SyncPowerSource(OnAc);

        // NOTE: still two EC writes (and two palette flashes) when coming from an unrelated profile —
        // only the already-active cases are short-circuited.
        Assert.Equal(["balanced", "turbo"], f.Pp!.SetCallIds);
    }

    [Fact]
    public void ReturningToATurboSource_WhenTheBaseIsAlreadyActive_WritesOnlyTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            current: TestProfiles.Balanced);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal(["turbo"], f.Pp!.SetCallIds);
    }

    [Fact]
    public void ReturningToATurboSource_WhenTurboIsNotSelectable_AppliesTheBaseInstead()
    {
        var f = LaptopServiceFixture.WithProfiles(
            settings: new Settings
                      {
                          TurboToggles = true,
                          OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
                      },
            selectable: [TestProfiles.Quiet, TestProfiles.Balanced],
            current: TestProfiles.Quiet);

        f.Service.SyncPowerSource(OnAc);

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
    }

    /// <summary>NOTE — PROBABLE BUG, derived from the code rather than observed in the app.
    ///
    /// <c>SeedSlotFromHardware</c> (`LaptopService.Profiles.cs` `SeedSlotFromHardware` — the Turbo branch) records <c>Turbo = true</c> + a GUESSED base
    /// (Balanced) whenever the hardware is in Turbo — without consulting <c>Settings.TurboToggles</c>. With
    /// the switch OFF, <c>ApplyStoredMode</c> ignores the flag (wantTurbo is false) and applies the base it
    /// finds. So: machine booted into Turbo, first reading seeds the slot with the guess, and the NEXT
    /// source change to that same source overwrites the hardware's Turbo with Balanced — silently
    /// changing the user's performance mode. With the flag on, wantTurbo would re-apply Turbo and the
    /// guess is harmless.
    ///
    /// The test pins the behaviour as it is, not as it should be.</summary>
    [Fact]
    public void TurboTogglesOff_ASourceCycleThroughTheSeedDropsTheMachineOutOfTurbo()
    {
        var f = LaptopServiceFixture.WithProfiles(settings: new Settings { TurboToggles = false },
                                                  current: TestProfiles.Turbo);

        f.Service.SyncPowerSource(OnAc);          // seeds the AC slot: BaseId=balanced, Turbo=true (guessed)
        Assert.Equal("balanced", f.Store.Settings.OnAc.BaseId);
        Assert.Empty(f.Pp!.SetCalls);             // the machine is left in Turbo at this point

        f.Service.SyncPowerSource(OnBattery);     // seeds the battery slot too
        Assert.Empty(f.Pp!.SetCalls);

        f.Service.SyncPowerSource(OnAc);          // back to AC: "restore what we remembered"

        Assert.Equal(["balanced"], f.Pp!.SetCallIds);
        Assert.Equal(TestProfiles.Balanced, f.Service.CurrentProfile());   // Turbo is gone
    }
}
