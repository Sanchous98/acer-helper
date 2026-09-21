using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// The blue-light CHAIN and the policy every link in it shares. UN-SUFFIXED deliberately, for the reason
// AcerProfilePorts.cs, AcerFanPort.cs and KwinTint.cs all state at length: the test project targets
// net10.0-windows while AcerHelper.csproj excludes **/*.Linux.cs from that TFM, so policy left in a Linux file
// cannot be reached by the suite at all. Each link keeps its transport in its own *.Linux.cs half and arrives
// here as delegates.
//
// THE CHAIN, in order, each falling through when its environment is not there:
//
//   1. X11 gamma ramp            (DisplayTint.*.cs — Windows and Linux; on Windows it is the only link)
//   2. KWin NightLight, by CONFIGURATION   (KwinTint.cs + KwinTint.Linux.cs)
//   3. GNOME night light, by CONFIGURATION (GnomeTint.cs + GnomeTint.Linux.cs)
//   4. wlr-gamma-control         (only where the compositor advertises it — not on KWin; see TintProbe)
//   5. nothing — the port is null and the Options row hides. Never a link that cannot actually change the screen.
//
// WHY A CONFIG ROUTE AT ALL, given the compositor also exposes preview()/stopPreview() over D-Bus, which is
// exact and self-clearing. That route was measured end to end and REJECTED: a preview lasts 15 s and re-arming
// it — the only way to make it persistent — makes KWin pop its "Color Temperature Preview" OSD on every re-arm
// (measured: two calls on the bus, two showText and two osdText). A popup every few seconds is not parity with
// the Win32 gamma ramp, which is silent. Writing the compositor's own night-light settings is silent, persistent
// and REVERSIBLE, and it is what the desktop's own settings dialog does. The preview measurements are kept in
// docs/acer-linux.md as the reason it is not used here.

/// <summary>
/// One candidate in the chain: a name (for the tests and for the order this file documents) and a probe that
/// answers with the port, or null when this environment cannot be served by that link.
///
/// The probe runs ONCE, when the chain is built, and that is the whole of the chain's fall-through: a link whose
/// environment is absent is simply not the link. It is deliberately not retried per <see cref="IDisplayTint.Apply"/>
/// — silently moving a user's click onto a different mechanism mid-flight would make a failure invisible and could
/// change the rendering under them. An apply that fails reports false and stays put.
/// </summary>
internal readonly record struct TintLink(string Name, Func<IDisplayTint?> Probe);

/// <summary>
/// The resolved chain: the first link whose probe answered, and nothing else. Constructed through
/// <see cref="TryCreate"/>, which returns null when every link declined so the composition root can leave the
/// port unset and the UI can hide the row.
///
/// IT IS ALSO THE PORT THE DEVICE RELEASES, and that is not decoration. Two of the links — KWin's and GNOME's —
/// work by writing the user's OWN configuration and putting it back on the way out (KwinConfigTint.Dispose →
/// Release, GnomeConfigTint the same), because a tint implemented as a configuration write survives the process
/// that wrote it. The device releases what it owns (<c>Device.Own</c> takes an IDisposable), and what it is
/// handed is THIS object, so a chain that cannot be disposed is a release that never reaches those two links:
/// <c>kwinrc</c> keeps <c>Active=true</c> at this app's temperature after the user quits, and KWin goes on
/// tinting a screen the app is no longer running behind — the exact opposite of the rule the links state.
/// </summary>
internal sealed class DisplayTintChain(string activeName, IDisplayTint active) : IDisplayTint, IDisposable
{
    /// <summary>Which link serves this session. Kept because "which mechanism is the user actually on" is the
    /// first question a bug report about the filter raises, and it is not answerable from the UI.</summary>
    internal string ActiveName => activeName;

    /// <summary>The chosen link, for tests that assert the order by identity rather than by effect.</summary>
    internal IDisplayTint Active => active;

    public int Levels => active.Levels;

    public bool Apply(int level) => active.Apply(level);

    /// <summary>Release the chosen link, if it has anything to release. Forwarded rather than absorbed, for the
    /// reason the class comment gives: the settings-writing links put the user's configuration back here and
    /// nowhere else. A link that is not disposable holds nothing to give back — the X11 gamma ramp is the
    /// process's own X connection and the server drops its ramp with the client — so it is skipped rather than
    /// required to implement a no-op.</summary>
    public void Dispose()
    {
        if (active is IDisposable owned) owned.Dispose();
    }

    /// <summary>The first link that answers wins; null when none does. The order of <paramref name="links"/> IS
    /// the fall-through order, so it is the one thing the tests pin here.</summary>
    internal static DisplayTintChain? TryCreate(IReadOnlyList<TintLink> links)
    {
        foreach (var link in links)
        {
            if (link.Probe() is { } port) return new DisplayTintChain(link.Name, port);
        }
        return null;
    }
}

