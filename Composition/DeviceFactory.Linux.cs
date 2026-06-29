namespace AcerHelper.Composition;

// Linux: no vendor-specific (e.g. Acer via sysfs) codec is wired yet, so there is no vendor device
// and the shared Create() falls back to the generic device. A future vendor backend goes here.
public static partial class DeviceFactory
{
    private static partial (IDevice? Device, string? Reason) CreateVendorDevice() => (null, null);
}
