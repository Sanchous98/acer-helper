using System.Globalization;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Blue-light reduction on KWin by writing KWin's OWN night-light settings — the silent, persistent, reversible
// route, and the one KDE's settings dialog uses. The policy lives in this UN-SUFFIXED file deliberately, for the
// reason AcerProfilePorts.cs and DisplayTintChain.cs state at length: the test project targets net10.0-windows
// while AcerHelper.csproj excludes **/*.Linux.cs from that TFM, so policy left in KwinTint.Linux.cs cannot be
// reached by the suite at all. What stays there is the I/O — kreadconfig6/kwriteconfig6, the reconfigure call, the
// session probe — and it arrives here as delegates.
//
// WHY NOT THE D-BUS preview()/stopPreview() ROUTE. It was measured end to end and it works, exactly: preview(T)
// applies precisely T, ramps at ~1745 K/s, and stopPreview() releases in <0.07 s. But a preview is bounded to 15 s
// by the compositor (nightlightmanager.cpp: m_previewTimer->start(15000)), and every preview() call also makes
// KWin queue showText to org.kde.plasmashell's OSD service (measured on the bus: two calls, two osdText). A popup
// every few seconds is not parity with the Win32 gamma ramp, which is silent. Hence the config route; the numbers
// are kept in docs/acer-linux.md as the reason.
//
// THE TWO THINGS THAT MAKE THIS ROUTE ACCEPTABLE: it is REVERSIBLE (the app records the user's own settings in the
// config, see the markers below, and puts them back; the exception is the user changing those keys from System
// Settings while the filter is on, in which case their setting wins — see Release and Forfeit), and it DOES NOT
// FIGHT KDE: the check is made against the CONFIG, not against KWin's runtime `enabled`/`daylight`, because a
// scheduled night light is "enabled" all day and only tinting at night. (NightLightState.KdeIsTinting is that
// runtime phase, kept for wording a message; it is deliberately NOT the gate.)

/// <summary>
/// KWin's night-light state as the D-Bus interface reports it. Used to check the compositor is ours to colour
/// before writing, and to VERIFY what it did afterwards.
///
/// <paramref name="Enabled"/> AND <paramref name="Daylight"/> TOGETHER are the trap: <c>Enabled</c> is KWin's
/// <c>m_active</c>, TRUE all day for anyone who has ever turned night light on, and does not mean the screen is
/// tinted. <see cref="KdeIsTinting"/> is the actual phase.
/// </summary>
/// <param name="Available">KWin's own <c>available</c>: whether this compositor can do night light at all.</param>
/// <param name="Inhibited">Some client (a video player, typically) has asked KWin to hold its night light still, so
/// a write would be accepted and do nothing.</param>
/// <param name="Mode">KWin's <c>mode</c>: <see cref="KwinConfig.ConstantMode"/> = the constant-temperature choice,
/// <see cref="KwinConfig.DarkLightMode"/> = follow the time of day.</param>
/// <param name="Running">KWin's <c>running</c> — whether it is actually applying a temperature: a configuration can
/// be accepted and <c>enabled</c> set while nothing is applied, and then the screen stays cold.</param>
/// <param name="CurrentTemperature">KWin's <c>currentTemperature</c> — the temperature it says it is showing NOW.
/// Used instead of <c>targetTemperature</c>, which is a statement of intent and can be set with nothing following
/// it — how the first version of this link reported success on a screen it had not changed.</param>
internal readonly record struct NightLightState(bool Available, bool Enabled, bool Inhibited, bool Daylight,
                                                int Mode, bool Running, int CurrentTemperature)
{
    /// <summary>The night light is in its tinting phase right now. The app's OWN tint satisfies this too, which is
    /// why the decision to stand down is taken against the configuration and its markers — see the file header.</summary>
    internal bool KdeIsTinting => Enabled && !Daylight;

    /// <summary>
    /// Whether the compositor is now showing exactly the temperature this app asked for — the verification an apply
    /// is judged by. ALL FOUR FACTS: the feature on, actually RUNNING, in the mode that keeps it constant (a write
    /// that missed would leave the scheduled default and only tint at night), and the CURRENT temperature equal to
    /// the request rather than merely the target.
    /// </summary>
    internal bool Holds(int temperature)
        => Enabled && Running && Mode == KwinConfig.ConstantMode && CurrentTemperature == temperature;

    /// <summary>Whether the night light is off and nothing is being applied — what a release is verified
    /// against.</summary>
    internal bool IsReleased => !Enabled && !Running;
}

