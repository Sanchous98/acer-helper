using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// THE LINUX HALF: the only things that touch this machine — the presence probe and the busctl runner the ports
// are built with. Both live here rather than in AsusdPlatform.cs because this file is compiled into the Linux
// build ONLY (AcerHelper.csproj <Compile Remove="**/*.Linux.cs"> under the Windows TFM), and the parts worth
// testing were kept out of it on purpose: the vocabulary, the parsers, the argument lists and the ownership
// policy are in the un-suffixed files beside this one, where the suite can reach them.
//
// THE ONE QUESTION THIS FILE ASKS is whether asusd owns its name on the SYSTEM bus right now — the same
// `busctl --system status NAME` test CardwireGpuAccess.Linux.cs uses, and for the same reason: it asks the bus
// rather than the filesystem, so an installed-but-crashed daemon reads as absent and the generic ports the base
// device wired simply stay. Busctl.Call already waits 4 s and reports a stuck process as a failure, which reads
// here as "not present" — the same answer as a daemon that is not there.
//
// INTERFACE PRESENCE for the Phase-2 peripherals (Backlight, Aura) is an INTROSPECTION test, because those
// interfaces have no supported_properties() of their own: the interface either appears on the object or it does
// not. The per-property READ then decides usability, so a present-but-broken property still falls back.

internal static class AsusdHost
{
    /// <summary>Whether asusd is on the system bus. Nothing is built or written when this is false.</summary>
    internal static bool IsPresent()
    {
        var (code, _) = Busctl.Call(Asusd.StatusArguments());
        return code == 0;
    }

    internal static AsusdPlatformPort CreateProfilesPort() => new(Busctl.Call);

    internal static BatteryToggle? CreateChargeLimit() => AsusdChargeLimit.TryCreate(Busctl.Call);

    /// <summary>The asusd plain-backlight port, or null when the interface is not on the object. NOT wired to the
    /// keyboard-brightness slot — see AsusdBacklight.cs for why (the asusd interface is the display panel).</summary>
    internal static AsusdBacklightPort? CreateBacklightPort()
    {
        var (code, output) = Busctl.Call(Asusd.IntrospectArguments(Asusd.BasePath));
        if (code != 0 || !Asusd.HasInterface(output, AsusdBacklight.Interface)) return null;
        return new AsusdBacklightPort(Busctl.Call);
    }

    /// <summary>Every live Aura device assembled into one <see cref="RgbDevice"/>, or null when asusd exposes
    /// none. Object paths are discovered from <c>busctl tree</c> and confirmed by introspection (per-device
    /// paths, see AsusdAura.cs). Concrete rather than <see cref="IRgbDevice"/> so the backend can Own() it.</summary>
    internal static RgbDevice? CreateAuraDevice()
    {
        var (treeCode, tree) = Busctl.Call(Asusd.TreeArguments());
        if (treeCode != 0) return null;

        var controllers = new List<IRgbController>();
        foreach (var path in AsusdAura.DevicePaths(tree))
        {
            var (introspectCode, introspect) = Busctl.Call(Asusd.IntrospectArguments(path));
            if (introspectCode != 0 || !Asusd.HasInterface(introspect, AsusdAura.Interface)) continue;
            if (AsusdAuraController.TryCreate(Busctl.Call, path) is { } controller) controllers.Add(controller);
        }
        return controllers.Count > 0 ? new RgbDevice([.. controllers]) : null;
    }

    /// <summary>The read-only FanCurves reader, over the SAME profile wire form the profile port observed. The
    /// profile signature has to be re-read here because this port is independent of that one.</summary>
    internal static AsusdFanCurvesReader? CreateFanCurvesReader()
    {
        var (code, output) = Busctl.Call(Asusd.GetPropertyArguments(
            Asusd.PlatformInterface, Asusd.PlatformProfileProperty));
        if (code != 0) return null;
        var signature = Asusd.SignatureOf(output);
        return signature.Length > 0 ? new AsusdFanCurvesReader(Busctl.Call, signature) : null;
    }
}
