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
        // nvapi64.dll), which hides the GPU section.
        if (NvidiaGpu.TryCreate() is { } gpu) { GpuOverclock = gpu; Own(gpu); }

        var clamshell = new Clamshell();
        if (!clamshell.Supported) { clamshell.Dispose(); return; }   // its ctor subscribed to the STATIC
        // SystemEvents — an undisposed reject would stay pinned (and handled) for the process lifetime.
        Clamshell = clamshell; Own(clamshell);
    }
}
