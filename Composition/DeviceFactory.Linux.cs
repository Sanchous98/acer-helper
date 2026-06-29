using AcerHelper.Vendors.Generic;

namespace AcerHelper.Composition;

/// <summary>
/// Linux backend. No vendor-specific (e.g. Acer via sysfs) codec is wired yet, so it returns the
/// generic device (power-profiles-daemon profiles + autostart). A future vendor backend would be
/// detected here and fall back to generic.
/// </summary>
public static class DeviceFactory
{
    public static IDevice Create() => GenericDevice.Create();
}
