using System.Collections.ObjectModel;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcerHelper.UI.ViewModels;

/// <summary>The dashboard root: device name + current-profile chip (header), the capability sections
/// (a collection rendered by DataTemplates) as a single full-width column, and a status line plus
/// the drawer-launch buttons (footer). Options and Lighting aren't in the main column — they live in
/// a slide-out side drawer opened from the footer. Built once from the device's capabilities;
/// <see cref="Refresh"/> pushes live state in.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MonitorViewModel? _monitor;
    private readonly ProfilesViewModel? _profiles;
    private readonly FansViewModel? _fans;
    private readonly TuningViewModel? _tuning;   // Tuning drawer: GPU clocks + CPU power (either may be null)
    private readonly BatteryViewModel? _battery;
    private readonly OptionsViewModel? _options;
    private readonly LightingViewModel? _lighting;

    public string DeviceName { get; }

    /// <summary>Small "v0.20.x" tag next to the device name. AppInfo.Version is the compile-time const
    /// baked from the csproj &lt;Version&gt; (AOT-safe — see UpdateChecker), so header and update check
    /// can never disagree about what version is running.</summary>
    public string AppVersion { get; } = "v" + AppInfo.Version;

    /// <summary>Tooltip for the tag — names the app, because an unlabeled "v0.20.2" sitting next to a
    /// hardware product name reads as a device/BIOS version. Brand + number: locale-neutral, no Tr key.</summary>
    public string AppVersionTip { get; } = "Acer Helper v" + AppInfo.Version;

    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    public bool ShowOptions => _options != null;
    public bool ShowLighting => _lighting != null;
    public bool ShowTuning => _tuning != null;

    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _status = "";

    /// <summary>The notifications behind the bell: a message the app has for the user (access not granted, a
    /// reboot pending, an update waiting), each with the action a click on it should run. Handed IN rather than
    /// built here, because this whole view model is torn down and rebuilt on a live language switch
    /// (<c>AppController.RebuildForLanguage</c>) while the conditions it is reporting outlive that swap — see
    /// <see cref="NotificationCenter"/>.</summary>
    public NotificationCenter Notifications { get; }

    /// <summary>Whether the bell's list is showing. PRESENTATION state, so it lives here and not in the list: a
    /// rebuilt window opens with the drop-down closed, while the notifications themselves carry over.</summary>
    [ObservableProperty] private bool _isNotificationsOpen;

    // Side drawer: which page is showing. DrawerContent identifies it; the three pages are hosted SIDE BY SIDE
    // in the view and toggled by the Is*Page flags below, NOT swapped through one shared host — see those flags.
    [ObservableProperty] private object? _drawerContent;
    [ObservableProperty] private string _drawerTitle = "";
    [ObservableProperty] private bool _isDrawerOpen;

    // The drawer pages, each with its own host in the view (MainWindow.axaml). They were previously swapped
    // through a single ContentControl, which is what caused the empty-drawer bug: after the Tuning page had been
    // hosted there — it is the only page with nested ContentControls of its own (GPU/CPU) — the presenter built
    // NOTHING for whatever page came next. Measured: the drawer page had 14 visual descendants (just the chrome:
    // back button, title, ScrollViewer, the presenter) and no view at all, while the view-model still held its
    // rows. Opening the same page a second time fixed itself, which is why it looked like "works once, then
    // empty" and why the victim alternated between Options and Lighting. Giving each page its own host removes
    // the re-hosting entirely: every view is built once and only its visibility changes. An IsVisible=false
    // control is not measured either, so the flyout still sizes to the page actually on screen.
    public OptionsViewModel?  OptionsPage  => _options;
    public LightingViewModel? LightingPage => _lighting;
    public TuningViewModel?   TuningPage   => _tuning;

    /// <summary>The battery section's view-model. Not a drawer page like the three above — it is one of
    /// <see cref="Sections"/> — but exposed for the same reason they are: its three charging rows are built with
    /// placeholders and are settled by <c>AppController</c> once the UI exists (see
    /// <see cref="BatteryViewModel.Prime"/>).</summary>
    public BatteryViewModel? Battery => _battery;

    public bool IsOptionsPage  => ReferenceEquals(DrawerContent, _options);
    public bool IsLightingPage => ReferenceEquals(DrawerContent, _lighting);
    public bool IsTuningPage   => ReferenceEquals(DrawerContent, _tuning);

    // Keep the page flags in step with DrawerContent (the [ObservableProperty] setter raises this).
    partial void OnDrawerContentChanged(object? value)
    {
        OnPropertyChanged(nameof(IsOptionsPage));
        OnPropertyChanged(nameof(IsLightingPage));
        OnPropertyChanged(nameof(IsTuningPage));
    }

    public MainViewModel(Device device, UiActions a, LightingViewModel? lighting, NotificationCenter notifications)
    {
        DeviceName = device.VendorName;
        _lighting = lighting;
        Notifications = notifications;

        // Main column (full width, glance order): sensors, performance modes, fans, battery.
        if (device.Sensors != null)
            Sections.Add(_monitor = new MonitorViewModel());
        if (device.PowerProfiles is { } pp)
            Sections.Add(_profiles = new ProfilesViewModel(pp.All, a.Profiles.Traits, a.Profiles.Apply,
                                                          a.Profiles.TurboToggles, a.Profiles.SetTurbo));
        if (device.FanControl is { } fc)
            Sections.Add(_fans = new FansViewModel(fc.Capability, a.Fans.Initial,
                a.Fans.SetFan, a.Fans.SetFanCurve, a.Fans.ShowCurve));
        var bat = a.Battery;
        if (bat.HasInfo || bat.Limit != null || bat.Calibration != null || bat.ChargeMode != null)
            Sections.Add(_battery = new BatteryViewModel(bat.HasInfo, bat.Limit, bat.Calibration, bat.ChargeMode));

        // Options live in the drawer, not the main column.
        _options = OptionsViewModel.TryCreate(device, a.Options);

        // Performance tuning (GPU clock offsets + CPU power mode) lives in its own "Tuning" drawer, opened
        // from the footer — kept off the main column so the dashboard stays uncluttered. The drawer exists when
        // the device supports at least one of the two; each child is null when its capability is absent.
        var gpuVm = device.GpuOverclock is { } gpu
            ? new GpuViewModel(gpu.Name, gpu.CoreRange, gpu.MemRange, a.Gpu.Initial, a.Gpu.SetGpuOc) : null;
        var cpuVm = device.CpuPower is { } cpu
            ? new CpuViewModel(cpu.Modes, a.Cpu.Initial, a.Cpu.SetCpuPower) : null;
        var coVm = device.CurveOptimizer is { } co
            ? new CoViewModel(co.Name, co.Range, co.MillivoltsPerCount, a.Co.Domains, a.Co.Initial, a.Co.SetCo) : null;
        if (gpuVm != null || cpuVm != null || coVm != null)
            _tuning = new TuningViewModel(gpuVm, cpuVm, coVm);
    }

    /// <summary>The bell: show the list, or put it away again. A toggle, like the drawer buttons, so the one
    /// control that opens the list also closes it.</summary>
    [RelayCommand] private void ToggleNotifications() => IsNotificationsOpen = !IsNotificationsOpen;

    /// <summary>Put the bell's list away. Reached from the window's dismiss layer, whose press is by
    /// construction a click OUTSIDE the list (see <c>MainWindow.axaml</c>/<c>.axaml.cs</c>), so it must HIDE
    /// and never open: a click elsewhere on the card while the list is showing dismisses it. Deliberately not
    /// the toggle — the two gestures mean different things and only the bell is allowed to open.</summary>
    [RelayCommand] private void CloseNotifications() => IsNotificationsOpen = false;

    // ---- the three sources that raise notifications ----
    //
    // Each of these was a banner in this window before the bell existed, and each keeps the action its banner's
    // click ran. The word is this view model's — that is its job, every string in the app is read here at
    // construction or per raise — while WHICH condition is live is the caller's, because only AppController can
    // see the device, the check result and the install outcome.
    //
    // The text arrives as a factory, so a list that survives a language rebuild says itself in the new language
    // (see NotificationViewModel.Text). Raising twice is harmless and expected — the periodic update check, a UI
    // rebuild — because an id is one entry: the second raise refreshes it and keeps its read flag.

    /// <summary>The startup (and later periodic) check found a newer release. The notification's click EXPANDS it
    /// to show <paramref name="changelog"/> and an explicit Install button; only that button runs
    /// <paramref name="install"/>. The id carries the VERSION: a newer release is a new condition, and one the
    /// user has ignored is not suppressed for it.
    ///
    /// ONE OFFER AT A TIME, which is what the family retirement is for: a check that finds v1.1 while the list
    /// still holds v1.0 leaves ONE entry, because the older one's install button would install an older release —
    /// a worse answer than no row at all. The retirement spares this version's own id, so a re-check of the SAME
    /// release refreshes the entry the user already has rather than replacing it (a supersede is not a dismissal:
    /// the read flag and the ignore state survive it).</summary>
    public void SetUpdate(string version, string? changelog, Action install)
    {
        var id = NotificationCenter.UpdateId(version);
        Notifications.RetireFamilyExcept(NotificationCenter.UpdateIdPrefix, id);
        Notifications.Raise(id, () => Loc.T("Update available: v{0}", version), install, () => changelog ?? "");
    }

    /// <summary>Bring the update to the user from the TRAY item: open the bell's list and expand the update entry
    /// so its changelog and Install button are on screen. The tray item is not itself an install trigger — only
    /// the notification's Install button is (see <see cref="NotificationViewModel"/>) — so this is where its
    /// click leads.</summary>
    public void ShowUpdate()
    {
        IsNotificationsOpen = true;
        Notifications.ExpandFamily(NotificationCenter.UpdateIdPrefix);
    }

    /// <summary>The Linux permission files are not installed, so the root-only controls are out of reach: offer
    /// the one-click pkexec install, whose click runs it (via the caller's callback).</summary>
    public void SetHardwareAccessNeeded(Action grant)
        => Notifications.Raise(NotificationCenter.HardwareAccessNeeded, () => Loc.T("Grant hardware access…"), grant);

    /// <summary>An install could not make the module parameters live, so what is missing now is a REBOOT. This is
    /// the one message with nowhere else to live: <c>Status</c> is rewritten from the device's own status message
    /// by every refresh tick (a few seconds), so a reboot instruction put there is on screen briefly and then
    /// replaced by text that does not mention rebooting at all.
    ///
    /// IT RETIRES THE OFFER, and that is the same arrangement the single banner had (its two messages were
    /// branches of one decision): once the files are in /etc the offer is over — the installer is idempotent and
    /// never offers itself again — so leaving it beside the reboot instruction would show the user a "grant
    /// access" button for access they already granted. They are separate IDS all the same, and that is what
    /// keeps an ignored offer from swallowing this instruction (see NotificationCenter.HardwareAccessRebootPending).
    ///
    /// THE GRANT CALLBACK COMES IN AS A PARAMETER, for the same reason it does in <see cref="SetHardwareAccessNeeded"/>,
    /// and that is the whole of a defect this used to carry: the entry promises "(click to retry)" in its own
    /// words, so the click has to be the retry — in the one state where retrying is the only recovery short of a
    /// reboot, and with no other way back to the installer.</summary>
    public void SetHardwareAccessRebootPending(Action grant)
    {
        Notifications.Ignore(NotificationCenter.HardwareAccessNeeded);
        Notifications.Raise(NotificationCenter.HardwareAccessRebootPending,
                            () => Loc.T("Restart your computer to finish enabling the unlocked controls (click to retry)."),
                            grant);
    }

    /// <summary>The hardware-access condition is over — the install took and the parameters are live, so the app
    /// only has to be restarted. Both entries are retracted, whichever of the two the user was looking at, and
    /// retracted for good: this is the branch that replaced the old banner's <c>NeedsHardwareAccess = false;</c>,
    /// and it means the same thing.</summary>
    public void ClearHardwareAccess()
    {
        Notifications.Ignore(NotificationCenter.HardwareAccessNeeded);
        Notifications.Ignore(NotificationCenter.HardwareAccessRebootPending);
    }

    [RelayCommand]
    private void OpenOptions()
    {
        _options?.Sync();   // re-read the rows' real state (hardware toggles, per-source profiles) before showing
        OpenDrawer(Loc.T("Options"), _options);
    }

    [RelayCommand] private void OpenTuning() => OpenDrawer(Loc.T("Tuning"), _tuning);

    [RelayCommand]
    private void OpenLighting()
    {
        // The doubt moment at the composite level, and the whole rule lives in the method it calls: re-apply OUR
        // lighting rather than ask the wire what it holds, yield to a host that owns the surface, and settle the
        // plain backlight (the one control here with no stored value to push) by reading it.
        //
        // Only when this click OPENS the drawer, because re-clicking the button of the drawer on screen CLOSES it
        // (the toggle in OpenDrawer) and the re-apply would then run for a drawer on its way out: it re-applies
        // the RGB panels and queues the backlight's read, and that read is a device transaction on a serial
        // worker that does NOT coalesce — so every toggle click would queue one more of them, unlike the event
        // path's read, which keeps one in flight plus one pending for exactly this reason (VerifiedHwValue).
        if (_lighting != null && !IsShowing(_lighting)) _lighting.Reapply();
        OpenDrawer(Loc.T("Lighting"), _lighting);
    }

    [RelayCommand] private void CloseDrawer() => IsDrawerOpen = false;

    /// <summary>Reflect a mode's fan preset in the fan section (called when the performance mode changes).</summary>
    public void ReloadFans(FanAxisState state) => _fans?.Load(state);

    /// <summary>Reflect a mode's GPU-OC offsets in the GPU section (called when the performance mode changes).</summary>
    public void ReloadGpuOc(GpuAxisState state) => _tuning?.Gpu?.Load(state);

    /// <summary>Reflect a mode's CPU power choice in the CPU section — on a performance-mode change, and on the
    /// pass that fills the row's construction placeholder (wave 6: <c>AppController</c>'s <c>Tick.CpuPrimed</c>).
    /// The two are separate events, which is why the caller does not gate this on the mode change.</summary>
    public void ReloadCpuPower(string? id) => _tuning?.Cpu?.Load(id);

    /// <summary>Reflect a mode's undervolt offsets in the CPU section (called when the performance mode changes).
    /// Index-aligned with the port's core groups.</summary>
    public void ReloadCo(IReadOnlyList<int> counts) => _tuning?.Co?.Load(counts);

    /// <summary>Rebind the lighting panels to a mode's lighting (called when the mode changes). The argument is
    /// the door for THAT mode, taken by whoever had the profile in hand — the panels take their values out of it
    /// and every later edit goes back through it.</summary>
    public void ReloadLighting(ILightZoneMode mode) => _lighting?.Reload(mode);

    /// <summary>Re-apply the lighting values the section already holds, without rebinding anything (the
    /// coordinator's re-apply burst, a resume, a lid open). Takes no argument because there is none to take:
    /// the values ARE the section's, and re-reading the graph on every tick is what this replaced.</summary>
    public void RepaintLighting() => _lighting?.Repaint();

    /// <summary>True while the Lighting drawer is the one actually on screen. Cheap check the input-driven
    /// brightness adoption gates on, so nothing happens on keystrokes when lighting isn't visible.</summary>
    public bool IsLightingVisible => IsShowing(_lighting);

    /// <summary>Whether <paramref name="content"/> is the section the open drawer is showing — the toggle rule,
    /// stated once because two callers need to know its answer before they act: <see cref="OpenDrawer"/> closes
    /// the drawer when it is true, and <see cref="OpenLighting"/> skips its re-apply when it is true (the click
    /// is then a close, not an open).</summary>
    private bool IsShowing(object? content) => IsDrawerOpen && ReferenceEquals(content, DrawerContent);

    /// <summary>An out-of-band input event (a special key) while the Lighting drawer is showing: ADOPT the
    /// brightness the hardware now holds as the new intent. The read runs off the UI thread and is the only path
    /// on which a read changes state — see docs/state-and-events.md and
    /// <see cref="LightingViewModel.AdoptFromInput"/>.</summary>
    public void AdoptLightingIfVisible()
    {
        if (IsLightingVisible) _lighting?.AdoptFromInput();
    }

    private void OpenDrawer(string title, object? content)
    {
        if (content == null) return;
        // Re-clicking the open drawer's button closes it (toggle).
        if (IsShowing(content)) { IsDrawerOpen = false; return; }
        DrawerContent = content;
        DrawerTitle = title;
        IsDrawerOpen = true;
    }

    // The battery telemetry row is deliberately NOT refreshed here. It has its own, faster writer — the
    // one-second BatteryPollSchedule path in AppController (RefreshBattery) — and this heavy pass is the SLOW
    // half (three seconds) that may only reach the UI after its own EC/WMI sweep. Feeding a battery snapshot
    // read at the start of this pass into the same BatteryViewModel would overwrite the fresh one-second value
    // with an older one every three seconds, pinning the row the user watches back to this pass's cadence (the
    // hazard the split exists to remove). One row, one writer: this method carries everything ELSE that the
    // pass reads, and the battery card is updated solely by the fast path.
    public void Refresh(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> selectable,
                        bool turboToggles, PerformanceProfile? baseProfile,
                        SensorSnapshot s, string? status)
    {
        HasProfile = current != null;
        ProfileName = current != null ? Loc.T(current.DisplayName) : "";
        _profiles?.Update(current, selectable, turboToggles, baseProfile);
        _monitor?.Update(s);
        if (status != null) Status = status;
    }
}
