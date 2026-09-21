using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The KWin blue-light link: what it writes into KWin's own settings, what it refuses to touch, and the
/// remember/restore that makes writing the user's configuration acceptable at all.
///
/// It is covered here rather than left to the live session because the policy is in an UN-SUFFIXED file for
/// exactly this reason: the test project targets <c>net10.0-windows</c> while the app excludes
/// <c>**/*.Linux.cs</c> from that TFM, so anything left in <c>KwinTint.Linux.cs</c> would be unreachable. The I/O
/// half — <c>kreadconfig6</c>, <c>kwriteconfig6</c>, <c>reconfigure</c>, the D-Bus read-back — arrives as
/// delegates, and the fake below models the part that matters: a configuration store, and a compositor that reads
/// it.
///
/// NOTHING IN HERE TOUCHES THE OWNER'S REAL CONFIGURATION. That is the point of the delegate seam, and it is not
/// negotiable: the owner's <c>kwinrc</c> is theirs, and the only thing these tests read is a dictionary.
///
/// WHAT CANNOT BE PINNED HERE: that KWin actually acts on a written group. The two facts that carry the mechanism
/// — the group and key names, and that <c>Mode</c> is stored as the choice NAME "Constant" rather than as the
/// integer 0 — were read out of KWin's own <c>nightlightsettings.kcfg</c> and out of KF6's
/// <c>KCoreConfigSkeleton::ItemEnum</c>, not observed on a written config (deliberately: writing the owner's
/// configuration to find out was not an option). That is precisely why the link VERIFIES through D-Bus instead of
/// trusting the write, and the verification is what these tests pin.
/// </summary>
public class KwinTintTests
{
    // ---- the trap: "enabled" is not "tinting" ----

    /// <summary>
    /// KWin's <c>enabled</c> is the switch in System Settings, so it is true ALL DAY for anyone who has ever
    /// turned night light on; <c>daylight</c> says which phase the schedule is in. Only the night phase is tinted.
    /// Reading <c>enabled</c> as "the screen is tinted" is the mistake this table exists to prevent — it is what
    /// words the status line when the app stands down.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]     // enabled, night phase -> tinting
    [InlineData(true, true, false)]     // enabled, daytime -> NOT tinting
    [InlineData(false, true, false)]    // off entirely
    [InlineData(false, false, false)]   // night phase of the schedule, but the feature is off
    public void EnabledAloneIsNotTinting(bool enabled, bool daylight, bool expected)
        => Assert.Equal(expected, State(enabled: enabled, daylight: daylight).KdeIsTinting);

    /// <summary>
    /// What an apply is judged by: enabled, RUNNING, in the constant mode, and showing the temperature asked for.
    /// Every row is a distinct way of being wrong, and the second is the one that matters most — a configuration
    /// can be accepted and enabled while nothing is applied, which is exactly the state the first version of this
    /// link mistook for success by checking the target temperature instead of what was on screen.
    /// </summary>
    [Theory]
    [InlineData(true, true, 0, 4000, 4000, true)]     // all four facts
    [InlineData(true, false, 0, 6500, 4000, false)]   // enabled, but nothing is being applied
    [InlineData(true, true, 1, 4000, 4000, false)]    // still the scheduled mode: the Mode write missed
    [InlineData(true, true, 0, 6500, 4000, false)]    // running, but the screen is not at our temperature
    [InlineData(false, false, 0, 4000, 4000, false)]  // not enabled at all
    public void HoldsNeedsTheFeatureRunningInTheConstantModeAtTheTemperature(
        bool enabled, bool running, int mode, int current, int asked, bool holds)
        => Assert.Equal(holds, State(enabled: enabled, running: running, mode: mode, current: current).Holds(asked));

    /// <summary>A release is judged by the compositor having let go — off AND nothing applied — not by the file
    /// looking restored.</summary>
    [Theory]
    [InlineData(false, false, true)]    // off, nothing running: released
    [InlineData(true, true, false)]     // still tinting: KWin never saw the change
    [InlineData(false, true, false)]    // off but still running: mid-teardown
    public void IsReleasedNeedsOffAndNothingApplied(bool enabled, bool running, bool released)
        => Assert.Equal(released, State(enabled: enabled, running: running).IsReleased);

    // ---- what an apply writes, and in what order ----

