using HidSharp;

namespace AcerHelper.Vendors.Acer;

// Windows transport for the ENE controller: HidSharp over the Win32 HID API. Enumeration by VID/PID plus
// the feature-report length picks the right interface among the device's collections. Lazily opened.
internal sealed partial class EneHidController
{
    private HidDevice? _device;
    private HidStream? _stream;

    private partial bool OpenTransport()
    {
        _device = FindDevice();
        return _device != null;
    }

    private partial bool SetFeature(byte[] report)
    {
        if (_device == null) return false;
        try
        {
            (_stream ??= _device.Open()).SetFeature(report);
            return true;
        }
        catch { return false; }
    }

    private static HidDevice? FindDevice()
    {
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(VID, PID))
                try { if (d.GetMaxFeatureReportLength() == FeatureLen) return d; }
                catch { /* skip interfaces we can't query */ }
        }
        catch { /* no device */ }
        return null;
    }

    public partial void Dispose() => _stream?.Dispose();
}