/// <summary>
/// The three availability decisions, as pure functions of the facts each Linux probe can establish. They live here,
/// away from the probes, because each one encodes a MEASURED fact about this machine rather than a guess, and
/// because getting one wrong is how the feature was broken in the first place.
/// </summary>
internal static class TintProbe
{
    /// <summary>
    /// Whether an X11 gamma ramp can change what the user sees. TWO conditions, and the second is the bug this
    /// whole exercise started from.
    ///
    /// <c>xrandr</c> answering is NOT enough: on the owner's KWin/Wayland session <c>DISPLAY=:0</c> is set because
    /// Plasma starts XWayland, so <c>xrandr --query</c> SUCCEEDS and reports "DP-1 connected primary 3440x1440"
    /// (measured). The old single-condition probe therefore returned true, the port was created, and
    /// <c>xrandr --gamma</c> wrote into XWayland's ramp — which is not the path the screen is composed from, so
    /// nothing happened. The owner's report was exactly that: the row was there and did nothing.
    ///
    /// On a Wayland session the compositor owns colour and an X11 gamma ramp cannot reach it, whatever XWayland
    /// answers. On a real X11 session (KWin or otherwise) it is the right, silent, config-free lever, and it wins
    /// the chain there.
    /// </summary>
    internal static bool X11GammaWorks(bool xrandrFoundOutputs, bool sessionIsWayland)
        => xrandrFoundOutputs && !sessionIsWayland;

    /// <summary>
    /// Whether KWin's own night-light SETTINGS can be written. All three facts are separate machines: no session
    /// bus at all; a session whose compositor has no night-light interface (an X11 session under another window
    /// manager — the X11 link's territory); and a KWin that has the interface but reports <c>available=false</c>,
    /// where a write would be accepted and change nothing.
    ///
    /// Whether the USER's own night light is enabled is deliberately NOT part of this: that is checked per apply
    /// against the config itself, because it changes while the app runs and because the answer decides whether the
    /// app says something rather than whether the mechanism exists.
    /// </summary>
    internal static bool KwinConfigUsable(bool busReachable, bool interfacePresent, bool compositorAvailable)
        => busReachable && interfacePresent && compositorAvailable;

    /// <summary>Whether GNOME's night light can be written: <c>gsettings</c> is on PATH and the colour schema it
    /// would write into is installed. The schema is the real test — <c>gsettings</c> ships on KDE systems too,
    /// where the schema is absent, and a write without it fails.</summary>
    internal static bool GnomeUsable(bool gsettingsPresent, bool colourSchemaPresent)
        => gsettingsPresent && colourSchemaPresent;
}

/// <summary>
/// The five UI levels as colour temperatures in kelvin — the ladder the two temperature-based links share (KWin's
/// night light and GNOME's). The level <i>vocabulary</i> is a cross-OS contract: the Options row builds its names
/// from <see cref="Count"/> and the same Off/Low/Medium/High/Long-use list serves the Windows gamma ramp, the X11
/// one and these, so the count and the ordering are fixed by that contract and only the temperatures are these
/// links' own.
///
/// THEY ARE NOT THE SAME COLOUR AS THE GAMMA-RAMP LINKS, and that is inherent rather than a defect to fix. The
/// Windows ramp and the X11 ramp scale the BLUE CHANNEL only (1.00/0.85/0.70/0.60/0.50), which does not move the
/// white point along the Planckian locus at all: it tilts white towards yellow-green, and the correlated colour
/// temperature of the result runs the WRONG WAY (0.50 blue computes to ≈6235 K against neutral's ≈4664 K by
/// McCamy), so no honest "equivalent temperature" can be derived from those tables. So this ladder is a
/// conventional warm ladder of the kind redshift/gammastep use — each step a real blackbody shift, which is the
/// better colour of the two — and the links agree on the level NAMES and the direction, not on the rendering.
/// </summary>
internal static class NightTintLevels
{
    /// <summary>Neutral white. KWin's own <c>DEFAULT_DAY_TEMPERATURE</c> (read from its source: constants.h) and
    /// already the observed value when nothing is applied; also the ceiling KWin clamps a temperature to.</summary>
    internal const int Neutral = 6500;

    // Off is never written: level 0 releases instead (see each adapter's Apply). The four warm steps are
    // "Long-use" strongest, the same order the Windows and X11 tables use.
    private static readonly int[] Kelvin = [Neutral, 4500, 4000, 3500, 3000];

    /// <summary>How many levels the UI gets — 5, Off included, as on every other link.</summary>
    internal static int Count => Kelvin.Length;

    /// <summary>The temperature for a UI level, clamped into the table. Level 0 answers <see cref="Neutral"/>,
    /// which is what "off" means; callers release rather than ask for it.</summary>
    internal static int TemperatureFor(int level) => Kelvin[Math.Clamp(level, 0, Kelvin.Length - 1)];

    /// <summary>Clamp to what the compositor will accept, at the top end only. 6500 is KWin's own ceiling for a
    /// night temperature (<c>DEFAULT_DAY_TEMPERATURE</c>, from constants.h); the FLOOR is KWin's too
    /// (<c>MIN_TEMPERATURE = 1000</c>, same file, measured) so no constant of ours is invented for it, and the
    /// ladder never goes near either bound anyway (3000..4500). GNOME's schema clamps to its own floor in the same
    /// spirit.</summary>
    internal static int Clamp(int temperature) => Math.Min(temperature, Neutral);
}

/// <summary>
/// One setting as it was <b>before</b> the app touched it, as read out of the config file.
/// <see cref="Value"/> null means the key was ABSENT, which is not the same as present-and-empty: restoring an
/// absent key means DELETING it so the application's own built-in default applies again, where writing back the
/// default it happens to resolve to would leave a key the user never had. Every remember/restore path in the
/// config-writing links goes through this distinction.
/// </summary>
internal readonly record struct TintPrior(string Key, string? Value)
{
    internal bool WasAbsent => Value is null;

    /// <summary>The string this prior should be written back as, or null when the key must be deleted — the one
    /// expression both restore paths share, so "delete what was absent" cannot be forgotten in one of them.</summary>
    internal string? RestoreValue => Value;
}
