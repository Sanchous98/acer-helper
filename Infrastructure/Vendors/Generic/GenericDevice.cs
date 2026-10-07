using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using GenericBattery = AcerHelper.Infrastructure.Vendors.Generic.BatteryInfo;

namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>
/// The generic laptop backend: it fills the machine (<see cref="Device"/>) with the capabilities any laptop
/// exposes through standard OS APIs — performance profiles, battery telemetry, autostart, and (where the
/// OS/hardware allow) sensors, clamshell and blue-light. This is the base every vendor backend EXTENDS:
/// <c>AcerDevice : GenericDevice</c> inherits these common ports and overrides/adds only its proprietary ones.
/// The cross-platform common wiring is in the constructor and the per-OS bits in <see cref="InitPlatform"/>
/// (GenericDevice.Windows.cs / .Linux.cs).
///
/// IT IS A BACKEND, NOT THE MACHINE. The ports live on <see cref="Device"/> and are inherited rather than
/// re-declared here, because they are not this backend's property: a machine has ports, and a backend is one of
/// the things that can fill them. What this class owns is the PROBING — which of those slots a generic OS can
/// answer for on this platform — and the teardown of what it created.
/// </summary>
public partial class GenericDevice : Device
{
    public GenericDevice(string? status = null)
    {
        StatusMessage = status;
        // Cross-platform telemetry, and the ONLY property the base can offer: whether it exists at all is the
        // OS's answer (null on a desktop / no battery), and the machine's charging controls — none of which the
        // generic OS surface can promise — are added by a vendor's InitVendor one property at a time.
        if (GenericBattery.TryCreate() is { } info) Battery.Telemetry = info.Read;
        Autostart = new Autostart();                // cross-platform (.desktop on Linux, scheduled task on Windows)
        InitPlatform();
    }

    /// <summary>Per-OS common capabilities (profiles, sensors, clamshell, blue-light) — the .Windows/.Linux part.</summary>
    partial void InitPlatform();

    /// <summary>Finish composition once ALL ports are final — in particular after a vendor backend has had its
    /// chance (in InitVendor) to REPLACE the generic profiles with a WMI/BIOS port. Called by the composition
    /// root (DeviceFactory) after the device is fully constructed, so it can make decisions that depend on the
    /// final port set (e.g. the overlay-CPU-power axis is only valid when the profiles are NOT themselves the
    /// Windows overlay). Per-OS work is in <see cref="FinalizeCompositionPlatform"/>.</summary>
    internal void FinalizeComposition() => FinalizeCompositionPlatform();

    partial void FinalizeCompositionPlatform();
}
