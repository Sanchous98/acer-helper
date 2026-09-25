using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The asusd Backlight interface behind the app's plain-backlight port.
//
// *** OWNER REVIEW: THIS IS THE DISPLAY BACKLIGHT, NOT THE KEYBOARD ONE. ***
// Read the asusd source before enabling this on a real machine: `rog-platform::backlight` binds
// BacklightType::Primary to the `intel_backlight` sysfs device and BacklightType::Screenpad to
// `asus_screenpad` — i.e. xyz.ljones.Backlight is the SCREEN panel's brightness. The app's
// IKeyboardBrightness slot is explicitly the plain KEYBOARD backlight (Domain/Ports.cs, and the UI only
// shows it when there are no RGB panels — UI/ViewModels/LightingViewModel.cs), whose real hardware path on
// Linux is the kernel LED class (`*kbd_backlight`), already served by SysfsKbdBacklight.
//
// The port below is therefore the correct asusd-wire mapping (primary_brightness is a 0..100 percentage) but
// it is deliberately NOT wired to KeyboardBrightness by AsusDevice.Linux.cs: doing so would replace a working
// keyboard slider with a screen-brightness control under a keyboard label, which is a wrong-slot mapping and
// a regression. It is implemented and tested so the plumbing exists if a display-brightness surface is ever
// added, and so the decision is explicit rather than an omission. The one-line enable is marked in
// AsusDevice.Linux.cs.

/// <summary>The asusd Backlight coordinates and the pure 0..100 rule.</summary>
internal static class AsusdBacklight
{
    internal const string Interface = "xyz.ljones.Backlight";
    internal const string PrimaryBrightnessProperty = "primary_brightness";

    /// <summary>asusd validates 0..100 and scales to the panel's own max; the app port is a percentage too.</summary>
    internal const int MaxPercent = 100;

    internal static int Clamp(int percent) => Math.Clamp(percent, 0, MaxPercent);
}

/// <summary>
/// The asusd-backed plain-backlight port, built over a delegated busctl runner so the whole thing is reachable
/// by a test. It reads and writes <c>primary_brightness</c> (signal <c>i</c>, 0..100), mapping asusd's percent
/// straight through: <see cref="MaxLevel"/> is 100 and a level is a percentage.
///
/// See the file header for why this is NOT wired to the keyboard slot on a real machine.
/// </summary>
internal sealed class AsusdBacklightPort(Func<string[], (int code, string output)> busctl) : IKeyboardBrightness
{
    public int MaxLevel => AsusdBacklight.MaxPercent;

    public string? LastError { get; private set; }

    /// <summary>The live percentage, clamped; 0 when the daemon cannot be read (the same "unreadable reads as
    /// off" rule the other backlight ports use).</summary>
    public int Get()
    {
        var (code, output) = busctl(Asusd.GetPropertyArgumentsAt(
            Asusd.BasePath, AsusdBacklight.Interface, AsusdBacklight.PrimaryBrightnessProperty));
        return code == 0 && AsusdValues.ParseScalarUInt(output) is { } v ? AsusdBacklight.Clamp(v) : 0;
    }

    public bool Set(int level)
    {
        var (code, output) = busctl(Asusd.SetPropertyArgumentsAt(
            Asusd.BasePath, AsusdBacklight.Interface, AsusdBacklight.PrimaryBrightnessProperty,
            "i", AsusdBacklight.Clamp(level).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (code != 0) { LastError = Asusd.Describe(code, output); return false; }
        LastError = null;
        return true;
    }
}