/// <summary>
/// Every name this link writes, in one place, because each one is a fact read out of KWin's own source.
///
/// <c>NightColor</c> is KWin's group name for the feature (nightlightsettings.kcfg), and
/// <c>Active</c>/<c>Mode</c>/<c>NightTemperature</c> are its three entries. <c>NightTemperature</c> is an Int,
/// default <c>DEFAULT_NIGHT_TEMPERATURE = 4500</c>; the group also has <c>DayTemperature</c> (6500), which this
/// link never touches.
///
/// <c>Mode</c> IS STORED AS THE INTEGER, AND WRITING THE CHOICE'S NAME IS A SILENT NO-OP — measured.
/// <c>nightlightsettings.kcfgc</c> sets <c>UseEnumTypes=true</c>, so the compiler gives every choice a
/// <c>value</c> taken from the C++ enumerator; <c>readConfig</c> compares the config entry against those same
/// values, so an entry of <c>Constant</c> matches nothing and the reader falls through to <c>readEntry(int)</c>,
/// which hands back the DEFAULT. Measured: <c>Mode=Constant</c> leaves KWin at <c>mode=1</c> with the screen
/// untouched.
///
/// THE MARKERS are the reversibility mechanism: keys of ours inside that same group (KWin ignores unknown keys),
/// holding the values the user had BEFORE the app wrote anything. Without them a run that ends without releasing
/// would leave <c>Active=true</c> behind, and the next run — which must respect an enabled night light — would see
/// OUR leftover as the user's. They are needed because the values cannot identify the leftover: 4500 is both this
/// app's "Low" AND KWin's own DEFAULT_NIGHT_TEMPERATURE.
/// </summary>
internal static class KwinConfig
{
    /// <summary>The config file, as <c>kwriteconfig6 --file</c> wants it: a bare name resolved in the user's
    /// config directory. A path would bypass <c>XDG_CONFIG_HOME</c>.</summary>
    internal const string File = "kwinrc";

    internal const string Group = "NightColor";

    internal const string ActiveKey = "Active";
    internal const string ModeKey = "Mode";
    internal const string TemperatureKey = "NightTemperature";

    /// <summary>The D-Bus <c>mode</c> value for the constant-temperature choice. KWin's enum is declared
    /// <c>enum NightLightMode { Constant, DarkLight }</c>, so Constant is 0 — confirmed by an untouched kwinrc
    /// reporting <c>mode=1</c> (DarkLight) on the bus.</summary>
    internal const int ConstantMode = 0;

    internal const int DarkLightMode = 1;

    /// <summary>The <c>Mode</c> value as it is WRITTEN to <c>kwinrc</c> — the integer, NOT the choice's name, and
    /// that distinction is the whole of a bug. MEASURED on the owner's machine: writing <c>Mode=Constant</c> left
    /// KWin reporting <c>mode=1</c> (DarkLight, its default) and the screen untinted, while <c>Mode=0</c> produced
    /// <c>mode=0</c> and the tint. Written as a literal rather than derived from <see cref="ConstantMode"/> because
    /// a test needs it as an attribute argument; the two are held together by <c>TheModeValuesAreTheEnumIntegers</c>.
    /// </summary>
    internal const string Constant = "0";

    /// <summary>The other <c>Mode</c>: follow the time of day. KWin's own default, and what this link restores when
    /// it was there.</summary>
    internal const string DarkLight = "1";

    // The markers, written in this order and read as a set; see ReadMarkers for why the Active marker is last.
    internal const string PriorActiveMarker = "AcerHelperPriorActive";
    internal const string PriorModeMarker = "AcerHelperPriorMode";
    internal const string PriorTemperatureMarker = "AcerHelperPriorNightTemperature";

    /// <summary>Stands in for "this key was not in the file at all". A sentinel rather than an empty value, because
    /// an empty value is what <c>kreadconfig6</c> also answers for an absent key, and "absent" must be restored by
    /// DELETING the key. No real value of these three keys can collide with it: Mode is 0 or 1, Active is
    /// true/false, NightTemperature is an integer.</summary>
    internal const string Absent = "__absent__";

