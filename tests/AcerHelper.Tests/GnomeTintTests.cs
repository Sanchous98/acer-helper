using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The GNOME blue-light link: what it writes, what it refuses to touch, and its remember/restore.
///
/// READ THIS BEFORE TRUSTING IT: this link has NEVER RUN against a real GNOME. The owner's session is KDE, where
/// the colour schema is not installed, so the chain's probe declines and nothing here has been near a settings
/// daemon. The key names, their types and their defaults come from gnome-settings-daemon's own schema source
/// (data/org.gnome.settings-daemon.plugins.color.gschema.xml.in); the behaviour below is what its POLICY does,
/// pinned against a fake. It is a documented implementation awaiting a GNOME machine — and because the chain
/// declines it on a KDE box, an untested link can never be the one the owner actually gets.
///
/// NOTHING IN HERE TOUCHES THE OWNER'S REAL SETTINGS: the I/O arrives as delegates and the fake is a dictionary.
///
/// WHAT CANNOT BE PINNED HERE: whether <c>gsettings</c> really prints an unsigned key as <c>uint32 2700</c> — the
/// format <see cref="GnomeConfig.ParseKelvin"/> takes apart, which is what the round-trip fake below imitates. If
/// that imitation is wrong the parser is wrong with it, and the link's read-back check would fail and roll the
/// write back rather than reporting a tint that is not there. That is the failure mode to prefer, and it is why
/// the link verifies instead of trusting.
/// </summary>
public class GnomeTintTests
{
    /// <summary>The write set, in order: the temperature first, then the switch, so the setting is already right
    /// when the night light comes on.</summary>
    [Fact]
    public void ApplyWritesTheTemperatureThenTheSwitch()
    {
        var gnome = new FakeGnome();

        Assert.True(gnome.Port.Apply(2));

        Assert.Equal(
        [
            (GnomeConfig.TemperatureKey, "4000"),
            (GnomeConfig.EnabledKey, "true"),
        ], gnome.Writes);
    }

    [Theory]
    [InlineData(1, "4500")]
    [InlineData(3, "3500")]
    [InlineData(4, "3000")]
    public void EachLevelWritesItsOwnTemperature(int level, string expected)
    {
        var gnome = new FakeGnome();

        gnome.Port.Apply(level);

        Assert.Contains((GnomeConfig.TemperatureKey, expected), gnome.Writes);
        Assert.Equal(int.Parse(expected), gnome.Kelvin(GnomeConfig.TemperatureKey));
    }

