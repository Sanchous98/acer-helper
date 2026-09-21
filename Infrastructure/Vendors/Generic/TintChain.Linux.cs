using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// The composition of the blue-light chain on Linux: which links this session offers, in the order they are tried,
// and — when none does — the sentence that says why. The chain itself, its order and the availability decisions
// are in the un-suffixed DisplayTintChain.cs, where the suite can reach them; this file only gathers the facts.
internal static class LinuxTint
{
    /// <summary>
    /// The port for this session, or null so the Options row hides. <paramref name="Note"/> is set only when the
    /// reason is one the user can act on — KDE's own night light having taken the colour pipeline — and is null for
    /// the ordinary "this desktop offers nothing to write to" case, which is not worth a status line.
    /// </summary>
    internal static (IDisplayTint? Port, string? Note) Create()
    {
        var links = new List<TintLink>
        {
            // 1. The X11 gamma ramp — silent, persistent, and no configuration touched. Right on a real X11
            //    session; on Wayland it is a lie, which is what TintProbe.X11GammaWorks is for (measured: XWayland
            //    answers xrandr on this session, so "xrandr found outputs" alone selects a dead mechanism).
            new("x11-gamma-ramp", () =>
            {
                var x11 = new DisplayTint();
                return TintProbe.X11GammaWorks(x11.Available, IsWaylandSession()) ? x11 : null;
            }),

            // 2. KWin's own night light, by configuration.
            new("kwin-night-light-config", KwinLink),

            // 3. GNOME's own night light, by configuration.
            new("gnome-night-light-config", GnomeLink),

            // 4. wlr-gamma-control. A PERMANENT ramp, which would be the ideal mechanism — it is silent, needs no
            //    configuration and no re-arming — but it is a wlroots protocol and the compositor here does not
            //    offer it: `wayland-info` on this session lists 66 globals and none of them is
            //    zwlr_gamma_control_v1 (measured). Nor could the app use it as it stands: the protocol is reached
            //    by scanning the Wayland registry, and this app links no Wayland client library. The slot is kept
            //    rather than deleted so that the ORDER and the reason are recorded here, and so a future compositor
            //    that does advertise it has an obvious place to be served from. It deliberately never answers with
            //    a port: a link that cannot change the screen must not be selected.
            new("wlr-gamma-control", () => null),
        };

        var chain = DisplayTintChain.TryCreate(links);

        // A tint left behind by a run that ended without releasing — a crash, a kill -9, a power cut. It is undone
        // here, at startup, and not only when the user next touches the filter: the app may well come back with the
        // filter switched off, and a stranded tint must not survive that. When the user does want the filter, the
        // apply that follows simply writes its own values over the cleaned-up ones. With no marker in the config
        // this touches NOTHING of the user's.
        if (chain?.Active is KwinConfigTint kwin) kwin.Recover();

        return (chain, chain is null ? DeclinedNote() : null);
    }

    private static IDisplayTint? KwinLink()
    {
        var bus = KwinNightLightBus.SessionBusReachable();
        var probed = bus ? KwinNightLightBus.Probe() : null;
        if (!TintProbe.KwinConfigUsable(bus, probed is not null, probed?.Available ?? false)) return null;

        return new KwinConfigTint(KwinConfigFile.Read, KwinConfigFile.Write, KwinConfigFile.Delete,
                                  KwinConfigFile.Commit, KwinNightLightBus.Read);
    }

    private static IDisplayTint? GnomeLink()
    {
        var tool = GnomeSettings.ToolPresent();
        if (!TintProbe.GnomeUsable(tool, tool && GnomeSettings.SchemaPresent())) return null;

        return new GnomeConfigTint(GnomeSettings.Read, GnomeSettings.Write, GnomeSettings.Commit);
    }

    /// <summary>
    /// Why the row is hidden, when the reason is the user's own doing. KDE's night light is a setting the app must
    /// not override, so the honest outcome is to offer nothing AND say so — a row that appeared and silently
    /// changed the user's schedule would be worse than no row. A leftover marker of ours is NOT this case (the
    /// night light is then only on because a previous run left it on, which <see cref="KwinConfigTint.Recover"/>
    /// has just undone), so it is excluded.
    /// </summary>
    private static string? DeclinedNote()
    {
        if (!KwinNightLightBus.SessionBusReachable()) return null;
        if (KwinNightLightBus.Probe() is not { Available: true } kwin) return null;
        if (KwinConfigFile.Read(KwinConfig.PriorActiveMarker) is not null) return null;
        if (!KwinConfig.IsOn(KwinConfigFile.Read(KwinConfig.ActiveKey))) return null;

        return kwin.KdeIsTinting
            ? "KDE's night light is on and tinting the screen — the app leaves it alone rather than overriding it, so its own blue-light filter stays hidden. Turn night light off in System Settings to use it."
            : "KDE's night light is switched on (on a schedule) — the app leaves it alone rather than overriding your schedule, so its own blue-light filter stays hidden. Turn night light off in System Settings to use it.";
    }

    /// <summary>
    /// Whether the compositor is a Wayland one — which is what decides the X11 link, and it is NOT the same
    /// question as "is X11 reachable". On this session both are true at once: <c>DISPLAY=:0</c> exists because
    /// Plasma starts XWayland, and <c>wayland-info</c> shows the session is Wayland. The compositor owns colour, so
    /// the X11 ramp is the wrong lever here however healthy xrandr looks.
    /// </summary>
    private static bool IsWaylandSession()
        => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Equals("wayland", StringComparison.OrdinalIgnoreCase) == true
           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}
