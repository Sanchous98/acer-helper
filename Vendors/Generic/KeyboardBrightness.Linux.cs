using System.IO;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Generic Linux keyboard-backlight brightness via the kernel LED class
/// (<c>/sys/class/leds/*kbd_backlight/{brightness,max_brightness}</c>) — the vendor-neutral, ACPI/EC-backed
/// interface the kernel exposes for Dell, ThinkPad, ASUS, Framework, Apple, … No dependency on any userspace
/// daemon (this is the Linux analogue of the Windows WMI backing — the hardware interface, not a desktop
/// service). Reading works as the user; the brightness node is root-writable only, so — like every other
/// root-only sysfs knob — the control is offered ONLY when the node is actually writable (a udev rule grants
/// this without running the app as root; see packaging/). Levels are discrete hardware steps
/// (Dell Latitude: 0..2 = Off/Dim/Bright).</summary>
public sealed class SysfsKbdBacklight : IKeyboardBrightness
{
    private const string LedRoot = "/sys/class/leds";

    private readonly string _dir;
    public int MaxLevel { get; }
    public string? LastError { get; private set; }

    private SysfsKbdBacklight(string dir, int max) { _dir = dir; MaxLevel = max; }

    /// <summary>Present only when a keyboard-backlight LED exists AND its brightness node is writable by this
    /// process (root, or a udev rule) — a slider that can't move is worse than none.</summary>
    public static SysfsKbdBacklight? TryCreate()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(LedRoot))
            {
                if (!Path.GetFileName(dir).EndsWith("kbd_backlight", StringComparison.Ordinal)) continue;
                if (!int.TryParse(ReadNode(dir, "max_brightness"), out var max) || max <= 0) continue;
                if (CanWrite(Path.Combine(dir, "brightness"))) return new SysfsKbdBacklight(dir, max);
            }
        }
        catch { /* none */ }
        return null;
    }

    public int Get()
        => int.TryParse(ReadNode(_dir, "brightness"), out var v) ? Math.Clamp(v, 0, MaxLevel) : 0;

    public bool Set(int level)
    {
        try
        {
            File.WriteAllText(Path.Combine(_dir, "brightness"), Math.Clamp(level, 0, MaxLevel).ToString());
            LastError = null;
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    // Probe write permission without writing (sysfs attrs don't act until an actual write happens).
    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }

    private static string? ReadNode(string dir, string file)
    {
        try { var p = Path.Combine(dir, file); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch { return null; }
    }
}
