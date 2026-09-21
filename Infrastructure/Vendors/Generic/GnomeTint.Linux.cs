using System.Diagnostics;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// The gsettings I/O for the GNOME blue-light link; the policy is in the un-suffixed GnomeTint.cs.
//
// UNVERIFIED HERE, and the reason is in the probe rather than in a comment alone: this machine is KDE, and
// `gsettings list-schemas` does not list org.gnome.settings-daemon.plugins.color at all (measured: gsettings
// answers "schema does not exist"), so GnomeSettings.SchemaPresent() is false, the link declines, and the chain
// never selects it. Nothing in this file has run against a real GNOME. The key names, types and defaults it uses
// come from gnome-settings-daemon's own schema source
// (data/org.gnome.settings-daemon.plugins.color.gschema.xml.in): night-light-enabled is a Bool defaulting to
// false, night-light-temperature is a `u` in KELVIN defaulting to 2700, and night-light-schedule-automatic
// defaults to true — so an enabled GNOME night light follows sunset and sunrise unless the user said otherwise,
// which is why the link leaves an enabled night light entirely alone.
internal static class GnomeSettings
{
    /// <summary>Whether the tool this link writes through is on PATH at all.</summary>
    internal static bool ToolPresent() => Call("gsettings", "--version").code == 0;

    /// <summary>Whether GNOME's colour schema is installed. The real test, not the presence of
    /// <c>gsettings</c>: the tool ships on KDE systems too, and a write without the schema fails.</summary>
    internal static bool SchemaPresent()
    {
        var (code, output) = Call("gsettings", "list-schemas");
        return code == 0 && output.Split('\n').Any(l => l.Trim() == GnomeConfig.Schema);
    }

    /// <summary>One key, as <c>gsettings get</c> prints it (a <c>u</c> key prints as <c>uint32 2700</c>, which
    /// GnomeConfig.ParseKelvin takes apart), or null when the call failed.</summary>
    internal static string? Read(string key)
    {
        var (code, output) = Call("gsettings", "get", GnomeConfig.Schema, key);
        if (code != 0) return null;
        var value = output.Trim();
        return value.Length == 0 ? null : value;
    }

    internal static void Write(string key, string value)
        => Call("gsettings", "set", GnomeConfig.Schema, key, value);

    /// <summary>
    /// Nothing to do: a <c>gsettings</c> write lands in dconf and the settings daemon watches its own keys, so the
    /// change is live by the time the call returns. It exists because the link's policy needs the same shape as
    /// KWin's, where a nudge really is required.
    /// </summary>
    internal static void Commit()
    {
    }

    private static (int code, string output) Call(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            for (var i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
