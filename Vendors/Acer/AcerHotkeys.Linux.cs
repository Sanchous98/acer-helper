using System.IO;
using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// Acer special keys on Linux via evdev. Only the Nitro/PredatorSense launcher key reaches userspace
/// here: atkbd translates its scancode (E0 75 — the same marker the Windows RawInput path decodes) to
/// KEY_PRESENTATION on the AT keyboard, while the Turbo key is consumed in-kernel by linuwu_sense
/// (cycle_gaming_thermal_profile), which cycles profiles itself. /dev/input/event* is root:input, so
/// the app's udev rule tags the AT keyboard with "uaccess" (ACL for the active local session only);
/// without that access the port simply isn't offered.
/// </summary>
internal sealed class AcerHotkeys : IHotkeys
{
    private const string DeviceName = "AT Translated Set 2 keyboard";
    private const ushort EV_KEY = 0x01, KEY_PRESENTATION = 425;

    private readonly FileStream _dev;
    private volatile bool _closing;
    private DateTime _lastFire;

    public event Action<HotkeyAction>? Pressed;
    public event Action? InputActivity;

    public static AcerHotkeys? TryCreate()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/input"))
            {
                var node = Path.GetFileName(dir);
                if (!node.StartsWith("event", StringComparison.Ordinal)) continue;
                if (Hwmon.ReadText(Path.Combine(dir, "device/name")) != DeviceName) continue;
                try { return new AcerHotkeys(File.Open($"/dev/input/{node}", FileMode.Open, FileAccess.Read)); }
                catch { /* no read access (udev rule not installed) — treat as absent */ }
            }
        }
        catch { /* no input class -> no hotkeys */ }
        return null;
    }

    private AcerHotkeys(FileStream dev)
    {
        _dev = dev;
        new Thread(ReadLoop) { IsBackground = true, Name = "acer-hotkeys" }.Start();
    }

    private void ReadLoop()
    {
        // struct input_event, 64-bit: 16-byte timestamp, u16 type, u16 code, s32 value (1 = key down).
        var buf = new byte[24];
        try
        {
            while (!_closing)
            {
                _dev.ReadExactly(buf);
                if (BitConverter.ToUInt16(buf, 16) != EV_KEY ||
                    BitConverter.ToUInt16(buf, 18) != KEY_PRESENTATION ||
                    BitConverter.ToInt32(buf, 20) != 1) continue;

                // The firmware auto-repeats the make/break while the key is held — act once per press.
                var now = DateTime.UtcNow;
                if (now - _lastFire < TimeSpan.FromMilliseconds(400)) continue;
                _lastFire = now;

                InputActivity?.Invoke();
                Pressed?.Invoke(HotkeyAction.ToggleWindow);   // AppController marshals to the UI thread
            }
        }
        catch { /* device closed/gone -> the port goes quiet */ }
    }

    public void Dispose()
    {
        _closing = true;
        _dev.Dispose();
    }
}
