using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

/// <summary>Builds the generic <see cref="OptionToggle"/>/<see cref="OptionChoice"/> models from whatever
/// hardware the device exposes, wrapping each setter so failures are reported via <paramref name="notify"/>
/// (off the UI thread, then handed to <paramref name="post"/> to get back onto it). This is the one place
/// that knows the device's option ports, so <see cref="AppController"/> doesn't; the produced models are
/// plain data handed to the view-models, which stay free of the service.
///
/// <paramref name="post"/> is a parameter rather than a direct <c>Dispatcher.UIThread.Post</c> call so this
/// file — the last one that would have kept the Application layer Avalonia-bound — needs no toolkit at all.
/// It also makes <see cref="RunSet"/>'s failure path testable: its only observable effect is what it hands
/// to that delegate, which a test can capture without a UI thread.</summary>
internal sealed class OptionsAssembler(LaptopService svc, Action<string> notify, Func<Task<bool>> confirmCalibration,
                                       Action<Action> post)
{
    /// <summary>Builds the toggle rows. <c>Initial</c> here is a PLACEHOLDER, not a reading: it is the value a
    /// failed read would produce (a flag port answers false when the EC won't answer), and the row fills in the
    /// real state via its prime once the UI is built — off the UI thread, on the row's own serial worker. That
    /// is why no <c>Get()</c> appears in this method.</summary>
    public IReadOnlyList<OptionToggle> Toggles()
    {
        var d = svc.Device;
        var list = new List<OptionToggle>();
        // LCD overdrive's write (SetGamingProfile) returns a status byte, so it self-confirms — no readback
        // (a second EC transaction the user hears as a second "click"). It is the one row that needs an explicit
        // Prime: with no Read there is nothing for the prime to fall back on. This costs ONE EC transaction at
        // startup (and on a language rebuild) and still ZERO per write, so the reason Read is absent stands.
        if (d.LcdOverdrive is { } lcd)
            list.Add(new OptionToggle(Loc.T("LCD overdrive"), true, false,
                v => RunSet(() => svc.SetFlag(lcd, v), "LCD overdrive"), Prime: lcd.Get));
        // Keyboard-backlight timeout uses SetFunction, which returns nothing about the result -> read back.
        if (d.KeyboardBacklight is { } kbd)
            list.Add(new OptionToggle(Loc.T("Keyboard backlight timeout"), true, false,
                v => RunSet(() => svc.SetFlag(kbd, v), "Backlight timeout"), Read: kbd.Get));
        if (d.FnLock is { } fn)
            list.Add(new OptionToggle(Loc.T("Fn lock"), true, false,
                v => RunSet(() => svc.SetFlag(fn, v), "Fn lock"), Read: fn.Get));

        // Publish the keyboard's zones as a virtual HID LampArray, so Windows Dynamic Lighting and
        // LampArray-aware apps can paint them (Domain/LampArrayBridge.cs). The row only exists where the
        // bridge can exist at all — the OS transport plus the installed driver (see docs/lamparray.md) — so on a
        // machine without the driver there is nothing to promise the user. `Read` is the bridge's REAL state:
        // publishing can fail (driver removed, PnP refused), and then the switch snaps back by itself.
        if (svc.LampArray is { } lamps)
            list.Add(new OptionToggle(Loc.T("Windows Dynamic Lighting"), true, lamps.Enabled,
                v => RunSet(() => svc.SetDynamicLighting(v), "Dynamic Lighting"), Read: () => lamps.Enabled));

        return list;
    }

    /// <summary>Builds the dropdown rows. As in <see cref="Toggles"/>, <c>InitialIndex</c> is a placeholder:
    /// index 0 is what <see cref="IndexOf"/> returns when the read finds nothing, so it is exactly what the row
    /// shows today when a read fails — the deferral introduces no new kind of lie.</summary>
    public IReadOnlyList<OptionChoice> Choices()
    {
        var d = svc.Device;
        var list = new List<OptionChoice>();

        if (d.UsbCharging is { } usb)
        {
            var levels = usb.Options;
            var names = levels.Select(l => Loc.T(l.DisplayName)).ToList();
            list.Add(new OptionChoice(Loc.T("USB charging when off:"), true, names, 0,
                i => RunSet(() => svc.SetChoice(usb, levels[i].Id), "USB charging"),
                Read: () => IndexOf(levels, usb.Get())));
        }

        // Keyboard-backlight brightness is a LIGHTING control -> it lives in the Lighting window
        // (LightingViewModel.Backlight), not here. See AppController.

        // Keyboard-backlight auto-off delay (duration dropdown, where the hardware exposes a set of delays).
        if (d.KeyboardBacklightTimeout is { } to)
        {
            var opts = to.Options;
            var names = opts.Select(o => Loc.T(o.DisplayName)).ToList();
            list.Add(new OptionChoice(Loc.T("Keyboard backlight timeout:"), true, names, 0,
                i => RunSet(() => svc.SetChoice(to, opts[i].Id), "Backlight timeout"),
                Read: () => IndexOf(opts, to.Get())));
        }

        if (d.DisplayTint is { } tint && tint.Levels > 0)
        {
            string[] all = ["Off", "Low", "Medium", "High", "Long-use"];
            var names = all.Take(tint.Levels).Select(n => Loc.T(n)).ToList();
            int idx = Math.Clamp(svc.Bluelight, 0, names.Count - 1);
            list.Add(new OptionChoice(Loc.T("Blue-light filter:"), true, names, idx,
                i => svc.SetBlueLight(i)));
        }

        return list;
    }

    /// <summary>Which performance profile each power source uses. The app has always remembered a profile per
    /// source and re-applied it on every AC&lt;-&gt;battery change, but the slots were filled INVISIBLY — by
    /// whatever you last picked while on that source — so both ended up on the same profile and plugging the
    /// charger in appeared to do nothing at all. These two rows make the pair explicit and editable.
    ///
    /// Semantics stay "the profile this source uses": picking a profile by hand while on a source still updates
    /// that source's row, so the two can never disagree about what will be applied (the rows re-read when the
    /// Options drawer opens — see OptionsViewModel.Sync). Setting the row for the source that is live right now
    /// applies it immediately instead of waiting for the next unplug.</summary>
    public IReadOnlyList<OptionChoice> PowerSourceProfiles()
    {
        if (svc.Device.PowerProfiles is not { } pp || pp.All.Count == 0) return [];
        var profiles = pp.All;
        var names = profiles.Select(p => Loc.T(p.DisplayName)).ToList();
        return [Row(onAc: true, "Profile on AC power:"), Row(onAc: false, "Profile on battery:")];

        OptionChoice Row(bool onAc, string label) => new(
            Loc.T(label), true, names, IndexOfProfile(profiles, svc.SourceProfile(onAc)),
            i => RunSet(() => svc.SetSourceProfile(onAc, profiles[i]), "Power-source profile"),
            Read: () => IndexOfProfile(profiles, svc.SourceProfile(onAc)));
    }

    // Battery controls live in the Battery section (not generic Options).

    /// <summary>Vendor battery charging strategy (e.g. Dell Adaptive/Express/Custom) as a dropdown.</summary>
    public OptionChoice? BatteryChargeMode()
    {
        if (svc.Device.BatteryChargeMode is not { } mode) return null;
        var modes = mode.Options;
        var names = modes.Select(m => Loc.T(m.DisplayName)).ToList();
        return new OptionChoice(Loc.T("Charge mode"), true, names, 0,
            i => RunSet(() => svc.SetChoice(mode, modes[i].Id), "Charge mode"),
            Read: () => IndexOf(modes, mode.Get()));
    }

    public OptionToggle? BatteryLimit()
        => svc.Device.BatteryChargeLimit is { } limit
            ? new OptionToggle(Loc.T("Charge limit (~80%)"), true, false,
                v => RunSet(() => svc.SetFlag(limit, v), "Battery limit"), Read: limit.Get)
            : null;

    // Gated behind a confirm dialog so a single click can't kick off a multi-hour charge/discharge cycle.
    public OptionToggle? BatteryCalibration()
        => svc.Device.BatteryCalibration is { } cal
            ? new OptionToggle(Loc.T("Calibration (full cycle)"), true, false,
                v => RunSet(() => svc.SetFlag(cal, v), "Battery calibration"),
                Read: cal.Get, ConfirmAsync: confirmCalibration)
            : null;

    // Apply one hardware set and report failure. Called on the row's serial worker thread (see HwSerial):
    // the row already serializes a control's writes + readback and runs them off the UI thread, and the WMI
    // layer serializes across controls, so this just runs the set inline and posts any error to the UI. The
    // row's own readback is what corrects the switch when a write silently doesn't take.
    //
    // The setter hands back BOTH halves of its failure, so the message carries the reason of THIS write. It used
    // to read a shared LastError field afterwards, which is a different thing: this runs on a row's own worker
    // while the UI thread and the background pass write the same field, so the reason shown could belong to
    // another call entirely (docs/open-decisions.md §2).
    private void RunSet(Func<(bool ok, string? error)> set, string what)
    {
        (bool ok, string? error) result;
        try { result = set(); }
        catch { result = (false, null); }
        if (result.ok) return;
        post(() => notify(Loc.T("{0} failed", Loc.T(what)) + (result.error != null ? $": {result.error}" : "")));
    }

    // A source with nothing remembered yet (fresh install, before that source was ever seen) has no profile to
    // point at, so the row shows the first one — it becomes real as soon as the user picks, or as soon as the
    // source is first seen and the slot is seeded from the hardware.
    private static int IndexOfProfile(IReadOnlyList<PerformanceProfile> list, PerformanceProfile? p)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == p?.Id)
                return i;
        return 0;
    }

    private static int IndexOf(IReadOnlyList<ChoiceOption> list, string? id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return 0;
    }
}
