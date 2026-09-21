using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Acer;

/// <summary>
/// THE TURBO KEY'S TWO BYTES, IN ONE PLACE, BECAUSE BOTH HALVES DECODE THE SAME DEVICE.
///
/// The key is not a keyboard scancode on either OS. Windows reads it as a HID input report on the Acer vendor
/// usage page (0x0088, usage 0x01) and matches the first two report bytes; measured on Linux, the same key
/// arrives as the same two bytes on the Acer HID device itself (VID 0x1025 / PID 0x174B — the device the app
/// already talks to for the power envelope):
///
/// <code>
///   /dev/hidraw8: report 04 85 ff      (two presses, 2026-09-21 08:43:11 and 08:43:15)
/// </code>
///
/// WHY THIS FILE EXISTS RATHER THAN A CONSTANT IN EACH HALF. The two halves are separate files by the repo's OS
/// split (<c>AcerHotkeys.Windows.cs</c> / <c>.Linux.cs</c>) and only one of them is compiled per TFM, so a
/// signature written twice could drift with nothing to notice: the key would simply stop working on one OS, and
/// the Windows half is the reference the Linux one has to match. The bytes therefore live here, in an
/// un-suffixed file both halves and the test project can see, and the guard in
/// <c>AcerHotkeyReportsTests</c> reads the two source files to keep them pointing at this one.
///
/// WHAT IS NOT TRUE ON LINUX, and used to be claimed in the reader's comment: that the kernel owns this key.
/// Mainline acer-wmi's <c>cycle_gaming_thermal_profile</c> never applies to it, because the key is the vendor
/// HID report and not a WMI event at all — measured with the key pressed on 2026-09-21: no evdev key on the AT
/// keyboard, no <c>acer_wmi</c> line in the journal, and no movement of the ACPI interrupt counters, while the
/// hidraw report above arrived on every press.
///
/// The third byte is deliberately NOT part of the signature: the capture shows <c>0xff</c> there, Windows
/// matches only the first two, and pinning a byte neither side has a meaning for would make the halves disagree
/// on evidence nobody has.
/// </summary>
internal static class AcerHotkeyReports
{
    /// <summary>The Turbo key's first two report bytes — Acer's vendor HID report, matched identically on both
    /// OSes (Windows: <c>AcerHotkeys.Windows.cs</c>'s RawInput HID branch, which reads the same fields).</summary>
    internal const byte TurboByte0 = 0x04;
    internal const byte TurboByte1 = 0x85;

    /// <summary>The action a report stands for, or null when it is not a key this app knows. Total: any length,
    /// including an empty or one-byte report, answers null rather than throwing — the caller is a read loop on a
    /// device that also carries the power-envelope traffic, so unknown reports are the normal case, not an error.
    /// </summary>
    internal static HotkeyAction? Decode(ReadOnlySpan<byte> report)
        => report.Length >= 2 && report[0] == TurboByte0 && report[1] == TurboByte1
            ? HotkeyAction.TogglePerformance
            : null;
}
