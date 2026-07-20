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

    // Runs on the controller's writer thread (never the UI thread — see EneHidController.SetFeature). The
    // synchronous SetFeature can block on a contended HID-over-I2C bus; that's fine here, it only stalls the
    // worker. On any failure drop the stream so the next write re-opens a fresh handle — otherwise a bad
    // handle opened at boot-with-display (bus busy) would stick forever (the old `_stream ??=` never re-opened).
    private partial bool WriteFeature(byte[] report)
    {
        if (_device == null) return false;
        try
        {
            (_stream ??= _device.Open()).SetFeature(report);
            return true;
        }
        catch { _stream?.Dispose(); _stream = null; return false; }
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

    private partial void CloseTransport() => _stream?.Dispose();
}
