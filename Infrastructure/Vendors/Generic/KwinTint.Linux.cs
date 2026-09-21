using System.Diagnostics;
using System.Text.RegularExpressions;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// The I/O half of the KWin blue-light link. Everything that decides anything lives in KwinTint.cs, which is
// UN-SUFFIXED so the suite can reach it — this file is excluded from the test project's net10.0-windows TFM, so
// only what cannot be tested without a compositor belongs here: the config reads and writes, the reconfigure call,
// the session probe, and the D-Bus read that verifies a write landed.
//
// MEASURED ON THIS MACHINE (KWin 6.7.5, 2026-09-20), because each of these is a trap otherwise:
//   * THE WRITES MUST NOTIFY. `kwriteconfig6` without `--notify` writes the file and tells nobody, and KWin does
//     not re-read its configuration by itself: it keeps using its in-memory copy. Measured both ways — with
//     `--notify` a write takes effect at once, and without it KWin stayed at `enabled=false, mode=1,
//     currentTemperature=6500` no matter what was written, and stayed there even with an
//     `org.kde.KWin.reconfigure` call after every write. `reconfigure` is therefore NOT used anywhere in this
//     file. The same fact applies to the release path: putting the original file back does not switch a running
//     tint off until KWin is notified, which is why the restore writes notify too and why the release is verified
//     against the compositor rather than against the file.
//   * `Mode` is written as the INTEGER (see KwinConfig in the un-suffixed half); the choice NAME is a silent
//     no-op that KWin reads as its own default.
//   * `kreadconfig6 --file kwinrc --group NightColor --key X` answers EMPTY, with exit status 0, for a key that is
//     not in the file — so "absent" is read from an empty answer, never from a failure. That is what makes an
//     absent key restorable (delete it again) rather than being replaced by the default it resolves to.
//   * `--file` takes a BARE NAME resolved in the user's config directory, which is what KWin itself opens; a path
//     would bypass XDG_CONFIG_HOME. (`--file probe.ini` writing to ~/.config/probe.ini is how that was found.)
//   * One `kreadconfig6`/`kwriteconfig6` call is ~6 ms, so a write set costs a handful of process spawns — which
//     matters because Apply runs on the UI thread. The one slow case is a LEVEL CHANGE while the filter is
//     already on: verifying it requires KWin's 2000 ms quick-adjust walk to finish, so that click can take up to
//     the Commit timeout below. Moving Apply off the UI thread would be the fix, and it is not this file's to make
//     (the call site is LaptopService.SetBlueLight).
//   * Reads do NOT include the global config files, which is exactly right here: the file this link restores is
//     the USER's kwinrc, so a value that came from /etc/xdg must not be promoted into it. (No distribution ships a
//     NightColor group, and the group only appears once a user configures night light, so the effective and
//     per-user values are the same in practice.)
//
// WHY ITS OWN busctl RUNNER rather than the Busctl class in PowerProfiles.Linux.cs: that one is hard-wired to
// --system and is shared with power-profiles-daemon and UPower. The KWin interface is per-SESSION, so every call
// here has to carry --user; the two are different buses with different failure modes.

/// <summary>
/// The <c>busctl --user</c> calls to KWin's night light, and the parse of its <c>GetAll</c> payload — the read side
/// of the link: the pre-checks before a write, and the read-back that decides whether the write landed.
/// </summary>
internal static class KwinNightLightBus
{
    private const string Bus   = "org.kde.KWin";
    private const string Obj   = "/org/kde/KWin/NightLight";
    private const string Iface = "org.kde.KWin.NightLight";

    /// <summary>What is read when the compositor cannot answer at all: not available, so the link declines rather
    /// than writing into a configuration nothing will act on.</summary>
    private static readonly NightLightState Unavailable = new(Available: false, Enabled: false, Inhibited: false,
                                                              Daylight: true, Mode: KwinConfig.DarkLightMode,
                                                              Running: false, CurrentTemperature: NightTintLevels.Neutral);

    /// <summary>Whether a session bus answers this process at all. Asked of the bus daemon itself
    /// (<c>org.freedesktop.DBus.ListNames</c>), which exists on every session bus, so a failure here really does
    /// mean "no session bus" and not "no KWin night light" — the two cases the chain's probe separates.</summary>
    internal static bool SessionBusReachable()
        => Call("call", "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames").code == 0;

