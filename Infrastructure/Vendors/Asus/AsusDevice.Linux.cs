using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// Linux: probe asusd and let it take over the ports it actually owns, keeping the generic ones otherwise. This
// file is the I/O half only — the vocabulary, the parsers, the argument lists and the ownership policy are in the
// un-suffixed files beside it, where the suite can reach them (this file is not compiled into the test TFM).
//
// THE SHAPE IS GRACEFUL DEGRADATION, and it is one direction only: asusd REPLACES a generic port, never the
// other way round, and only when its own property is usable. A machine without asusd keeps:
//   * profiles  — the power-profiles-daemon port, or the raw platform_profile sysfs port, wired by GenericDevice;
//   * the charge cap — the standard charge_control_end_threshold sysfs node, wired by GenericDevice;
//   * the keyboard backlight — the kernel LED class, wired by GenericDevice.
// So the base constructor is the fallback and this method only ever overrides it.
public sealed partial class AsusDevice
{
    partial void InitVendor()
    {
        var present = AsusdHost.IsPresent();

        // ---- Phase 1: profiles + charge cap ----
        var profiles = present ? AsusdHost.CreateProfilesPort() : null;
        var charge = present ? AsusdHost.CreateChargeLimit() : null;

        var facts = new AsusdPlatformFacts(
            AsusdPresent: present,
            ProfilesAvailable: profiles?.Available == true,
            ChargeLimitUsable: charge != null);

        AsusWiring.Apply(this, facts, profiles, charge);

        if (!present) return;

        // ---- Phase 2: RGB (Aura) ----
        // Additive, not a replacement: a laptop with no separate lighting backend simply gains the Aura zones,
        // and one whose Aura interface is absent keeps whatever it had. Owned because RgbDevice disposes the
        // controllers it was assembled from.
        var aura = AsusdHost.CreateAuraDevice();
        AsusWiring.ApplyAura(this, present, aura != null, aura);
        if (aura != null) Own(aura);

        // ---- Phase 3b: the GPU MUX (shared IGpuMux) ----
        // Built from the same AsusArmoury port as the other firmware knobs; the MUX adapter reuses the port's
        // validation, single-flight and Gpu-kind queueing (asusd applies it at shutdown = the next restart). The
        // port's own consent delegate is a pass-through here because the MUX's gate is the UI's confirmation
        // prompt (<see cref="AsusdGpuMux"/> is only reachable through the ViewModel, which asks first).
        if (AsusdArmouryPort.TryCreate(Busctl.Call, _ => true) is { } armoury)
            GpuMux = new AsusdGpuMux(armoury);

        // ---- Phase 2, implemented but deliberately NOT wired ----
        //
        // Backlight: asusd's xyz.ljones.Backlight is the DISPLAY panel (rog-platform binds Primary to
        // intel_backlight), NOT the keyboard backlight the KeyboardBrightness slot is. Binding it would replace a
        // working keyboard-LED slider with a screen-brightness control under a keyboard label. The correct port
        // exists and is tested (AsusdBacklightPort); the one-line enable, if a display-brightness surface is ever
        // added, is:
        //     var bl = AsusdHost.CreateBacklightPort();
        //     if (AsusdOwnership.TakeOverBacklight(present, bl != null)) KeyboardBrightness = bl;
        // See AsusdBacklight.cs and docs/asus-support.md.
        //
        // FanCurves: read-only in Phase 2 (AsusdFanCurvesReader); there is no consumer for the curve model until
        // the write/IFanControl work lands in Phase 3, and no write is made here.
        //
        // one_shot_full_charge: a distinct momentary action (AsusdOneShotCharge), not mapped to Battery.Calibration
        // — it has no latch to be a toggle. Surfacing it needs an owner decision about where the action row lives.
    }
}
