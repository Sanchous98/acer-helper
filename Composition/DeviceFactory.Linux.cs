using AcerHelper.Os;

namespace AcerHelper.Composition;

/// <summary>
/// Linux backend (skeleton). The architecture is in place — it returns an <see cref="IDevice"/>
/// whose feature ports are null until real sysfs (Linuwu-Sense)/evdev/hidraw codecs are added as
/// <c>*.Linux.cs</c> files. Run-at-login is implemented for real.
/// </summary>
public static class DeviceFactory
{
    public static IDevice Create() => new CompositeDevice
    {
        VendorName = "Linux",
        StatusMessage = "Linux hardware control is not implemented yet.",
        Autostart = new Autostart(),
    };
}
