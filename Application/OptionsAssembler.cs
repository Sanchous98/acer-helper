using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

/// <summary>Builds the generic <see cref="OptionToggle"/>/<see cref="OptionChoice"/> models from whatever
/// hardware the device exposes, wrapping each setter so failures are reported via <paramref name="notify"/>
/// (off the UI thread, then handed to <paramref name="post"/> to get back onto it). This is the one place
/// that reads the settings this machine offers, so <see cref="AppController"/> doesn't; the produced
/// models are plain data handed to the view-models, which stay free of the service.
///
/// THE HARDWARE ROWS COME FROM THE SETTINGS THE SETTINGS MODEL HOLDS (Domain/Settings.cs), not from a port slot
/// and a hard-coded name per row. The model holds the set its machine's backend declared, and each declaration
/// carries its own shape (a flag or a choice) and its own opaque key; this file supplies the one thing a
/// declaration deliberately does not — the row's label, which is the UI's business (<see cref="LabelFor"/>). So a
/// backend that declares nothing offers nothing, and one that declares a setting this build has no name for still
/// gets a row rather than silence.
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
    /// is why no <c>Get()</c> appears in this method.
    ///
    /// A flag whose write self-confirms carries NO readback (<see cref="SettingDeclaration.ReadbackVerifiesWrite"/>)
    /// and is the one shape that then needs an explicit <c>Prime</c>: with no Read there is nothing for the prime
    /// to fall back on.</summary>
    public IReadOnlyList<OptionToggle> Toggles()
    {
        var list = new List<OptionToggle>();
        foreach (var setting in svc.DeclaredSettings)
        {
            if (setting is not FlagSetting flag) continue;
            var label = Loc.T(LabelFor(setting.Key));
            list.Add(new OptionToggle(label, true, false,
                v => RunSet(() => svc.ApplySetting(flag, FlagSetting.Value(v)), label),
                Read: flag.ReadbackVerifiesWrite ? flag.Read : null,
                Prime: flag.ReadbackVerifiesWrite ? null : flag.Read));
        }

        // Publish the keyboard's zones as a virtual HID LampArray, so Windows Dynamic Lighting and
        // LampArray-aware apps can paint them (Domain/LampArrayBridge.cs). The row only exists where the
        // bridge can exist at all — the OS transport plus the installed driver (see docs/lamparray.md) — so on a
        // machine without the driver there is nothing to promise the user. NOT a declared setting: it is the
        // app's own feature rather than a knob the firmware owns, so its state lives in the bridge, not in
        // Settings.DeviceSettings. `Read` is the bridge's REAL state: publishing can fail (driver removed, PnP
        // refused), and then the switch snaps back by itself.
        if (svc.LampArray is { } lamps)
        {
            var label = Loc.T("Windows Dynamic Lighting");
            list.Add(new OptionToggle(label, true, lamps.Enabled,
                v => RunSet(() => svc.SetDynamicLighting(v), label),
                Read: () => lamps.Enabled));
        }

        return list;
    }

    /// <summary>Builds the dropdown rows. As in <see cref="Toggles"/>, <c>InitialIndex</c> is a placeholder:
    /// index 0 is what <see cref="ChoiceSetting.IndexOf"/> returns when the read finds nothing, so it is exactly
    /// what the row shows today when a read fails — the deferral introduces no new kind of lie.</summary>
    public IReadOnlyList<OptionChoice> Choices()
    {
        var list = new List<OptionChoice>();
        foreach (var setting in svc.DeclaredSettings)
        {
            if (setting is not ChoiceSetting choice) continue;
            var label = Loc.T(LabelFor(setting.Key));
            var options = choice.Options;
            var names = options.Select(o => Loc.T(o.DisplayName)).ToList();
            list.Add(new OptionChoice(label, true, names, 0,
                i => RunSet(() => svc.ApplySetting(choice, options[i].Id), label),
                Read: () => choice.IndexOf(choice.Read())));
        }

        // Keyboard-backlight brightness is a LIGHTING control -> it lives in the Lighting window
        // (LightingViewModel.Backlight), not here. See AppController.

        if (svc.Device.DisplayTint is { } tint && tint.Levels > 0)
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

        OptionChoice Row(bool onAc, string label)
        {
            var text = Loc.T(label);
            return new OptionChoice(text, true, names, IndexOfProfile(profiles, svc.SourceProfile(onAc)),
                i => RunSet(() => svc.SetSourceProfile(onAc, profiles[i]), text),
                Read: () => IndexOfProfile(profiles, svc.SourceProfile(onAc)));
        }
    }

    // Battery controls live in the Battery section (not generic Options). They read the battery OBJECT, whose
    // properties are ops rather than ports: a row exists exactly when the property does, which is the same
    // fact the old nullable port carried — now declared by the battery itself (Domain/Battery.cs).

    /// <summary>Vendor battery charging strategy (e.g. Dell Adaptive/Express/Custom) as a dropdown.</summary>
    public OptionChoice? BatteryChargeMode()
    {
        if (svc.Device.Battery.ChargeMode is not { } mode) return null;
        var modes = mode.Options;
        var names = modes.Select(m => Loc.T(m.DisplayName)).ToList();
        var label = Loc.T("Charge mode");
        return new OptionChoice(label, true, names, 0,
            i => RunSet(() => svc.SetBatteryChoice(mode, modes[i].Id), label),
            Read: () => IndexOf(modes, mode.Read()));
    }

    public OptionToggle? BatteryLimit()
    {
        if (svc.Device.Battery.ChargeLimit is not { } limit) return null;
        var label = Loc.T("Charge limit (~80%)");
        return new OptionToggle(label, true, false,
            v => RunSet(() => svc.SetBatteryToggle(limit, v), label), Read: limit.Read);
    }

    // Gated behind a confirm dialog so a single click can't kick off a multi-hour charge/discharge cycle.
    public OptionToggle? BatteryCalibration()
    {
        if (svc.Device.Battery.Calibration is not { } cal) return null;
        var label = Loc.T("Calibration (full cycle)");
        return new OptionToggle(label, true, false,
            v => RunSet(() => svc.SetBatteryToggle(cal, v), label),
            Read: cal.Read, ConfirmAsync: confirmCalibration);
    }

    /// <summary>Apply one hardware setting and report a failure. Called on the row's serial worker thread (see
    /// HwSerial): the row already serializes a control's writes + readback and runs them off the UI thread, and
    /// the WMI layer serializes across controls, so this just runs the set inline and posts any error to the UI.
    /// The row's own readback is what corrects the switch when a write silently doesn't take.
    ///
    /// A DECLARED setting refuses by THROWING (Domain/Settings.cs), and this is where that becomes a sentence:
    /// the exception carries what happened — which setting, and the transport's own words — while the label is
    /// the UI's, so the message names the row the user is looking at. Composing it from a second, English name
    /// per setting is what made five rows report a control the interface never showed them
    /// (docs/open-decisions.md, «Известные особенности» 5); that name is gone from this file.</summary>
    private void RunSet(Action set, string label)
    {
        try { set(); }
        catch (SettingNotAppliedException ex) { Fail(label, ex.Reason); }
        // A throw from outside the write itself (a transport that does not route its failure through the
        // declaration, a programming error below it): still reported, with no reason to name.
        catch { Fail(label, null); }
    }

    /// <summary>The same report for a row that is NOT a declared setting: the battery's three charging rows and
    /// the power-source pair still hand back both halves of their outcome, because their properties carry their
    /// reason out of the call rather than throwing (Domain/Battery.cs, LaptopService.ApplyStoredMode). Only the
    /// message's source changed here — it names the row the user sees, as above.</summary>
    private void RunSet(Func<(bool ok, string? error)> set, string label)
    {
        (bool ok, string? error) result;
        try { result = set(); }
        catch { result = (false, null); }
        if (!result.ok) Fail(label, result.error);
    }

    // One post, and it is a post — not a direct call: the message is built on the UI thread, which is where the
    // notify delegate expects to be handed a string.
    private void Fail(string label, string? reason)
        => post(() => notify(Loc.T("{0} failed", label) + (reason != null ? $": {reason}" : "")));

    /// <summary>The UI's own name for a setting the backend declares, by the backend's own key. This is the ONLY
    /// place that turns a setting into words, and it sits here because naming and localization belong to the UI:
    /// a declaration carries no display name (Domain/Settings.cs). A key with no entry reads as the key itself,
    /// so a backend that declares a setting this build has no name for still gets a row and a message instead of
    /// silence.
    ///
    /// The two keyboard-backlight entries share a label prefix and nothing else: one machine's timeout is a
    /// duration CHOICE, another's is a plain FLAG, and they are two declarations on two different machines —
    /// never two rows on one.</summary>
    private static string LabelFor(string key) => key switch
    {
        "lcd_override"      => "LCD overdrive",                  // Acer (both bindings)
        "backlight_timeout" => "Keyboard backlight timeout",     // Acer: the on/off flag
        "usb_charging"      => "USB charging when off:",         // Acer: battery thresholds
        "stop_timeout"      => "Keyboard backlight timeout:",    // Dell: a duration from a fixed set
        "FnLock"            => "Fn lock",                        // Dell: the BIOS attribute's own name
        "UsbPowerShare"     => "USB charging when off:",         // Dell
        _                   => key,
    };

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
