using AcerHelper.Vendors.Generic;

namespace AcerHelper.Composition;

/// <summary>
/// Composition root. The shared flow is "try the OS's vendor backend; otherwise the generic device
/// (carrying the reason the vendor backend was unavailable)". The vendor detection and wiring — the
/// only OS-specific part — is supplied by <see cref="CreateVendorDevice"/> in DeviceFactory.*.cs.
/// </summary>
public static partial class DeviceFactory
{
    public static IDevice Create()
    {
        var (device, reason) = CreateVendorDevice();
        return device ?? GenericDevice.Create(reason);
    }

    /// <summary>OS-specific: detect + assemble the vendor device, or <c>(null, reason)</c> if none matched.</summary>
    private static partial (IDevice? Device, string? Reason) CreateVendorDevice();
}