    /// <summary>The compositor's state, or null when the interface is not there (an X11 session under another
    /// window manager, or a compositor without night light).</summary>
    internal static NightLightState? Probe()
    {
        var (code, payload) = Call("call", Bus, Obj, "org.freedesktop.DBus.Properties", "GetAll", "s", Iface);
        if (code != 0) return null;
        var available = Bool(payload, "available");
        if (available == null) return null;      // answered, but not this interface's payload
        return new NightLightState(available.Value, Bool(payload, "enabled") ?? false,
                                   Bool(payload, "inhibited") ?? false, Bool(payload, "daylight") ?? true,
                                   Int(payload, "mode") ?? KwinConfig.DarkLightMode,
                                   Bool(payload, "running") ?? false,
                                   Int(payload, "currentTemperature") ?? NightTintLevels.Neutral);
    }

    /// <summary>The state, never null — one thing for the policy to read.</summary>
    internal static NightLightState Read() => Probe() ?? Unavailable;

    /// <summary>Reads one scalar out of a <c>GetAll</c> payload — <c>a{sv} 12 "available" b true ...</c> — or null
    /// when the key is not in it.</summary>
    private static bool? Bool(string payload, string key)
    {
        var m = Regex.Match(payload, "\"" + key + "\"\\s+b\\s+(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    private static int? Int(string payload, string key)
    {
        var m = Regex.Match(payload, "\"" + key + "\"\\s+u\\s+(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static (int code, string output) Call(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("busctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--user");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

/// <summary>
/// The write side: KWin's <c>NightColor</c> group through <c>kreadconfig6</c>/<c>kwriteconfig6</c>, the tools the
/// system provides and the ones KWin reacts to. The command lines themselves are built in the un-suffixed
/// <c>KwinConfigCli</c>, where the load-bearing <c>--notify</c> flag is pinned by a test; this class only runs
/// them.
/// </summary>
internal static class KwinConfigFile
{
    /// <summary>
    /// How long to give KWin to reach the state asked for before the caller's read-back is taken as the answer.
    ///
    /// SIZED FOR THE RAMP, not for a round trip. Enabling the night light applies the temperature at once, but
    /// CHANGING it while already enabled goes through KWin's quick-adjust, which walks currentTemperature towards
    /// the target in 50 K steps over QUICK_ADJUST_DURATION = 2000 ms. Since the verification requires the CURRENT
    /// temperature (see NightLightState.Holds), a level change is only confirmable when that walk has finished, so
    /// the wait has to outlast it. The loop still exits on the first poll that sees the state, which is the usual
    /// case for the first apply.
    /// </summary>
    private const int CommitTimeoutMs = 2500;

    private const int PollMs = 100;

    /// <summary>One key of the group, or null when it is absent — which <c>kreadconfig6</c> reports as an empty
    /// answer with a zero exit status.</summary>
    internal static string? Read(string key)
    {
        var (code, output) = Call(KwinConfigCli.Read(key));
        if (code != 0) return null;
        var value = output.Trim();
        return value.Length == 0 ? null : value;
    }

    internal static void Write(string key, string value) => Call(KwinConfigCli.Write(key, value));

    /// <summary>Remove a key, so KWin's own built-in default is what the user is left with. This is how a key that
    /// was ABSENT before the app touched it goes back to being absent.</summary>
    internal static void Delete(string key) => Call(KwinConfigCli.Delete(key));

    /// <summary>
    /// Wait for the compositor to reach the state asked for: holding <paramref name="expected"/> degrees, or — when
    /// it is null — off with nothing being applied.
    ///
    /// NO <c>reconfigure</c> HERE, and that is deliberate: it was measured not to help. KWin acts on the
    /// configuration when it is NOTIFIED of the change, which the writes do; a reconfigure call on its own, with a
    /// silently-written file, left KWin on its in-memory copy every time. If the state does not arrive within the
    /// timeout the caller's verification is what decides, and a failed verification rolls the write back.
    /// </summary>
    internal static void Commit(int? expected)
    {
        var deadline = Environment.TickCount64 + CommitTimeoutMs;
        while (true)
        {
            var state = KwinNightLightBus.Read();
            if (expected is { } temperature ? state.Holds(temperature) : state.IsReleased) return;
            if (Environment.TickCount64 >= deadline) return;
            Thread.Sleep(PollMs);
        }
    }

    private static (int code, string output) Call(string[] argv)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            for (var i = 1; i < argv.Length; i++) psi.ArgumentList.Add(argv[i]);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
