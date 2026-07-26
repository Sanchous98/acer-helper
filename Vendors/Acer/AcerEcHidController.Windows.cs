using HidSharp;

namespace AcerHelper.Vendors.Acer;

// Windows transport for the Acer EC controller: HidSharp over the Win32 HID API, same shape as
// EneHidController.Windows.cs. Enumeration by VID/PID plus the feature-report length picks the right interface:
// this device exposes nine collections and only the vendor one (usage page 0xFF05) has 65-byte feature reports
// — the others report 0, 3, 4 or 6 — so the length alone is unambiguous. Lazily opened.
internal sealed partial class AcerEcHidController
{
    private HidDevice? _device;
    private HidStream? _stream;

    private partial bool OpenTransport()
    {
        _device = FindDevice();
        return _device != null;
    }

    // Runs on the controller's writer thread (never the UI thread — see AcerEcHidController.Apply). The
    // synchronous SetFeature can block on a contended HID-over-I2C bus; that only stalls the worker. On any
    // failure drop the stream so the next write re-opens a fresh handle.
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
