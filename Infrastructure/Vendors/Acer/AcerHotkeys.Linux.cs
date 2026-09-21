using System.IO;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Acer;

/// <summary>
/// Acer special keys on Linux, read from the TWO places this firmware puts them — they are not the same kind of
/// event, and that is the whole reason both are here.
///
/// **Nitro/PredatorSense (launcher).** atkbd translates its scancode (E0 75 — the same marker the Windows
/// RawInput path decodes) to KEY_PRESENTATION on the AT keyboard, which is an ordinary evdev key.
///
/// **Turbo.** It is NOT an evdev key and NOT a WMI event: it is a VENDOR HID INPUT REPORT on the Acer HID device
/// (VID 0x1025 / PID 0x174B), the same device the app already writes the power envelope to. Measured on
/// 2026-09-21 with the key pressed, after three other paths had been ruled out on the same presses — no evdev
/// key on the AT keyboard, no <c>acer_wmi</c> line in the journal, no movement of the ACPI interrupt counters —
/// while every press arrived on hidraw:
///
/// <code>
///   /dev/hidraw8: report 04 85 ff      (08:43:11 and 08:43:15)
/// </code>
///
/// Those two bytes are the SAME signature Windows matches out of RawInput on the Acer vendor usage page (0x0088,
/// usage 0x01), which is why the signature lives in <see cref="AcerHotkeyReports"/> and both halves decode it
/// there. The previous version of this comment claimed the opposite — that mainline acer-wmi consumes the key
/// in-kernel through <c>cycle_gaming_thermal_profile</c> and that <c>HotkeyAction.TogglePerformance</c> was
/// therefore unreachable here. That was a reading of the module parameter, not of the key: the parameter is real
/// and defaults to Y, but it can never see this key, because the key is not a WMI event at all. It is now read
/// here, and the app toggles Turbo itself — the same action, and the same order of profiles, as on Windows.
///
/// ACCESS differs between the two sources, and neither needs a new rule: <c>/dev/input/event*</c> is root:input,
/// so the app's udev rule tags the AT keyboard with "uaccess" (ACL for the active local session only), while the
/// Acer hidraw node is already granted for the EC power-envelope path. A missing source is not a failure: the
/// port is offered as long as ONE of them opened, so a machine with only one of the two keys still gets it.
/// </summary>
internal sealed class AcerHotkeys : IHotkeys
{
    private const string KeyboardName = "AT Translated Set 2 keyboard";
    private const ushort EV_KEY = 0x01, KEY_PRESENTATION = 425;

    /// <summary>Room for one HID input report, including its report id — the same 65-byte frame the EC path
    /// writes as a feature report to this device.</summary>
    private const int ReportBufferSize = 65;

    private readonly FileStream? _kbd;   // AT keyboard: the Nitro key
    private readonly FileStream? _hid;   // Acer HID device: the Turbo key
    private volatile bool _closing;
    private DateTime _lastNitro, _lastTurbo;

    public event Action<HotkeyAction>? Pressed;
    public event Action? InputActivity;

    public static AcerHotkeys? TryCreate()
    {
        var kbd = TryOpenKeyboard();
        var hid = TryOpenAcerHid();
        Console.Error.WriteLine($"[hotkeys] keyboard={(kbd is null ? "none" : "open")} turbo={(hid is null ? "none" : "open")}");
        if (kbd is null && hid is null) return null;   // neither source: no hotkey port at all
        return new AcerHotkeys(kbd, hid);
    }