    /// <summary>
    /// An enabled night light is left alone, and NOTHING is written on the way to declining. GNOME's schedule is
    /// on by default (<c>night-light-schedule-automatic</c> is true in the schema), so an enabled night light is
    /// normally a scheduled one — the case the owner's rule names explicitly.
    /// </summary>
    [Fact]
    public void TheUsersEnabledNightLightIsNeverTouched()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.EnabledKey] = "true";

        Assert.False(gnome.Port.Apply(3));

        Assert.Empty(gnome.Writes);
    }

    /// <summary>Off on a device that never tinted touches nothing.</summary>
    [Fact]
    public void OffOnAFreshPortTouchesNothing()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";

        Assert.True(gnome.Port.Apply(0));

        Assert.Empty(gnome.Writes);
        Assert.Equal(2700, gnome.Kelvin(GnomeConfig.TemperatureKey));
    }

    /// <summary>
    /// The round trip, and the one that matters most here: GNOME's own default night temperature is 2700 K, which
    /// is WARMER than anything this app offers. Leaving our 3000 behind would have silently raised a preference
    /// the user had set warmer than the app can go.
    /// </summary>
    [Fact]
    public void TurningTheFilterOffPutsTheUsersTemperatureBack()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";

        gnome.Port.Apply(4);
        Assert.Equal(3000, gnome.Kelvin(GnomeConfig.TemperatureKey));      // ours is in place

        gnome.Port.Apply(0);

        Assert.Equal(2700, gnome.Kelvin(GnomeConfig.TemperatureKey));
        Assert.False(GnomeConfig.IsOn(gnome.Value(GnomeConfig.EnabledKey)));
    }

    /// <summary>A second apply must not re-record: after the first, the value in the settings is ours, and
    /// remembering it would leave our own tint behind as if it were the user's.</summary>
    [Fact]
    public void ASecondApplyKeepsTheTemperatureTheFirstOneRecorded()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";

        gnome.Port.Apply(2);
        gnome.Port.Apply(4);
        Assert.Equal(3000, gnome.Kelvin(GnomeConfig.TemperatureKey));

        gnome.Port.Apply(0);
        Assert.Equal(2700, gnome.Kelvin(GnomeConfig.TemperatureKey));
    }

    /// <summary>The app closing is the same duty as switching the filter off.</summary>
    [Fact]
    public void DisposePutsTheUsersTemperatureBack()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";

        gnome.Port.Apply(3);
        gnome.Port.Dispose();

        Assert.Equal(2700, gnome.Kelvin(GnomeConfig.TemperatureKey));
        Assert.False(GnomeConfig.IsOn(gnome.Value(GnomeConfig.EnabledKey)));
    }

    /// <summary>Whether the app's tint is in the settings, for the same reason the KWin link exposes it: a test
    /// has to tell "cleaned up" from "written and forgotten".</summary>
    [Fact]
    public void IsWrittenFollowsTheSnapshot()
    {
        var gnome = new FakeGnome();

        Assert.False(gnome.Port.IsWritten);
        gnome.Port.Apply(2);
        Assert.True(gnome.Port.IsWritten);
        gnome.Port.Apply(0);
        Assert.False(gnome.Port.IsWritten);
    }

    // ---- the user can take it back at any time, and wins ----

    /// <summary>The user changes the night temperature from GNOME's own panel while the filter is on. Their value
    /// survives — the app forfeits and, crucially, does NOT write the value it remembered over their new one.</summary>
    [Fact]
    public void AUserChangeWhileTintingWinsAndTheAppForfeits()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";
        gnome.Port.Apply(2);

        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 3300";     // their edit, not ours

        Assert.False(gnome.Port.Apply(4));

        Assert.Equal(3300, gnome.Kelvin(GnomeConfig.TemperatureKey));
        Assert.False(gnome.Port.IsWritten);
    }

    /// <summary>And when what they did was switch the night light off, it stays off — the next click on the app's
    /// row must not switch it back on.</summary>
    [Fact]
    public void AUserWhoSwitchesTheNightLightOffIsNotOverridden()
    {
        var gnome = new FakeGnome();
        gnome.Port.Apply(2);

        gnome.Config[GnomeConfig.EnabledKey] = "false";               // their edit

        Assert.False(gnome.Port.Apply(4));

        Assert.False(GnomeConfig.IsOn(gnome.Value(GnomeConfig.EnabledKey)));
        Assert.False(gnome.Port.IsWritten);
    }

    /// <summary>
    /// AND THE SAME RULE APPLIES WHEN THE APP QUITS, which is where it was missing. The release used to write the
    /// two keys unconditionally: <c>night-light-enabled=false</c> — on the reasoning that the app only ever
    /// applied while it was off — and the temperature it had recalled from before the app started. Both are the
    /// user's own keys, and GNOME's panel writes them live, so with the filter on and the user having just changed
    /// the night temperature, quitting wrote the OLD value back over theirs; had they switched the night light off
    /// instead, it was switched back on. The quit path now asks the same question an apply asks ("do the settings
    /// still hold what this run wrote?", StillOurs) and, when the answer is no, forgets the tint and writes
    /// NOTHING: what they are left with is their own night light at their own temperature.
    ///
    /// MUTATION that reddens it: remove the <c>!StillOurs()</c> guard from <c>Release</c> — the recalled 2700 is
    /// then written back over their 3300.
    /// </summary>
    [Fact]
    public void AUserChangeWhileTintingSurvivesTheAppQuitting()
    {
        var gnome = new FakeGnome();
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";
        gnome.Port.Apply(2);                                  // ours: 4000 and enabled=true

        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 3300";   // their edit in GNOME's panel

        gnome.Port.Dispose();                                 // ...and the user quits

        Assert.Equal(3300, gnome.Kelvin(GnomeConfig.TemperatureKey));
        Assert.False(gnome.Port.IsWritten);
    }

    // ---- the write is verified, not trusted ----

    /// <summary>The settings daemon does not act on the write. The apply reports failure and rolls back.</summary>
    [Fact]
    public void AWriteTheDaemonIgnoresIsRolledBackAndReported()
    {
        var gnome = new FakeGnome { DaemonHonoursWrites = false };
        gnome.Config[GnomeConfig.TemperatureKey] = "uint32 2700";

        Assert.False(gnome.Port.Apply(2));

        Assert.Equal(2700, gnome.Kelvin(GnomeConfig.TemperatureKey));
        Assert.False(gnome.Port.IsWritten);
    }

    [Fact]
    public void SuccessFollowsTheDaemonsOwnAnswer()
    {
        Assert.True(new FakeGnome().Port.Apply(2));
        Assert.False(new FakeGnome { DaemonHonoursWrites = false }.Port.Apply(2));
    }

    // ---- the one piece of parsing this link owns ----

    /// <summary>An unsigned key is printed WITH its type, so the integer has to be picked out of
    /// <c>uint32 2700</c>. UNVERIFIED — the format is what gsettings is documented to print, and no GNOME is
    /// installed here to observe it on.</summary>
    [Theory]
    [InlineData("uint32 2700", 2700)]
    [InlineData("uint32 4000", 4000)]
    [InlineData("2700", 2700)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    public void ParseKelvinReadsWhatGsettingsPrints(string? printed, int? expected)
        => Assert.Equal(expected, GnomeConfig.ParseKelvin(printed));

    // ---- the harness ----

    /// <summary>A settings store, and a settings daemon that reads it. It writes a <c>u</c> key the way
    /// <c>gsettings</c> would — storing it and printing it back with its type — so the link's own parser is
    /// genuinely exercised by the round trip rather than bypassed.
    /// </summary>
    private sealed class FakeGnome
    {
        public FakeGnome() => Port = new GnomeConfigTint(Read, Write, Commit);

        /// <summary>ONE port for the whole test: the adapter remembers the user's value in itself, so a fresh
        /// instance per access would silently lose it and make every "did it restore?" question meaningless.</summary>
        public GnomeConfigTint Port { get; }

        public Dictionary<string, string> Config { get; } = new(StringComparer.Ordinal);
        public List<(string Key, string Value)> Writes { get; } = [];

        /// <summary>False models the settings daemon not acting on what was written: the write is still ATTEMPTED
        /// and recorded, it just does not land, which is the case the adapter's read-back must catch.</summary>
        public bool DaemonHonoursWrites { get; set; } = true;

        /// <summary>The value as <c>gsettings get</c> would print it.</summary>
        public string? Value(string key) => Config.TryGetValue(key, out var v) ? v : null;

        /// <summary>The temperature as the adapter would read it back — through its own parser, so the round trip
        /// exercises the <c>uint32</c> form rather than bypassing it.</summary>
        public int? Kelvin(string key) => GnomeConfig.ParseKelvin(Value(key));

        private string? Read(string key) => Value(key);

        private void Write(string key, string value)
        {
            Writes.Add((key, value));
            if (!DaemonHonoursWrites) return;
            Config[key] = key == GnomeConfig.TemperatureKey ? $"uint32 {value}" : value;
        }

        private void Commit()
        {
        }
    }
}
