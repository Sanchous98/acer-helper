using AcerHelper.Features;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>Builds the generic <see cref="OptionToggle"/>/<see cref="OptionChoice"/> models from whatever
/// hardware the device exposes, wrapping each setter so failures are reported via <paramref name="notify"/>
/// (off the UI thread, then posted back). This is the one place that knows the device's option ports, so
/// <see cref="AppController"/> doesn't; the produced models are plain data handed to the view-models,
/// which stay free of the service.</summary>
internal sealed class OptionsAssembler(LaptopService svc, Action<string> notify, Func<Task<bool>> confirmCalibration)
{
    public IReadOnlyList<OptionToggle> Toggles()
    {
        var d = svc.Device;
        var list = new List<OptionToggle>();
        // LCD overdrive's write (SetGamingProfile) returns a status byte, so it self-confirms — no readback
        // (a second EC transaction the user hears as a second "click").
        if (d.LcdOverdrive is { } lcd)
            list.Add(new OptionToggle("LCD overdrive", true, lcd.Get(),
                v => RunSet(() => svc.SetLcdOverdrive(v), "LCD overdrive")));
        // Keyboard-backlight timeout uses SetFunction, which returns nothing about the result -> read back.
        if (d.KeyboardBacklight is { } kbd)
            list.Add(new OptionToggle("Keyboard backlight timeout", true, kbd.GetTimeout(),
                v => RunSet(() => svc.SetBacklightTimeout(v), "Backlight timeout"), Read: kbd.GetTimeout));
        if (d.FnLock is { } fn)
            list.Add(new OptionToggle("Fn lock", true, fn.Get(),
                v => RunSet(() => svc.SetFnLock(v), "Fn lock"), Read: fn.Get));
        return list;
    }

    public IReadOnlyList<OptionChoice> Choices()
    {
        var d = svc.Device;
        var list = new List<OptionChoice>();

        if (d.UsbCharging is { } usb)
        {
            var levels = usb.Levels;
            var names = levels.Select(l => l.DisplayName).ToList();
            list.Add(new OptionChoice("USB charging when off:", true, names, IndexOf(levels, usb.Get()),
                i => RunSet(() => svc.SetUsbCharging(levels[i].Id), "USB charging"),
                Read: () => IndexOf(levels, usb.Get())));
        }

        // Keyboard-backlight brightness is a LIGHTING control -> it lives in the Lighting window
        // (LightingViewModel.Backlight), not here. See AppController.

        // Keyboard-backlight auto-off delay (duration dropdown, where the hardware exposes a set of delays).
        if (d.KeyboardBacklightTimeout is { } to)
        {
            var opts = to.Options;
            var names = opts.Select(o => o.DisplayName).ToList();
            list.Add(new OptionChoice("Keyboard backlight timeout:", true, names, IndexOf(opts, to.Get()),
                i => RunSet(() => svc.SetKeyboardTimeout(opts[i].Id), "Backlight timeout"),
                Read: () => IndexOf(opts, to.Get())));
        }

        if (d.DisplayTint is { } tint && tint.Levels > 0)
        {
            string[] all = ["Off", "Low", "Medium", "High", "Long-use"];
            var names = all.Take(tint.Levels).ToList();
            int idx = Math.Clamp(svc.Settings.Bluelight, 0, names.Count - 1);
            list.Add(new OptionChoice("Blue-light filter:", true, names, idx,
                i => svc.SetBlueLight(i)));
        }

        return list;
    }

    // Battery controls live in the Battery section (not generic Options).

    /// <summary>Vendor battery charging strategy (e.g. Dell Adaptive/Express/Custom) as a dropdown.</summary>
    public OptionChoice? BatteryChargeMode()
    {
        if (svc.Device.BatteryChargeMode is not { } mode) return null;
        var modes = mode.Modes;
        var names = modes.Select(m => m.DisplayName).ToList();
        return new OptionChoice("Charge mode", true, names, IndexOf(modes, mode.Get()),
            i => RunSet(() => svc.SetBatteryChargeMode(modes[i].Id), "Charge mode"),
            Read: () => IndexOf(modes, mode.Get()));
    }

    public OptionToggle? BatteryLimit()
        => svc.Device.BatteryChargeLimit is { } limit
            ? new OptionToggle("Charge limit (~80%)", true, limit.Get(),
                v => RunSet(() => svc.SetBatteryLimit(v), "Battery limit"), Read: limit.Get)
            : null;

    // Gated behind a confirm dialog so a single click can't kick off a multi-hour charge/discharge cycle.
    public OptionToggle? BatteryCalibration()
        => svc.Device.BatteryCalibration is { } cal
            ? new OptionToggle("Calibration (full cycle)", true, cal.Get(),
                v => RunSet(() => svc.SetBatteryCalibration(v), "Battery calibration"),
                Read: cal.Get, ConfirmAsync: confirmCalibration)
            : null;

    // Apply one hardware set and report failure. Called on the row's serial worker thread (see HwSerial):
    // the row already serializes a control's writes + readback and runs them off the UI thread, and the WMI
    // layer serializes across controls, so this just runs the set inline and posts any error to the UI. The
    // row's own readback is what corrects the switch when a write silently doesn't take.
    private void RunSet(Func<bool> set, string what)
    {
        bool ok;
        try { ok = set(); }
        catch { ok = false; }
        if (ok) return;
        var e = svc.LastError;
        Dispatcher.UIThread.Post(() => notify($"{what} failed{(e != null ? $": {e}" : "")}"));
    }

    private static int IndexOf(IReadOnlyList<ChoiceOption> list, string? id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return 0;
    }
}
