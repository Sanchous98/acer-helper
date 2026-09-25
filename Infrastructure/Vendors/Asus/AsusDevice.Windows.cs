using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// WINDOWS: the ASUS ATK ACPI backend (transport: AsusAtkChannel; protocol: AsusAtk). Every port is built over
// the same control delegate, and every one is CAPABILITY-PROBED — a port whose device does not answer is simply
// absent, so the generic Windows ports (the OS power-mode overlay, NvAPI, the AMD undervolt, clamshell) stand.
//
// WHAT IS AND IS NOT WIRED here, and why:
//   * profiles, charge limit, panel overdrive, the GPU MUX (shared IGpuMux) and the TUF keyboard RGB are wired;
//   * fan curves are implemented (AsusWindowsFanCurvesReader/Writer) but NOT wired to IFanControl, the same
//     decision as Linux: a firmware temperature-point curve is not the app's mode/speed port;
//   * ROG per-key Aura HID is not implemented (no verified codec) and is refused — see AsusAtkRgb.cs;
//   * no port is reachable from the refresh loop: every write is an explicit call, the MUX through the Tuning
//     card's confirmation, the rest through the rows that own them.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs).
public sealed partial class AsusDevice
{
    partial void InitVendor()
    {
        var channel = AsusAtkChannel.TryOpen();
        if (channel == null) return;   // not an ASUS ATK machine: the generic Windows ports stand
        Own(channel);                  // the ports below capture its control delegate, so it must outlive them

        var atk = new AsusAtkDevice(channel.Control);
        var model = AsusModels.Detect(Product);

        if (AsusWindowsProfiles.TryCreate(atk, model.Vivo) is { } profiles) PowerProfiles = profiles;

        if (AsusWindowsChargeLimit.TryCreate(atk) is { } limit) Battery.ChargeLimit = limit;

        if (AsusWindowsPanelOverdrive.TryCreate(atk) is { } overdrive)
            Declare(new FlagSetting { Key = AsusWindowsPanelOverdrive.SettingKey, Port = overdrive });

        // The shared MUX port is attached even when unsupported, so the Tuning card can state the refusal rather
        // than the capability vanishing (the same shape the Acer path uses).
        GpuMux = AsusWindowsGpuMux.Create(atk, model.Vivo);

        if (AsusAtkRgbController.TryCreate(atk) is { } rgb)
        {
            var lighting = new RgbDevice(rgb);
            Lighting = lighting;
            Own(lighting);
        }
    }
}
