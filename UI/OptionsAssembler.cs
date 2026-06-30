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
        if (d.LcdOverdrive is { } lcd)
            list.Add(new OptionToggle("LCD overdrive", true, lcd.Get(),
                v => RunHwSet(() => svc.SetLcdOverdrive(v), "LCD overdrive")));
        if (d.KeyboardBacklight is { } kbd)
            list.Add(new OptionToggle("Keyboard backlight timeout", true, kbd.GetTimeout(),
                v => RunHwSet(() => svc.SetBacklightTimeout(v), "Backlight timeout")));
        return list;
    }

    public IReadOnlyList<OptionChoice> Choices()
    {
        var d = svc.Device;
        var list = new List<OptionChoice>();

        if (d.UsbCharging is { } usb)
        {
            var levels = usb.Levels;
            var names = levels.Select(l => l == 0 ? "Off" : $"{l}%").ToList();
            int idx = IndexOf(levels, usb.Get());
            list.Add(new OptionChoice("USB charging when off:", true, names, idx,
                i => RunHwSet(() => svc.SetUsbCharging(levels[i]), "USB charging")));
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

    // Battery toggles live in the Battery section (not generic Options).
    public OptionToggle? BatteryLimit()
        => svc.Device.BatteryChargeLimit is { } limit
            ? new OptionToggle("Charge limit (~80%)", true, limit.Get(),
                v => RunHwSet(() => svc.SetBatteryLimit(v), "Battery limit"))
            : null;

    // Gated behind a confirm dialog so a single click can't kick off a multi-hour charge/discharge cycle.
    public OptionToggle? BatteryCalibration()
        => svc.Device.BatteryCalibration is { } cal
            ? new OptionToggle("Calibration (full cycle)", true, cal.Get(),
                v => RunHwSet(() => svc.SetBatteryCalibration(v), "Battery calibration"),
                ConfirmAsync: confirmCalibration)
            : null;

    private void RunHwSet(Func<bool> set, string what) => Task.Run(() =>
    {
        bool ok;
        try { ok = set(); }
        catch { ok = false; }
        if (ok) return;
        var e = svc.LastError;
        Dispatcher.UIThread.Post(() => notify($"{what} failed{(e != null ? $": {e}" : "")}"));
    });

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return 0;
    }
}