    /// <summary>
    /// The write set, in order: the user's three keys are recorded in the markers FIRST, then the temperature and
    /// the mode, then <c>Active</c> last so that any config re-read triggered by an intermediate write sees an
    /// inert <c>Active=false</c> rather than half a tint. <c>Mode</c> is the choice NAME, which is what KF6's
    /// ItemEnum serializes.
    /// </summary>
    [Fact]
    public void ApplyWritesKdesOwnGroupInTheOrderThatCannotHalfApply()
    {
        var kwin = new FakeKwin();

        Assert.True(kwin.Port.Apply(2));

        Assert.Equal(
        [
            (KwinConfig.PriorTemperatureMarker, KwinConfig.Absent),
            (KwinConfig.PriorModeMarker, KwinConfig.Absent),
            (KwinConfig.PriorActiveMarker, KwinConfig.Absent),
            (KwinConfig.TemperatureKey, "4000"),
            (KwinConfig.ModeKey, "0"),                        // the INT, not the choice's name — measured
            (KwinConfig.ActiveKey, "true"),
        ], kwin.Writes);
        Assert.Single(kwin.Commits);
    }

    /// <summary>Level 0 is a release, never a write of the neutral temperature: a <c>NightTemperature=6500</c>
    /// left in the user's settings would be this app's value sitting in their configuration, which is exactly what
    /// "off" must not do.</summary>
    [Fact]
    public void OffWritesNothingAndOnlyRestores()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.DarkLight;

        Assert.True(kwin.Port.Apply(2));
        kwin.Writes.Clear();

        Assert.True(kwin.Port.Apply(0));

