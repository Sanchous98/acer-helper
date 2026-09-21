using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Generic Linux common capabilities: performance profiles via power-profiles-daemon (or the sysfs
// platform_profile fallback), temperature/RPM monitoring via hwmon, and — where the kernel allows it —
// fan PWM control. (Battery telemetry + autostart are wired cross-platform in GenericDevice.cs.) A vendor
// backend extends this and overrides/adds its own.
public partial class GenericDevice
{
    partial void InitPlatform()
    {
        var (_, product) = MachineInfo.Read();
        if (!string.IsNullOrWhiteSpace(product)) VendorName = product!;

        IPowerProfiles? profiles = null;
        var ppd = new PpdPowerProfiles();          // preferred: switches via polkit (no root)
        if (ppd.Available) profiles = ppd;
        else { var sysfs = new SysfsPowerProfiles(); if (sysfs.Available) profiles = sysfs; }
        PowerProfiles = profiles;
        if (profiles == null && StatusMessage == null)
            StatusMessage = "No power-profile interface found — limited controls.";

        Sensors = HwmonSensors.TryCreate();        // RPM + temps (read-only, universal)

        var fans = HwmonFanControl.TryCreate();    // PWM control only where the kernel allows it (usually null)
        if (fans != null) { FanControl = fans; Own(fans); }

        // Standard-Linux extras (no vendor tool needed):
        Battery.ChargeLimit = SysfsChargeLimit.TryCreate();  // charge_control_end_threshold (many laptops)
        KeyboardBrightness = SysfsKbdBacklight.TryCreate(); // plain kbd backlight via kernel LED class

        // NVIDIA dGPU clock overclock via NVML (libnvidia-ml.so.1) — cross-vendor (any laptop with an NVIDIA dGPU +
        // driver), so it lives here in the generic base rather than a vendor backend. The Linux twin of the Windows
        // NvAPI wiring and the same shape on purpose: null unless the library loads, NVML initialises, a device is
        // VISIBLE and the offset APIs resolve, which hides the GPU section everywhere else. It is re-probed on each
        // composition and holds no state, so a dGPU the access broker has not handed out yet — the case on this
        // machine while the grant is pending — simply shows no section until one appears. See
        // docs/nvidia-gpu-oc-linux.md.
        if (NvidiaGpu.TryCreate() is { } gpu) { GpuOverclock = gpu; Own(gpu); }

        // AMD Curve Optimizer (CPU undervolt) over the SMU mailboxes — cross-vendor for the same reason the GPU
        // offsets are on Windows: it depends on the CPU and on an already-loaded kernel driver, not on the laptop's
        // vendor. Null unless this is a CPU whose mailbox layout is known AND the ryzen_smu driver is present,
        // agrees about the part and leaves its smn node writable — which hides the section on every other machine
        // (the common case, since the driver ships separately from the app). No install offer here, deliberately:
        // the driver is a distribution package rather than a payload this app carries, so there is nothing to
        // install, and what the app CAN grant — the node permissions — is part of the existing hardware-access
        // install (Infrastructure/HardwareAccess.cs). See docs/curve-optimizer-linux.md.
        if (RyzenCurveOptimizer.TryCreate() is { } co) { CurveOptimizer = co; Own(co); }

        // Blue-light: a CHAIN of adapters, each falling through to the next where its environment is not there —
        // X11 gamma ramp, then KWin's own night light by configuration, then GNOME's, then wlr-gamma-control. Null
        // when this session offers none, so the section hides; the note is set only when the reason is the user's
        // own KDE night light, which the app must not override (see Infrastructure/Vendors/Generic/TintChain.Linux.cs).
        //
        // Owned, and that is load-bearing rather than tidiness: the chosen link may be KWin's or GNOME's night
        // light driven by writing the user's own settings, and those links put them back in their Dispose. What
        // the device is handed is the CHAIN (LinuxTint.Create returns the concrete type for exactly this reason),
        // so owning it is what carries the release through to the link that holds them.
        var (tint, tintNote) = LinuxTint.Create();
        if (tint != null) { DisplayTint = tint; Own(tint); }   // release on exit
        else if (tintNote != null && StatusMessage == null) StatusMessage = tintNote;

        // No clamshell on Linux: the DE's power manager owns the lid (e.g. KDE PowerDevil holds a block
        // inhibitor on handle-lid-switch and suspends per its own config), so there's no DE-agnostic lever
        // to override it — the port stays null and the UI hides it. (Windows keeps clamshell.)
    }
}