    private static FileStream? TryOpenKeyboard()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/input"))
            {
                var node = Path.GetFileName(dir);
                if (!node.StartsWith("event", StringComparison.Ordinal)) continue;
                if (Hwmon.ReadText(Path.Combine(dir, "device/name")) != KeyboardName) continue;
                try { return File.Open($"/dev/input/{node}", FileMode.Open, FileAccess.Read); }
                catch { /* no read access (udev rule not installed) — try the other source */ }
            }
        }
        catch { /* no input class -> no keyboard source */ }
        return null;
    }

    private static FileStream? TryOpenAcerHid()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/hidraw"))
            {
                var name = Path.GetFileName(dir);
                if (!AcerEcHidController.IsAcerNode(Path.Combine(dir, "device/uevent"))) continue;
                // TWO ATTEMPTS ON PURPOSE: the EC power-envelope controller holds this same node, and
                // File.Open's implicit FileShare.None conflicts with it — the reader was silently getting
                // nothing (measured: "[hotkeys] keyboard=open turbo=none" while the envelope writes worked).
                // Sharing the node is what hidraw allows; the exclusive share is not something this file needs.
                foreach (var share in new[] { FileShare.None, FileShare.ReadWrite })
                {
                    try
                    {
                        var stream = new FileStream($"/dev/{name}", FileMode.Open, FileAccess.Read, share);
                        Console.Error.WriteLine($"[hotkeys] turbo node {name} opened with FileShare.{share}");
                        return stream;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[hotkeys] {name} FileShare.{share} failed: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[hotkeys] hidraw enumeration failed: {ex.GetType().Name}"); }
        return null;
    }

    private AcerHotkeys(FileStream? kbd, FileStream? hid)
    {
        _kbd = kbd;
        _hid = hid;
        if (_kbd is not null) new Thread(NitroLoop) { IsBackground = true, Name = "acer-hotkeys" }.Start();
        if (_hid is not null) new Thread(TurboLoop) { IsBackground = true, Name = "acer-turbo-key" }.Start();
    }

    private void NitroLoop()
    {
        // struct input_event, 64-bit: 16-byte timestamp, u16 type, u16 code, s32 value (1 = key down).
        var buf = new byte[24];
        while (!_closing)
        {
            try { _kbd!.ReadExactly(buf); }
            catch { return; }   // device closed/gone -> the port goes quiet

            if (BitConverter.ToUInt16(buf, 16) != EV_KEY ||
                BitConverter.ToUInt16(buf, 18) != KEY_PRESENTATION ||
                BitConverter.ToInt32(buf, 20) != 1) continue;

            // The firmware auto-repeats the make/break while the key is held — act once per press.
            if (!Debounce(ref _lastNitro)) continue;

            // Subscriber exceptions must not kill the read loop (the Nitro key would stay dead for the
            // rest of the session) — only a device error above may end it.
            try
            {
                InputActivity?.Invoke();
                Pressed?.Invoke(HotkeyAction.ToggleWindow);   // AppController marshals to the UI thread
            }
            catch { /* handler bug — swallow, keep listening */ }
        }
    }

    /// <summary>The Turbo key: raw input reports from the Acer HID device. The same device carries the EC power
    /// envelope's traffic, so a report that is not a key is the ordinary case — <see cref="AcerHotkeyReports.Decode"/>
    /// answers null for those and the loop moves on. Pressed is raised on its own (no <c>InputActivity</c>): that
    /// event is the keyboard's, and the Windows half draws the same line between its two RawInput branches.</summary>
    private void TurboLoop()
    {
        var buf = new byte[ReportBufferSize];
        var logged = 0;
        Console.Error.WriteLine("[hotkeys] turbo loop started");
        while (!_closing)
        {
            int read;
            try { read = _hid!.Read(buf, 0, buf.Length); }
            catch (Exception ex) { Console.Error.WriteLine($"[hotkeys] turbo read failed: {ex.GetType().Name}"); return; }
            if (read <= 0) continue;

            if (logged < 20)
            {
                Console.Error.WriteLine($"[hotkeys] report {read} bytes: {Convert.ToHexString(buf.AsSpan(0, Math.Min(read, 6)))}");
                logged++;
            }

            if (AcerHotkeyReports.Decode(buf.AsSpan(0, read)) is not { } action) continue;
            if (!Debounce(ref _lastTurbo)) continue;

            Console.Error.WriteLine($"[hotkeys] decoded {action}");
            try { Pressed?.Invoke(action); }
            catch { /* handler bug — swallow, keep listening */ }
        }
    }

    /// <summary>Act once per press: the firmware repeats the make/break while a key is held, and a double fire
    /// would toggle Turbo twice — back to where it started, from the user's point of view a dead key.</summary>
    private static bool Debounce(ref DateTime last)
    {
        var now = DateTime.UtcNow;
        if (now - last < TimeSpan.FromMilliseconds(400)) return false;
        last = now;
        return true;
    }

    public void Dispose()
    {
        _closing = true;
        _kbd?.Dispose();
        _hid?.Dispose();
    }
}