    /// <summary>All three markers, in write order with <c>Active</c>'s last: a marker set interrupted by a crash
    /// therefore cannot claim a tint that was never switched on.</summary>
    internal static readonly IReadOnlyList<string> Markers = [PriorTemperatureMarker, PriorModeMarker, PriorActiveMarker];

    internal static bool IsOn(string? configValue)
        => configValue is not null && (configValue.Equals("true", StringComparison.OrdinalIgnoreCase)
                                       || configValue == "1"
                                       || configValue.Equals("on", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The command line for one configuration change, built HERE rather than in the Linux half so that the flag which
/// makes the whole route work is pinned by a test. Every form carries <c>--notify</c>.
///
/// WHY <c>--notify</c> IS NOT OPTIONAL: <c>kwriteconfig6</c> without it writes the file and tells nobody, and KWin
/// does not re-read its configuration on its own. MEASURED on the owner's machine, which is how the first version
/// of this link shipped broken: writing <c>Mode</c>/<c>Active</c>/<c>NightTemperature</c> in every combination,
/// with and without <c>org.kde.KWin.reconfigure</c> afterwards, left KWin at
/// <c>enabled=false, mode=1, currentTemperature=6500</c> — the row moved, the screen did not. It is equally
/// load-bearing on the way out: restoring the file to its original contents — even with <c>reconfigure</c> — does
/// NOT switch a running tint off, because KWin never learns the file changed.
///
/// <c>--type</c> is given per key because that is the form measured to work: <c>bool</c> for <c>Active</c> and
/// <c>int</c> for the two numeric keys. The markers get no type at all, since their values are opaque strings.
/// </summary>
internal static class KwinConfigCli
{
    /// <summary>Reads one key. kreadconfig6 answers empty for a key that is not there, which is how "absent" is
    /// told from any real value.</summary>
    internal static string[] Read(string key) =>
        ["kreadconfig6", "--file", KwinConfig.File, "--group", KwinConfig.Group, "--key", key];

    /// <summary>Writes one key, notifying.</summary>
    internal static string[] Write(string key, string value)
    {
        var argv = new List<string> { "kwriteconfig6", "--notify", "--file", KwinConfig.File, "--group", KwinConfig.Group };
        if (TypeOf(key) is { } type) { argv.Add("--type"); argv.Add(type); }
        argv.Add("--key"); argv.Add(key); argv.Add(value);
        return [.. argv];
    }

    /// <summary>Removes one key, notifying — a delete changes what KWin reads just as a write does.</summary>
    internal static string[] Delete(string key)
    {
        var argv = new List<string> { "kwriteconfig6", "--notify", "--file", KwinConfig.File, "--group", KwinConfig.Group };
        if (TypeOf(key) is { } type) { argv.Add("--type"); argv.Add(type); }
        argv.Add("--key"); argv.Add(key); argv.Add("--delete");
        return [.. argv];
    }

    /// <summary>The <c>--type</c> for a key, or null to write it as an opaque string. The two numerics need it to
    /// be read back as numbers; <c>Active</c> needs it to be read back as a real boolean.</summary>
    internal static string? TypeOf(string key) => key switch
    {
        KwinConfig.ActiveKey => "bool",
        KwinConfig.ModeKey or KwinConfig.TemperatureKey => "int",
        _ => null,                                   // the markers: opaque strings, absent sentinel included
    };
}

/// <summary>
/// KWin's night light driven through its configuration: the silent, persistent route. Writes
/// <c>Active=true</c> + <c>Mode=0</c> + <c>NightTemperature=&lt;level&gt;</c> to the <c>NightColor</c> group of
/// <c>kwinrc</c>, every change notifying (see <see cref="KwinConfigCli"/>), and CHECKS what the compositor did.
///
/// WHY A VERIFICATION STEP AND NOT JUST A WRITE. A config write can be a no-op in several ways that all look
/// identical from the file's side: the change signal may not be emitted (the bug that shipped first), KWin may be
/// inhibited, or the mode may land in a form KWin reads as its own default. So an apply reads the compositor back
/// through D-Bus and requires <see cref="NightLightState.Holds"/>; anything less is a failed apply, the config is
/// rolled back and false returned. A release is verified the same way, through
/// <see cref="NightLightState.IsReleased"/>, since restoring the file does not switch a running tint off until
/// KWin is notified of it.
/// </summary>
/// <param name="read">One key of the group, or null when the key is absent (the Linux side maps
/// <c>kreadconfig6</c>'s empty answer to null).</param>
/// <param name="write">Writes one key of the group, notifying.</param>
/// <param name="delete">Removes one key of the group, notifying, so KWin's built-in default applies again.</param>
/// <param name="commit">Waits, up to a bounded time, for the compositor to reach the state asked for: the
/// temperature to be held, or null for "off and nothing applied". Whether that is good enough is this class's
/// decision, not the delegate's.</param>
/// <param name="state">KWin's night-light state over D-Bus, for the pre-checks and the verification.</param>
internal sealed class KwinConfigTint(Func<string, string?> read, Action<string, string> write, Action<string> delete,
                                    Action<int?> commit, Func<NightLightState> state) : IDisplayTint, IDisposable
{
    /// <summary>Five levels, Off first — the shared UI vocabulary.</summary>
    public int Levels => NightTintLevels.Count;

    /// <summary>Whether THIS app run has a tint in the config — true from the moment its three keys are written,
    /// which is BEFORE the read-back that decides whether KWin acted on them. In memory rather than read back from
    /// the markers, because a marker set may equally belong to a previous run that died.</summary>
    private bool _written;

    /// <summary>The temperature this run last wrote, so "is the configuration still ours?" can be asked exactly.
    /// Only meaningful while <see cref="_written"/>.</summary>
    private int _writtenTemperature;

    /// <summary>Whether this app's tint is in the config right now, so a caller can tell "the user turned the filter
    /// off and we cleaned up" from "we wrote and forgot".</summary>
    internal bool IsWritten => _written;

    /// <summary>Set a level, or release on 0. Returns whether the screen actually changed: false means the
    /// compositor is not ours to colour or the write did not take — never that a tint was applied.</summary>
    public bool Apply(int level)
    {
        var wanted = Math.Clamp(level, 0, NightTintLevels.Count - 1);
        if (wanted == 0) return Release();

        var compositor = state();
        if (!compositor.Available || compositor.Inhibited) return false;   // not ours to colour

        if (_written)
        {
            // Our own tint is live — but the file may not hold what this run put there any more, because the user
            // can change it from System Settings while the filter is on. Their setting wins: we stop claiming the
            // configuration and drop the markers WITHOUT restoring.
            if (!StillOurs())
            {
                Forfeit();
                return false;
            }
        }
        else
        {
            // A marker set this run did not write belongs to a run that died — put the user's values back first.
            // Then an enabled night light is the user's own and is respected.
            Recover();
            if (KwinConfig.IsOn(read(KwinConfig.ActiveKey))) return false;
        }

        // Remember the user's values IN THE CONFIG before anything of ours goes near it, and only on the first
        // apply of this run: after that the values in the file are OURS.
        if (!_written)
        {
            var priors = new[]
            {
                new TintPrior(KwinConfig.TemperatureKey, read(KwinConfig.TemperatureKey)),
                new TintPrior(KwinConfig.ModeKey, read(KwinConfig.ModeKey)),
                new TintPrior(KwinConfig.ActiveKey, read(KwinConfig.ActiveKey)),
            };
            foreach (var p in priors) write(Marker(p.Key), p.WasAbsent ? KwinConfig.Absent : p.Value!);
        }

        var temperature = NightTintLevels.Clamp(NightTintLevels.TemperatureFor(wanted));
        write(KwinConfig.TemperatureKey, temperature.ToString(CultureInfo.InvariantCulture));
        write(KwinConfig.ModeKey, KwinConfig.Constant);
        write(KwinConfig.ActiveKey, "true");

        // Remembered HERE, before the verification: these three keys are in the file either way, and a release asks
        // "does the configuration still hold what this run wrote?" (see StillOurs), so the rollback below has to
        // recognise its own writes to put the user's values back.
        _written = true;
        _writtenTemperature = temperature;

        commit(temperature);

        if (state().Holds(temperature)) return true;

        Release();                                      // roll back: leave nothing of ours behind
        return false;
    }

    /// <summary>Whether the configuration still holds what this run wrote. The markers alone cannot answer it —
    /// they survive the user changing the settings underneath them — so the values are compared.</summary>
    private bool StillOurs()
        => KwinConfig.IsOn(read(KwinConfig.ActiveKey))
           && read(KwinConfig.ModeKey) == KwinConfig.Constant
           && read(KwinConfig.TemperatureKey) == _writtenTemperature.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stop claiming a configuration the user has taken over: drop the markers and forget the tint. Nothing
    /// is written back — see the call sites — and KWin needs no nudge, because deleting markers of ours changes
    /// nothing it reads.</summary>
    private void Forfeit()
    {
        foreach (var marker in KwinConfig.Markers) delete(marker);
        _written = false;
        _writtenTemperature = 0;
    }

    /// <summary>
    /// Put the user's own settings back, drop the markers, and confirm the screen is actually out of it.
    ///
    /// Safe to call at any time and on a port that never applied anything: with no marker present it does NOTHING
    /// AT ALL, and deliberately does not check the compositor either, because a night light that is on when the app
    /// never tinted is the user's own business. It reads the markers rather than an in-memory snapshot so that a
    /// release after a crash is the same code path as a release after a click. The returned flag is what the
    /// compositor reports, not what the file looks like.
    ///
    /// AND IT ASKS WHETHER THE CONFIGURATION IS STILL OURS, which is <see cref="Apply"/>'s rule on the way out: the
    /// user can change the night light from System Settings while the filter is on, and restoring what the app
    /// recorded would write their OLD values over the choice they just made.
    /// </summary>
    internal bool Release()
    {
        var priors = ReadMarkers();
        if (priors is null) return true;               // the config is not ours — hands off

        // Only while this run has a tint there to claim: with no writes of ours in the file the markers are a dead
        // run's (Recover's business) and there is no "what this run wrote" to compare against.
        if (_written && !StillOurs())
        {
            Forfeit();
            return state().IsReleased;
        }

        Restore(priors);
        _written = false;
        return state().IsReleased;
    }

    /// <summary>Called through <c>Device.Own</c> when the app exits. Same as <see cref="Release"/>: an app that
    /// exits cleanly leaves the user's settings exactly as they were, and one that does not is covered by the
    /// markers on the next run.</summary>
    public void Dispose() => Release();

    /// <summary>
    /// Undo a tint a previous run left behind, if it left one — the crash path, run at startup as well as before
    /// every apply: before an apply, so the check that follows does not see OUR <c>Active=true</c> and refuse to
    /// touch it; at startup, because the app may come back with the filter switched OFF, and then nothing would
    /// clean up and the screen would stay warm.
    /// </summary>
    internal void Recover()
    {
        if (ReadMarkers() is not { } stale) return;
        Restore(stale);
        _written = false;
    }

    /// <summary>
    /// The user's values as the markers recorded them, or null when the config is not ours.
    ///
    /// A partial set answers an EMPTY list — "ours, but nothing to restore" — because the markers are written in
    /// order and the real keys only after all three, so an interrupted marker write changed nothing of the user's.
    /// </summary>
    private IReadOnlyList<TintPrior>? ReadMarkers()
    {
        var temperature = read(KwinConfig.PriorTemperatureMarker);
        var mode = read(KwinConfig.PriorModeMarker);
        var active = read(KwinConfig.PriorActiveMarker);

        if (temperature is null && mode is null && active is null) return null;   // not ours
        if (temperature is null || mode is null || active is null) return [];     // torn: nothing was changed

        return
        [
            new TintPrior(KwinConfig.TemperatureKey, Decode(temperature)),
            new TintPrior(KwinConfig.ModeKey, Decode(mode)),
            new TintPrior(KwinConfig.ActiveKey, Decode(active)),
        ];
    }

    /// <summary>Write the recorded values back — deleting the keys that were absent, so KWin's own defaults are
    /// what the user is left with — then drop the markers and wait for the compositor to let go. The markers go
    /// last: an interruption anywhere in here leaves them in place for the next run to finish the job. The wait is
    /// for KWin to have stopped, not merely for the file to look right — see <see cref="Release"/>.</summary>
    private void Restore(IReadOnlyList<TintPrior> priors)
    {
        foreach (var p in priors)
        {
            if (p.RestoreValue is { } value) write(p.Key, value);
            else delete(p.Key);
        }
        foreach (var marker in KwinConfig.Markers) delete(marker);
        commit(null);                  // null = "expect it off and nothing applied"
    }

    private static string Marker(string key) => key switch
    {
        KwinConfig.ActiveKey => KwinConfig.PriorActiveMarker,
        KwinConfig.ModeKey => KwinConfig.PriorModeMarker,
        _ => KwinConfig.PriorTemperatureMarker,
    };

    private static string? Decode(string marker) => marker == KwinConfig.Absent ? null : marker;
}
