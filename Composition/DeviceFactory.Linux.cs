using AcerHelper.Os;

namespace AcerHelper.Composition;

/// <summary>
/// Linux backend. No vendor-specific (e.g. Acer/Linuwu) codec is wired yet, so this returns the
/// **generic device**: performance profiles via the ACPI <c>platform_profile</c> sysfs interface
/// (works on Dell, ThinkPad, Acer-via-acer-wmi, etc.) plus run-at-login. Other features are absent
/// (null ports) and the UI hides them. A future Acer-on-Linux backend would be detected and
/// preferred here, falling back to this.
/// </summary>
public static class DeviceFactory
{
    public static IDevice Create()
    {
        var (_, product) = MachineInfo.Read();
        var profiles = new SysfsPowerProfiles();

        return new CompositeDevice
        {
            VendorName = string.IsNullOrWhiteSpace(product) ? "Generic" : product!,
            StatusMessage = profiles.Available ? null : "No ACPI platform_profile — limited controls.",
            PowerProfiles = profiles.Available ? profiles : null,
            Autostart = new Autostart(),
        };
    }
}
