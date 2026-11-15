using HidSharp;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Windows transport for the ENE controller: HidSharp over the Win32 HID API. Enumeration by VID/PID plus
// the feature-report length picks the right interface among the device's collections. Lazily opened.
internal sealed partial class EneHidController
{
    private HidDevice? _device;
    private HidStream? _stream;

    // The device was not there last time we looked, and this is the moment we may look again. A wedged ENE (a
    // Code 10 parent: the firmware handshake failed) stays absent until the machine is power-cycled, so the
    // writer would otherwise re-enumerate the whole HID tree on every queued report — and the re-apply burst
    // queues several per action. Five seconds is short enough that a controller that comes back is picked up by
    // the next action, and long enough that a run of writes costs one enumeration, not one each.
    private const long ProbeBackoffMs = 5000;
    private long _nextProbeTicks;

    private partial bool OpenTransport()
    {
        _device = FindDevice();
        return _device != null;
    }

    // The device can be absent at construction and appear later (or the other way round): the first write after a
    // failed probe RE-ENUMERATES, so a controller that was wedged at startup but recovered — or one HidSharp could
    // not see yet because its device list was still loading — starts working without an app restart. That is the
    // whole recovery path now that the zones exist regardless of the probe.
    private bool EnsureDevice()
    {
        if (_device != null) return true;
        var now = Environment.TickCount64;
        if (now < _nextProbeTicks) return false;
        if (OpenTransport()) return true;
        _nextProbeTicks = now + ProbeBackoffMs;
        return false;
    }

    // Runs on the controller's writer thread (never the UI thread — see EneHidController.SetFeature). The
    // synchronous SetFeature can block on a contended HID-over-I2C bus; that's fine here, it only stalls the
    // worker. On any failure drop the stream so the next write re-opens a fresh handle — otherwise a bad
    // handle opened at boot-with-display (bus busy) would stick forever (the old `_stream ??=` never re-opened).
    private partial bool WriteFeature(byte[] report)
    {
        if (!EnsureDevice()) return false;
        try
        {
            (_stream ??= _device!.Open()).SetFeature(report);
            return true;
        }
        catch
        {
            _stream?.Dispose(); _stream = null;
            // The device itself may be gone (a re-enumerated node keeps its instance id but the handle dies), so
            // let the next write look for it again rather than retrying a dead HidDevice forever.
            _device = null;
            return false;
        }
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
