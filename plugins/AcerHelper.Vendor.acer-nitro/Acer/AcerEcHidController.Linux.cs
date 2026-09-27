using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Linux transport for the Acer EC controller: hidraw directly, no HID library — the same approach and the same
// reason as EneHidController.Linux.cs (this controller sits on HID-over-I2C, which HidSharp's Linux enumeration
// never lists). One hidraw node covers all of a device's collections, so matching the parent hid device's
// HID_ID (bus:vendor:product) is enough; the report id in byte 0 selects the vendor collection's report.
// Reaching /dev/hidrawN without root needs an EXPLICIT grant — an I2C-HID controller gets no uaccess ACL by
// default. See EneHidController.Linux.cs for why, and packaging/60-acer-helper.rules for the rule that grants it.
//
// NOTE: untested on Linux hardware. A missing or unwritable node degrades to Available = false and the profile
// path keeps its previous behaviour, so a wrong guess means "no EC envelope control", never a bad write.
// See docs/power-an18-61.md.
internal sealed partial class AcerEcHidController
{
    private FileStream? _dev;

    private partial bool OpenTransport()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/hidraw"))
            {
                if (!IsAcerNode(Path.Combine(dir, "device/uevent"))) continue;
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
    // SHARED, because the Turbo key arrives on this very device and AcerHotkeys.Linux.cs has to find the same
    // node: two copies of the vendor/product test is exactly how the two ends of a device identity drift apart.
    internal static bool IsAcerNode(string ueventPath)
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

    // Runs on the controller's writer thread. Re-opens the node if it was dropped, and drops it again on
    // failure so the next write re-opens.
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

    // SEND-then-GET, the same order the codec requires: HIDIOCSFEATURE pushes the request, then
    // HIDIOCGFEATURE(len) = _IOC(WRITE|READ, 'H', 0x07, len) reads the reply back into the buffer (byte 0 is
    // the report id on the way in and on the way out). Called from the caller's slow pool schedule (inside the
    // controller's _ioGate), never the UI thread. On any failure drop the node so the next call re-opens.
    //
    // UNTESTED ON LINUX HARDWARE (the codec is verified on Windows — see docs/power-an18-61.md). A failure
    // here returns false and the caller hides the row; it can never fabricate a source.
    private partial bool ReadFeature(byte[] send, byte[] reply)
    {
        if (_dev == null && !OpenTransport()) return false;
        try
        {
            var set = 0xC0000000u | ((uint)send.Length << 16) | ('H' << 8) | 0x06;
            if (Ioctl(_dev!.SafeFileHandle, set, send) < 0) { _dev?.Dispose(); _dev = null; return false; }
            var get = 0xC0000000u | ((uint)reply.Length << 16) | ('H' << 8) | 0x07;
            if (IoctlGet(_dev.SafeFileHandle, get, reply) >= 0) return true;
        }
        catch { /* fall through to drop the node */ }
        _dev?.Dispose(); _dev = null;
        return false;
    }

    private partial void CloseTransport() => _dev?.Dispose();

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle fd, nuint request, [In] byte[] data);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlGet(SafeFileHandle fd, nuint request, [In, Out] byte[] data);
}