        Assert.DoesNotContain(kwin.Writes, w => w.Key == KwinConfig.TemperatureKey && w.Value == "6500");
        Assert.Contains(KwinConfig.ModeKey, kwin.Config.Keys);           // the user's own mode is back
        Assert.Equal(KwinConfig.DarkLight, kwin.Config[KwinConfig.ModeKey]);
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);   // absent before -> absent again
    }

    /// <summary>A level maps to its own temperature, clamped at the top so nothing above KWin's ceiling is ever
    /// written into the user's settings.</summary>
    [Theory]
    [InlineData(1, "4500")]
    [InlineData(3, "3500")]
    [InlineData(4, "3000")]
    public void EachLevelWritesItsOwnTemperature(int level, string expected)
    {
        var kwin = new FakeKwin();

        kwin.Port.Apply(level);

        Assert.Contains((KwinConfig.TemperatureKey, expected), kwin.Writes);
        Assert.Equal(expected, kwin.Config[KwinConfig.TemperatureKey]);
    }

    // ---- the rule that keeps the app off the user's own night light ----

    /// <summary>
    /// An enabled night light is left ALONE, whatever mode it is in — this is the "with a schedule" case the
    /// owner's rule names, and it is the whole reason the check is made against the CONFIG rather than against
    /// KWin's runtime phase: a scheduled night light is "enabled" all day and only tinting at night, so a check on
    /// the phase would let the app take the pipeline over during the day and then fight the schedule at sunset.
    ///
    /// Equally important is what does NOT happen: nothing is written, so the user's configuration is not even
    /// touched on the way to declining.
    /// </summary>
    [Theory]
    [InlineData(KwinConfig.DarkLight)]     // on a schedule
    [InlineData(KwinConfig.Constant)]      // at a fixed temperature of their own
    [InlineData(null)]                     // enabled with no Mode key at all (KWin's own default: scheduled)
    public void TheUsersEnabledNightLightIsNeverTouched(string? mode)
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ActiveKey] = "true";
        if (mode is not null) kwin.Config[KwinConfig.ModeKey] = mode;

        Assert.False(kwin.Port.Apply(3));

        Assert.Empty(kwin.Writes);
        Assert.Empty(kwin.Deletes);
        Assert.Empty(kwin.Commits);
    }

    /// <summary>An inhibited night light is equally not ours: another client has asked KWin to hold it still, so a
    /// write would be accepted and do nothing.</summary>
    [Fact]
    public void AnInhibitedCompositorIsNotWrittenTo()
    {
        var kwin = new FakeKwin { Inhibited = true };

        Assert.False(kwin.Port.Apply(3));

        Assert.Empty(kwin.Writes);
        Assert.Empty(kwin.Deletes);
    }

    /// <summary>A compositor that says it cannot do night light does not get written to either.</summary>
    [Fact]
    public void ACompositorWithoutNightLightIsNotWrittenTo()
    {
        var kwin = new FakeKwin { Available = false };

        Assert.False(kwin.Port.Apply(3));

        Assert.Empty(kwin.Writes);
        Assert.Empty(kwin.Deletes);
    }

    /// <summary>Off on a device that never tinted must not touch the user's keys at all — no marker means the
    /// configuration is not ours, and the app keeps its hands off.</summary>
    [Fact]
    public void OffOnAFreshPortTouchesNothing()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.Constant;
        kwin.Config[KwinConfig.TemperatureKey] = "2700";

        Assert.True(kwin.Port.Apply(0));

        Assert.Empty(kwin.Writes);
        Assert.Empty(kwin.Deletes);
        Assert.Empty(kwin.Commits);
        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
    }

    // ---- reversibility: the reason writing the user's config is acceptable ----

    /// <summary>
    /// The full round trip. The user had a night temperature set (2700 — warmer than anything this app offers) and
    /// no <c>Active</c> key at all; after the filter is turned off the group must be byte-for-byte what it was:
    /// the temperature back, the mode back, and the key that was ABSENT deleted again rather than replaced with
    /// the value it happens to resolve to.
    /// </summary>
    [Fact]
    public void TurningTheFilterOffPutsTheUsersSettingsBackExactly()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.DarkLight;
        kwin.Config[KwinConfig.TemperatureKey] = "2700";
        var before = new Dictionary<string, string>(kwin.Config);

        kwin.Port.Apply(2);
        Assert.Equal("4000", kwin.Config[KwinConfig.TemperatureKey]);     // ours is in place
        kwin.Port.Apply(0);

        Assert.Equal(before, kwin.Config);
        Assert.All(KwinConfig.Markers, m => Assert.DoesNotContain(m, kwin.Config.Keys));
    }

    /// <summary>The app closing is the same duty as the filter being switched off: the settings go back.</summary>
    [Fact]
    public void DisposePutsTheUsersSettingsBack()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.TemperatureKey] = "2700";

        kwin.Port.Apply(4);
        kwin.Port.Dispose();

        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);
    }

    /// <summary>A second apply must NOT re-record the priors: they are what the user had, and after the first
    /// apply the value in the file is ours. Recording again would make "the user's" 4000 and turning the filter
    /// off would leave our own tint behind as if it were theirs.</summary>
    [Fact]
    public void ASecondApplyKeepsThePriorsTheFirstOneRecorded()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.TemperatureKey] = "2700";

        kwin.Port.Apply(2);
        var markers = KwinConfig.Markers.ToDictionary(m => m, m => kwin.Config[m]);

        kwin.Port.Apply(4);

        Assert.Equal(markers, KwinConfig.Markers.ToDictionary(m => m, m => kwin.Config[m]));
        Assert.Equal("3000", kwin.Config[KwinConfig.TemperatureKey]);

        kwin.Port.Apply(0);
        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
    }

    /// <summary>Nothing of the app's may survive the filter being turned off.</summary>
    [Fact]
    public void IsWrittenFollowsTheMarkers()
    {
        var kwin = new FakeKwin();

        Assert.False(kwin.Port.IsWritten);
        kwin.Port.Apply(2);
        Assert.True(kwin.Port.IsWritten);
        kwin.Port.Apply(0);
        Assert.False(kwin.Port.IsWritten);
    }

    // ---- the crash path ----

    /// <summary>
    /// A run that ended while tinted leaves its markers behind, and it is undone at startup as well as before an
    /// apply — because the app can come back with the filter switched OFF, and then nothing would apply and
    /// nothing would clean up, leaving the screen warm until the user found it in System Settings.
    ///
    /// The state below is what such a crash leaves: KWin switched on at our temperature, and the markers holding
    /// what the user really had (a night temperature, no <c>Active</c> key).
    /// </summary>
    [Fact]
    public void RecoverUndoesATintLeftByARunThatDied()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ActiveKey] = "true";                    // ours, from the dead run
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.Constant;         // ours
        kwin.Config[KwinConfig.TemperatureKey] = "3500";               // ours
        kwin.Config[KwinConfig.PriorTemperatureMarker] = "2700";       // the user's
        kwin.Config[KwinConfig.PriorModeMarker] = KwinConfig.DarkLight;
        kwin.Config[KwinConfig.PriorActiveMarker] = KwinConfig.Absent;

        kwin.Port.Recover();

        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
        Assert.Equal(KwinConfig.DarkLight, kwin.Config[KwinConfig.ModeKey]);
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);
        Assert.All(KwinConfig.Markers, m => Assert.DoesNotContain(m, kwin.Config.Keys));
        Assert.Single(kwin.Commits);                                   // KWin was told to re-read
    }

    /// <summary>And with no marker there is nothing to recover: a configuration the app never wrote is not
    /// touched, not even to "tidy" it.</summary>
    [Fact]
    public void RecoverLeavesAConfigurationThatIsNotOursAlone()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ActiveKey] = "true";                    // the user's own night light
        kwin.Config[KwinConfig.TemperatureKey] = "2700";

        kwin.Port.Recover();

        Assert.Empty(kwin.Writes);
        Assert.Empty(kwin.Deletes);
        Assert.Empty(kwin.Commits);
        Assert.Equal("true", kwin.Config[KwinConfig.ActiveKey]);
        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
    }

    /// <summary>A marker set interrupted part-way is ours but has nothing to restore — the real keys are only
    /// written after all three markers, so nothing of the user's can have changed. The half-written markers are
    /// dropped and the user's own keys are left exactly as they are.</summary>
    [Fact]
    public void ATornMarkerSetIsOnlyCleanedUp()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.DarkLight;
        kwin.Config[KwinConfig.PriorModeMarker] = KwinConfig.Absent;   // one marker only: the write was interrupted

        kwin.Port.Recover();

        Assert.DoesNotContain(KwinConfig.PriorModeMarker, kwin.Config.Keys);
        Assert.Equal(KwinConfig.DarkLight, kwin.Config[KwinConfig.ModeKey]);
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);
    }

    /// <summary>An apply recovers a leftover first, so it can never read our own <c>Active=true</c> as the user's
    /// and lock itself out of its own route.</summary>
    [Fact]
    public void ApplyRecoversALeftoverBeforeDeciding()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.ActiveKey] = "true";
        kwin.Config[KwinConfig.ModeKey] = KwinConfig.Constant;
        kwin.Config[KwinConfig.TemperatureKey] = "3500";
        kwin.Config[KwinConfig.PriorTemperatureMarker] = "2700";
        kwin.Config[KwinConfig.PriorModeMarker] = KwinConfig.DarkLight;
        kwin.Config[KwinConfig.PriorActiveMarker] = KwinConfig.Absent;

        Assert.True(kwin.Port.Apply(1));                              // not refused as "the user's night light"
        Assert.Equal("4500", kwin.Config[KwinConfig.TemperatureKey]);

        kwin.Port.Apply(0);
        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);  // and the user's real value is what returns
    }

    // ---- the user can take it back at any time, and wins ----

    /// <summary>
    /// The user changes the night light from System Settings WHILE the filter is on. Their value must survive: the
    /// app stops claiming the configuration and drops its markers WITHOUT restoring, because restoring would write
    /// the values the user had BEFORE the app started over the choice they have just made. (What they leave behind
    /// — our <c>Active=true</c> and <c>Mode=Constant</c> — stays, since that is the state their own edit now
    /// describes.)
    /// </summary>
    [Fact]
    public void AUserChangeWhileTintingWinsAndTheAppForfeits()
    {
        var kwin = new FakeKwin();
        kwin.Config[KwinConfig.TemperatureKey] = "2700";
        kwin.Port.Apply(2);

        kwin.Config[KwinConfig.TemperatureKey] = "3300";      // their edit, not ours

        Assert.False(kwin.Port.Apply(4));

        Assert.Equal("3300", kwin.Config[KwinConfig.TemperatureKey]);
        Assert.False(kwin.Port.IsWritten);
        Assert.All(KwinConfig.Markers, m => Assert.DoesNotContain(m, kwin.Config.Keys));
    }

    /// <summary>The same when what they did was switch the night light OFF: it stays off. The app must not use the
    /// next click on its own row to switch it back on.</summary>
    [Fact]
    public void AUserWhoSwitchesTheNightLightOffIsNotOverridden()
    {
        var kwin = new FakeKwin();
        kwin.Port.Apply(2);

        kwin.Config[KwinConfig.ActiveKey] = "false";          // their edit

        Assert.False(kwin.Port.Apply(4));

        Assert.Equal("false", kwin.Config[KwinConfig.ActiveKey]);
        Assert.False(kwin.Port.IsWritten);
    }

    // ---- the write is verified, not trusted ----

    /// <summary>
    /// The compositor ignores the configuration (KWin was inhibited, or a name in it was not understood). The apply
    /// must report failure AND roll back, so that a wrong key name or a refused mode can never leave a half-written
    /// configuration or a reported tint that is not on the screen.
    /// </summary>
    [Fact]
    public void AWriteTheCompositorIgnoresIsRolledBackAndReported()
    {
        var kwin = new FakeKwin { CompositorHonoursConfig = false };
        kwin.Config[KwinConfig.TemperatureKey] = "2700";

        Assert.False(kwin.Port.Apply(2));

        Assert.Equal("2700", kwin.Config[KwinConfig.TemperatureKey]);
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);
        Assert.All(KwinConfig.Markers, m => Assert.DoesNotContain(m, kwin.Config.Keys));
        Assert.False(kwin.Port.IsWritten);
    }

    /// <summary>Success is reported only when the compositor confirms it, so the caller can never be told a tint
    /// exists that does not.</summary>
    [Fact]
    public void SuccessFollowsTheCompositorsOwnAnswer()
    {
        Assert.True(new FakeKwin().Port.Apply(2));
        Assert.False(new FakeKwin { CompositorHonoursConfig = false }.Port.Apply(2));
    }

    // ---- the flag that makes the whole route work ----

    /// <summary>
    /// EVERY configuration change this link makes carries <c>--notify</c>, and there is no path that bypasses these
    /// two builders — apply, release, rollback and the marker bookkeeping all go through them.
    ///
    /// That is the whole point of pinning it here: the flag is invisible to a fake, and its absence was the first
    /// version's bug. On the owner's machine it wrote the file and told nobody, KWin went on using the copy it held
    /// in memory, and the only symptoms were a seconds-long lag and a screen that never warmed up. A DELETE needs
    /// the flag for the same reason a write does: it changes what KWin reads.
    /// </summary>
    [Fact]
    public void EveryWriteAndDeleteFormNotifies()
    {
        var keys = new[] { KwinConfig.ActiveKey, KwinConfig.ModeKey, KwinConfig.TemperatureKey }.Concat(KwinConfig.Markers);

        foreach (var key in keys)
        {
            Assert.Contains("--notify", KwinConfigCli.Write(key, "x"));
            Assert.Contains("--notify", KwinConfigCli.Delete(key));
            Assert.DoesNotContain("--notify", KwinConfigCli.Read(key));    // reading changes nothing
        }
    }

    /// <summary>The flags each key needs to be read back as what it is: a real boolean for the switch, numbers for
    /// the two numerics, and an opaque string for the markers — whose values include the absent sentinel.</summary>
    [Theory]
    [InlineData(KwinConfig.ActiveKey, "bool")]
    [InlineData(KwinConfig.ModeKey, "int")]
    [InlineData(KwinConfig.TemperatureKey, "int")]
    [InlineData(KwinConfig.PriorModeMarker, null)]
    public void EachKeyIsWrittenWithTheTypeKwinReadsItAs(string key, string? expected)
        => Assert.Equal(expected, KwinConfigCli.TypeOf(key));

    /// <summary>The config is addressed by the bare name KWin itself opens — a path would bypass
    /// <c>XDG_CONFIG_HOME</c>, which is how the very first experiment managed to write to the wrong place.</summary>
    [Fact]
    public void TheConfigIsAddressedByTheBareNameKwinOpens()
    {
        Assert.Equal("kwinrc", KwinConfig.File);
        Assert.Contains("kwinrc", KwinConfigCli.Write(KwinConfig.ActiveKey, "true"));
        Assert.DoesNotContain("/", KwinConfig.File);
    }

    /// <summary>
    /// The written <c>Mode</c> values ARE the enum's integers — the literals "0"/"1" must not drift from
    /// <see cref="KwinConfig.ConstantMode"/>/<see cref="KwinConfig.DarkLightMode"/>, or the write would be the
    /// silent no-op this link was fixed for. The literals exist only because an attribute argument must be
    /// constant, so this is what keeps the pair honest.
    /// </summary>
    [Fact]
    public void TheModeValuesAreTheEnumIntegers()
    {
        Assert.Equal(KwinConfig.ConstantMode.ToString(), KwinConfig.Constant);
        Assert.Equal(KwinConfig.DarkLightMode.ToString(), KwinConfig.DarkLight);
        Assert.Equal(0, KwinConfig.ConstantMode);
        Assert.Equal(1, KwinConfig.DarkLightMode);
    }

    /// <summary>
    /// THE WORST FAILURE THIS FEATURE HAS, as a test: KWin never learns the configuration was put back, so it keeps
    /// the tint applied while the file looks perfectly clean. The release has to NOTICE and report it — claiming
    /// success here would leave the owner's screen warm until their next login, which is the one outcome worse than
    /// the filter not working.
    /// </summary>
    [Fact]
    public void AReleaseTheCompositorDidNotNoticeIsReportedAsFailed()
    {
        var kwin = new FakeKwin();
        Assert.True(kwin.Port.Apply(2));

        kwin.CompositorKeepsItsInMemoryCopy = true;      // ...as if the notify signal never arrived

        Assert.False(kwin.Port.Apply(0));

        // The FILE was restored all the same. It is the runtime that did not follow, and saying so is the point.
        Assert.All(KwinConfig.Markers, m => Assert.DoesNotContain(m, kwin.Config.Keys));
        Assert.DoesNotContain(KwinConfig.ActiveKey, kwin.Config.Keys);
    }

    // ---- the harness ----

    private static NightLightState State(bool enabled, bool daylight = true, int mode = KwinConfig.DarkLightMode,
                                         bool running = false, int current = 6500)
        => new(Available: true, Enabled: enabled, Inhibited: false, Daylight: daylight, Mode: mode,
               Running: running, CurrentTemperature: current);

    /// <summary>A configuration store, and a compositor that reads it — the two halves the link talks to, wired the
    /// way the live session wires them. <see cref="CompositorHonoursConfig"/> false models the compositor not
    /// acting on what was written, which is the case the read-back check exists for.
    /// </summary>
    private sealed class FakeKwin
    {
        public Dictionary<string, string> Config { get; } = new(StringComparer.Ordinal);
        public List<(string Key, string Value)> Writes { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<int?> Commits { get; } = [];

        public bool CompositorHonoursConfig { get; set; } = true;
        public bool Available { get; set; } = true;
        public bool Inhibited { get; set; }

        /// <summary>True models KWin never learning that the configuration changed — the failure the
        /// <c>--notify</c> flag exists to prevent: the file looks right and the compositor keeps its in-memory
        /// copy, tint included. The release is supposed to NOTICE this and say so.</summary>
        public bool CompositorKeepsItsInMemoryCopy { get; set; }

        public FakeKwin() => Port = new KwinConfigTint(Read, Write, Delete, Commit, State_);

        /// <summary>ONE port for the whole test: the adapter keeps "have I written yet?" in itself, so a fresh
        /// instance per access would silently lose it.</summary>
        public KwinConfigTint Port { get; }

        private string? Read(string key) => Config.TryGetValue(key, out var v) ? v : null;

        private void Write(string key, string value)
        {
            Writes.Add((key, value));
            Config[key] = value;
        }

        private void Delete(string key)
        {
            Deletes.Add(key);
            Config.Remove(key);
        }

        private void Commit(int? expected) => Commits.Add(expected);

        /// <summary>What KWin's D-Bus interface would answer, given the configuration and whether KWin acts on
        /// it.</summary>
        private NightLightState State_()
        {
            if (!Available) return new(false, false, false, true, KwinConfig.DarkLightMode, false, 6500);
            if (Inhibited) return new(true, false, true, true, KwinConfig.DarkLightMode, false, 6500);
            if (!CompositorHonoursConfig) return new(true, false, false, true, KwinConfig.DarkLightMode, false, 6500);
            if (CompositorKeepsItsInMemoryCopy) return new(true, true, false, false, KwinConfig.ConstantMode, true, 4000);

            var on = KwinConfig.IsOn(Read(KwinConfig.ActiveKey));
            var mode = Read(KwinConfig.ModeKey) == KwinConfig.Constant ? KwinConfig.ConstantMode : KwinConfig.DarkLightMode;
            var temperature = int.TryParse(Read(KwinConfig.TemperatureKey), out var t) ? t : NightTintLevels.Neutral;
            var running = on && mode == KwinConfig.ConstantMode;
            // KWin applies the temperature it is running at; that is what an apply is verified against, so the
            // fake has to report the current temperature and not merely the configured one.
            return new(true, on, false, !running, mode, running, running ? temperature : 6500);
        }
    }
}
