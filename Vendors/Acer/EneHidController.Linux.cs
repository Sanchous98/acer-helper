using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AcerHelper.Vendors.Acer;

// Linux transport for the ENE controller: hidraw directly, no HID library. The controller is not
// necessarily USB — on the Nitro AN18-61 (and other recent models) it sits on HID-over-I2C, which
// HidSharp's Linux enumeration never lists. hidraw covers every HID bus: we find our node by the parent
// hid device's HID_ID (bus:vendor:product) in sysfs and push feature reports with HIDIOCSFEATURE.
// Reaching /dev/hidrawN without root relies on the desktop's uaccess ACL (present for built-in HID) or a
// udev rule.
internal sealed partial class EneHidController
{
    private FileStream? _dev;

    private partial bool OpenTransport()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/hidraw"))
            {
                if (!IsEne(Path.Combine(dir, "device/uevent"))) continue;
                try
                {
                    _dev = File.Open($"/dev/{Path.GetFileName(dir)}", FileMode.Open, FileAccess.ReadWrite);
                    return true;
                }
                catch { /* no permission on this node — try the next match */ }
            }
        }
        catch { /* no hidraw class -> no device */ }
        return false;
    }

    // uevent of the parent hid device carries HID_ID=<bus>:<vendor>:<product> (8 hex digits each).
    private static bool IsEne(string ueventPath)
    {
        try
        {
            foreach (var line in File.ReadLines(ueventPath))
                if (line.StartsWith("HID_ID=", StringComparison.Ordinal))
                    return line.EndsWith($":{VID:X8}:{PID:X8}", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* unreadable -> not ours */ }
        return false;
    }

    // Runs on the controller's writer thread (never the UI thread — see EneHidController.SetFeature). Re-opens
    // the hidraw node if it was dropped, and drops it again on failure so the next write re-opens — a node
    // opened in a bad state at boot-with-display (bus busy) would otherwise stay broken until restart.
    private partial bool WriteFeature(byte[] report)
    {
        if (_dev == null && !OpenTransport()) return false;
        try
        {
            // HIDIOCSFEATURE(len) = _IOC(WRITE|READ, 'H', 0x06, len); the report id is byte 0 of the buffer.
            var request = 0xC0000000u | ((uint)report.Length << 16) | ('H' << 8) | 0x06;
            if (Ioctl(_dev!.SafeFileHandle, request, report) >= 0) return true;
        }
        catch { /* fall through to drop the node */ }
        _dev?.Dispose(); _dev = null;
        return false;
    }

    private partial void CloseTransport() => _dev?.Dispose();

    // No device-node restart on Linux: hidraw re-opens per write (WriteFeature re-runs OpenTransport when the
    // node was dropped), and the display-contended-bus corruption this works around is a Windows/this-model
    // issue. Kept as a no-op so the shared writer loop can call it unconditionally.
    private partial void TryReinitTransport() { }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle fd, nuint request, [In] byte[] data);
}
