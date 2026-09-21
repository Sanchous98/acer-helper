using System.Globalization;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Blue-light reduction on GNOME, by writing GNOME's own night-light settings — the same shape as KwinTint.cs and
// for the same reasons: silent, persistent and reversible beats a mechanism that has to be re-armed. The policy is
// in this UN-SUFFIXED file so the suite can reach it (see DisplayTintChain.cs for why); the gsettings I/O stays in
// GnomeTint.Linux.cs and arrives as delegates.
//
// UNVERIFIED ON THIS MACHINE, and that is stated rather than glossed: the owner's session is KDE, where
// `gsettings list-schemas` does not list org.gnome.settings-daemon.plugins.color at all (measured — the schema is
// simply not installed), so this adapter's probe declines here and NOTHING in this file or its Linux half has ever
// run against a real GNOME. What it is written from is gnome-settings-daemon's own schema source
// (data/org.gnome.settings-daemon.plugins.color.gschema.xml.in), which is where the key names, their types and
// their defaults below come from. Treat it as a documented implementation awaiting a GNOME machine, not as a
// measured one — and note that the chain will not select it on a KDE box, so an untested adapter can never be the
// one the owner gets.

/// <summary>
/// Every name this link writes, read out of gnome-settings-daemon's colour schema.
///
///   * <c>night-light-enabled</c> — Bool, default false.
///   * <c>night-light-temperature</c> — <c>u</c>, KELVIN (the schema says so: "This temperature in Kelvin is used
///     to modify the screen tones... Higher values are bluer, lower redder"), default <c>2700</c>. That default is
///     BELOW our lowest step (3000), which is precisely why the user's own value has to be remembered and put back:
///     leaving our 3000 behind would silently raise a preference the user had set warmer than anything this app
///     offers.
///   * <c>night-light-schedule-automatic</c> — Bool, default TRUE, i.e. a GNOME night light that has been switched
///     on follows sunset and sunrise unless the user turned the schedule off. That default is why the rule below is
///     "an enabled night light is left alone" rather than "a night light that is tinting right now".
/// </summary>
internal static class GnomeConfig
{
    internal const string Schema = "org.gnome.settings-daemon.plugins.color";

    internal const string EnabledKey = "night-light-enabled";
    internal const string TemperatureKey = "night-light-temperature";

    internal static bool IsOn(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || value == "1"
                                 || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The Kelvin value out of a <c>gsettings get</c> answer, or null when it is not one.
    ///
    /// The parsing is not decoration: a <c>u</c> key is printed WITH its type, as <c>uint32 2700</c>, so the
    /// integer has to be picked out of that. UNVERIFIED — this format is what `gsettings` is documented to print
    /// for an unsigned integer key, and no GNOME is installed here to observe it on. If it is ever wrong, the
    /// adapter's read-back check fails and the apply rolls back rather than reporting a tint that is not there,
    /// which is the failure mode to prefer.
    /// </summary>
    internal static int? ParseKelvin(string? printed)
    {
        if (printed is null) return null;
        var last = printed.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kelvin) ? kelvin : null;
    }
}

