namespace AcerHelper.Vendors.Generic;

// Generic Windows common capabilities: performance profiles via the power-mode overlay, blue-light gamma,
// and clamshell. (Battery telemetry + autostart are wired cross-platform in GenericDevice.cs.) A vendor
// backend extends this and overrides/adds its own.
public partial class GenericDevice
{
    partial void InitPlatform()
    {
        var profiles = new OverlayPowerProfiles();
        if (profiles.Available) PowerProfiles = profiles;

        DisplayTint = new DisplayTint();

        // NVIDIA dGPU clock overclock via NvAPI — cross-vendor (any laptop with an NVIDIA dGPU + driver), so it
        // lives here in the generic base rather than a vendor backend. Null on AMD/Intel-only machines (no
        // nvapi64.dll), which hides the GPU section. (CPU power is wired later in FinalizeCompositionPlatform —
        // it depends on the FINAL performance-profile backend, which a vendor's InitVendor may still replace.)
        if (NvidiaGpu.TryCreate() is { } gpu) { GpuOverclock = gpu; Own(gpu); }

        var clamshell = new Clamshell();
        if (!clamshell.Supported) { clamshell.Dispose(); return; }   // its ctor subscribed to the STATIC
        // SystemEvents — an undisposed reject would stay pinned (and handled) for the process lifetime.
        Clamshell = clamshell; Own(clamshell);
    }

    // CPU power management via the Windows power-mode overlay — the one driverless CPU-power knob (no ring-0
    // undervolt/PPT; Acer exposes no WMI power path). Wired here (after the vendor backend has finalized the
    // profile port) rather than in InitPlatform, and ONLY when the performance profiles are NOT themselves the
    // Windows overlay: OverlayCpuPower and OverlayPowerProfiles drive the SAME overlay with the SAME GUIDs, so
    // if the profile picker already IS the overlay (generic laptop, or a vendor whose WMI/BIOS profile path was
    // unavailable) a CPU-power control would fight it — and, since the per-mode key is then the overlay GUID,
    // corrupt the per-profile store. So it's an independent axis only when a vendor WMI/EC profile port took
    // over. Null (section hidden) otherwise, and on an OS without the overlay API.
    partial void FinalizeCompositionPlatform()
    {
        if (PowerProfiles is OverlayPowerProfiles) return;
        CpuPower = OverlayCpuPower.TryCreate();
    }
}