/// <summary>
/// GNOME's night light driven through its configuration.
///
/// The discipline is <see cref="KwinConfigTint"/>'s: refuse to touch the user's own night light, remember what was
/// there, verify what the compositor did, and put the user's values back when the filter is turned off, when the
/// app exits, or when an apply fails. What differs is only what the platform allows:
///
///   * There are NO crash-recovery markers here, and that is a real gap rather than a simplification.
///     <c>gsettings</c> refuses to write a key its schema does not define, so unlike KWin's <c>kwinrc</c> — where
///     the app can leave three keys of its own beside the ones it changes — GNOME gives it nowhere to record the
///     user's prior values. The memory is therefore this object's, and a run that ends without releasing leaves
///     <c>night-light-enabled=true</c> behind. The next run then declines (an enabled night light is left alone),
///     so the visible symptom of that crash is GNOME's own night light switched on and this app's row hidden — the
///     user's own settings dialog shows it and can undo it. Recoverable, visible, and documented; on KWin the same
///     leftover would additionally have locked this app out of its own route, which is why the marker exists there
///     and not here.
///   * There is no "absent" to restore, because <c>gsettings get</c> always answers — with the schema default when
///     the user has never set the key. Restoring therefore writes the remembered value back rather than deleting
///     it, so a key that was at its default may end up explicitly set to that same default. That is behaviourally
///     identical, and it is the closest thing to reversibility the interface offers.
/// </summary>
/// <param name="read">One key of the schema, as <c>gsettings get</c> prints it.</param>
/// <param name="write">Writes one key of the schema.</param>
/// <param name="commit">Makes the running configuration pick the change up, if the platform needs a nudge.</param>
internal sealed class GnomeConfigTint(Func<string, string?> read, Action<string, string> write, Action commit)
    : IDisplayTint, IDisposable
{
    private IReadOnlyList<TintPrior>? _priors;

    /// <summary>The temperature this run last wrote, so "are the settings still ours?" can be asked exactly.
    /// Only meaningful while <see cref="_priors"/> is set.</summary>
    private int _writtenTemperature;

    /// <summary>Five levels, Off first — the shared UI vocabulary.</summary>
    public int Levels => NightTintLevels.Count;

    /// <summary>Whether this app's tint is written right now.</summary>
    internal bool IsWritten => _priors is not null;

    public bool Apply(int level)
    {
        var wanted = Math.Clamp(level, 0, NightTintLevels.Count - 1);
        if (wanted == 0) return Release();

        if (_priors is not null)
        {
            // Our own tint is live — but the user may have changed the settings since, from GNOME's own panel.
            // Their value wins: stop claiming it, WITHOUT writing the remembered value back over their choice.
            if (!StillOurs())
            {
                Forfeit();
                return false;
            }
        }
        else if (GnomeConfig.IsOn(read(GnomeConfig.EnabledKey)))
        {
            // The user's own night light — enabled, and by default on a sunset-to-sunrise schedule. Left alone,
            // and the composition root says so rather than hiding behind a row that would override it.
            return false;
        }

        // Remember once, so a second apply cannot record our own tint as the user's preference.
        _priors ??= [new TintPrior(GnomeConfig.TemperatureKey, read(GnomeConfig.TemperatureKey))];

        var temperature = NightTintLevels.Clamp(NightTintLevels.TemperatureFor(wanted));
        write(GnomeConfig.TemperatureKey, temperature.ToString(CultureInfo.InvariantCulture));
        write(GnomeConfig.EnabledKey, "true");
        commit();

        if (Holds(temperature))
        {
            _writtenTemperature = temperature;
            return true;
        }

        Release();
        return false;
    }

    /// <summary>Put the user's settings back. Nothing to do — and NOTHING TOUCHED — on a port that never applied
    /// anything, so "Off" cannot switch off a night light the app never switched on.</summary>
    internal bool Release()
    {
        if (_priors is not { } priors) return true;

        write(GnomeConfig.EnabledKey, "false");        // we only ever applied while it was off
        foreach (var p in priors) write(p.Key, p.Value ?? string.Empty);
        _priors = null;
        _writtenTemperature = 0;
        commit();
        return true;
    }

    /// <summary>Whether the settings still hold what this run wrote — the only way to notice that the user has
    /// taken the night light over while the filter was on, since the remembered value cannot tell by itself.</summary>
    private bool StillOurs()
        => GnomeConfig.IsOn(read(GnomeConfig.EnabledKey))
           && GnomeConfig.ParseKelvin(read(GnomeConfig.TemperatureKey)) == _writtenTemperature;

    /// <summary>Stop claiming settings the user has taken over: forget the tint and the recalled value, and write
    /// nothing — see the call site.</summary>
    private void Forfeit()
    {
        _priors = null;
        _writtenTemperature = 0;
    }

    public void Dispose() => Release();

    /// <summary>What the platform actually did, not what was asked: the night light on AND at our temperature.</summary>
    private bool Holds(int temperature)
        => GnomeConfig.IsOn(read(GnomeConfig.EnabledKey))
           && GnomeConfig.ParseKelvin(read(GnomeConfig.TemperatureKey)) == temperature;
}
