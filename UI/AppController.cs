using System.ComponentModel;
using System.Runtime.InteropServices;
using AcerHelper.Features;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the tray icon, windows and the refresh loop. Talks only to the
/// <see cref="LaptopService"/> (Application) and the device's feature ports (Domain).</summary>
internal sealed class AppController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly LaptopService _svc;
    private readonly Dictionary<string, WindowIcon> _icons = new();
    private readonly Dictionary<string, NativeMenuItem> _menuItems = new();

    private readonly MainWindow _main;
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer;

    // Options/Lighting side panels: one acrylic window each (persistent content, so switching never
    // flashes stale content), shown to the main flyout's left without animation. _shownWin is the one
    // currently visible (null = none).
    private readonly SidePanelWindow? _optionsWin;
    private readonly SidePanelWindow? _lightingWin;
    private SidePanelWindow? _shownWin;

    private DateTime _lastTurbo = DateTime.MinValue;
    private DateTime _lastNitro = DateTime.MinValue;

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, LaptopService svc)
    {
        _desktop = desktop;
        _svc = svc;

        var d = _svc.Device;
        var lighting = d.Lighting != null ? new LightingViewModel(d.Lighting, _svc.Settings, _svc.PersistLighting) : null;
        _vm = new MainViewModel(d, new UiActions(
            ApplyProfile, ApplyFan,
            (m, c, g) => _svc.PersistFan((FanMode)m, (byte)c, (byte)g),
            BuildHardwareToggles(), BuildHardwareChoices(),
            () => d.Clamshell?.Enabled ?? false, b => _svc.SetClamshell(b),
            _svc.Settings.TurboToggles, b => _svc.SetTurboToggles(b),
            () => d.Autostart?.IsEnabled() ?? false, b => _svc.SetAutostart(b),
            _svc.Settings.FanMode, _svc.Settings.CpuFan, _svc.Settings.GpuFan,
            d.BatteryInfo != null, BuildBatteryLimit(), BuildBatteryCalibration()),
            lighting);
        _main = new MainWindow { DataContext = _vm };
        _main.Deactivated += (_, _) => MaybeDismiss();

        _optionsWin  = BuildSidePanel("Options",  _vm.OptionsContent);
        _lightingWin = BuildSidePanel("Lighting", _vm.LightingContent);
        _vm.PropertyChanged += OnVmChanged;

        _svc.ApplyStartupState();

        _tray = new TrayIcon { ToolTipText = "Acer Helper", IsVisible = true, Icon = MakeIcon(Colors.Gray), Menu = BuildMenu() };
        _tray.Clicked += (_, _) => ToggleMain();
        TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { _tray });

        if (d.Hotkeys != null) d.Hotkeys.Pressed += OnHotkey;

        Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        ShowMain();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        var profiles = _svc.Device.PowerProfiles?.All ?? [];
        foreach (var p in profiles)
        {
            var item = new NativeMenuItem { Header = p.DisplayName, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => ApplyProfile(p);
            _menuItems[p.Id] = item;
            menu.Items.Add(item);
        }
        if (profiles.Count > 0) menu.Items.Add(new NativeMenuItemSeparator());
        var show = new NativeMenuItem { Header = "Show" }; show.Click += (_, _) => ShowMain(); menu.Items.Add(show);
        if (_svc.Device.Lighting != null) { var light = new NativeMenuItem { Header = "Lighting…" }; light.Click += (_, _) => ShowLighting(); menu.Items.Add(light); }
        var exit = new NativeMenuItem { Header = "Exit" }; exit.Click += (_, _) => ExitApp(); menu.Items.Add(exit);
        return menu;
    }

    // ---- actions ----

    private void ApplyProfile(PerformanceProfile p)
    {
        if (!_svc.ApplyProfile(p)) Notify("Failed to set " + p.DisplayName + Err(_svc.LastError));
        Refresh();
    }

    private void ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        if (!_svc.ApplyFan(mode, cpu, gpu)) Notify("Fans failed" + Err(_svc.LastError));
        Refresh();
    }

    private List<OptionToggle> BuildHardwareToggles()
    {
        var d = _svc.Device;
        var list = new List<OptionToggle>();
        if (d.LcdOverdrive is { } lcd)
            list.Add(new OptionToggle("LCD overdrive", true, lcd.Get(),
                v => RunHwSet(() => _svc.SetLcdOverdrive(v), "LCD overdrive")));
        if (d.KeyboardBacklight is { } kbd)
            list.Add(new OptionToggle("Keyboard backlight timeout", true, kbd.GetTimeout(),
                v => RunHwSet(() => _svc.SetBacklightTimeout(v), "Backlight timeout")));
        return list;
    }

    // Battery toggles live in the Battery section (not generic Options).
    private OptionToggle? BuildBatteryLimit()
        => _svc.Device.BatteryChargeLimit is { } limit
            ? new OptionToggle("Charge limit (~80%)", true, limit.Get(),
                v => RunHwSet(() => _svc.SetBatteryLimit(v), "Battery limit"))
            : null;

    private OptionToggle? BuildBatteryCalibration()
        // Gated behind a confirm dialog (ConfirmCalibrationAsync) so a single click can't kick off a
        // multi-hour charge/discharge cycle. Turning it OFF (to abort) isn't confirmed.
        => _svc.Device.BatteryCalibration is { } cal
            ? new OptionToggle("Calibration (full cycle)", true, cal.Get(),
                v => RunHwSet(() => _svc.SetBatteryCalibration(v), "Battery calibration"),
                ConfirmAsync: ConfirmCalibrationAsync)
            : null;

    private Task<bool> ConfirmCalibrationAsync()
    {
        // The dialog steals focus from the flyout; that's not a click "outside", so don't dismiss it.
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            "Start battery calibration?",
            "This runs a full charge then a full discharge cycle and can take several hours. Keep the "
            + "laptop plugged in and don't depend on it meanwhile. Turn the switch back off to stop.",
            "Start");
    }

    private List<OptionChoice> BuildHardwareChoices()
    {
        var d = _svc.Device;
        var list = new List<OptionChoice>();

        if (d.UsbCharging is { } usb)
        {
            var levels = usb.Levels;
            var names = levels.Select(l => l == 0 ? "Off" : $"{l}%").ToList();
            int idx = IndexOf(levels, usb.Get());
            list.Add(new OptionChoice("USB charging when off:", true, names, idx,
                i => RunHwSet(() => _svc.SetUsbCharging(levels[i]), "USB charging")));
        }

        if (d.DisplayTint is { } tint && tint.Levels > 0)
        {
            string[] all = ["Off", "Low", "Medium", "High", "Long-use"];
            var names = all.Take(tint.Levels).ToList();
            int idx = Math.Clamp(_svc.Settings.Bluelight, 0, names.Count - 1);
            list.Add(new OptionChoice("Blue-light filter:", true, names, idx,
                i => _svc.SetBlueLight(i)));
        }

        return list;
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) 
            if (list[i] == value) 
                return i;
        return 0;
    }

    // ---- hotkeys ----

    private void OnHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() =>
    {
        switch (action)
        {
            case HotkeyAction.TogglePerformance: OnTogglePerformance(); break;
            case HotkeyAction.ToggleWindow:      OnToggleWindow();      break;
        }
    });

    private void OnTogglePerformance()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTurbo).TotalMilliseconds < 800) return;
        _lastTurbo = now;

        var applied = _svc.TogglePerformance();
        if (applied != null) Notify("Profile: " + applied.DisplayName);
        Refresh();
    }

    private void OnToggleWindow()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNitro).TotalMilliseconds < 600) return;
        _lastNitro = now;
        if (_main.IsVisible) HideAll();
        else ShowMain();
    }

    // ---- refresh / windows ----

    private void Refresh()
    {
        _svc.EvaluateClamshell();

        var current = _svc.CurrentProfile();
        var selectable = _svc.SelectableProfiles();
        var sensors = _svc.ReadSensors();
        var battery = _svc.ReadBatteryInfo();
        var status = _svc.Device.StatusMessage ?? string.Empty;

        _vm.Refresh(current, selectable, sensors, battery, status);
        _tray.ToolTipText = "Acer Helper — " + (current?.DisplayName ?? "?");
        if (current != null) _tray.Icon = ProfileIcon(current);

        foreach (var kv in _menuItems)
        {
            kv.Value.IsChecked = current?.Id == kv.Key;
            kv.Value.IsEnabled = selectable.Any(p => p.Id == kv.Key);
        }
    }

    private void ToggleMain()
    {
        if (_main.IsVisible) { HideAll(); return; }
        // If the flyout just light-dismissed itself because this very click moved focus off it,
        // don't reopen it — otherwise the tray icon could never close the panel.
        if ((DateTime.UtcNow - _main.LastDismissedUtc).TotalMilliseconds < 300) return;
        ShowMain();
    }

    private void ShowMain()
    {
        _main.Show();
        _main.PositionNearTray();   // also forces foreground (needed when opened via the Nitro hotkey)
    }

    private void ShowLighting()
    {
        // Lighting is a side panel of the main flyout, not a separate top-level menu.
        ShowMain();
        _vm.OpenLightingCommand.Execute(null);
    }

    // ---- side panels (Options / Lighting) ----

    private SidePanelWindow? BuildSidePanel(string title, object? content)
    {
        if (content == null) return null;
        var w = new SidePanelWindow();
        w.SetPanel(title, content);             // content is set once and stays -> no stale-frame flash
        w.SetBack(_vm.CloseDrawerCommand);
        w.Deactivated += (_, _) => MaybeDismiss();
        return w;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDrawerOpen) or nameof(MainViewModel.DrawerContent))
            UpdateSidePanel();
    }

    /// <summary>Show the side window matching the view-model and hide the other (no animation, no
    /// content swap — each window keeps its own content).</summary>
    private void UpdateSidePanel()
    {
        SidePanelWindow? want = null;
        if (_vm.IsDrawerOpen)
            want = ReferenceEquals(_vm.DrawerContent, _vm.OptionsContent) ? _optionsWin : _lightingWin;

        if (ReferenceEquals(want, _shownWin)) return;

        var old = _shownWin;
        _shownWin = want;
        _main.SuppressDismiss = true;   // showing/hiding our own window isn't a click "outside"

        if (want != null) { PositionSide(want); want.Show(); }
        old?.Hide();
        if (want == null) _main.Activate();   // closed -> focus back to the main flyout
    }

    /// <summary>Pin a side window to the main flyout's left edge with an 8px gap, matching its height.</summary>
    private void PositionSide(SidePanelWindow win)
    {
        var screen = _main.Screens.Primary ?? _main.Screens.All.FirstOrDefault();
        double s = screen?.Scaling ?? 1.0;
        int wpx = (int)(360 * s);
        int gapPx = (int)(8 * s);
        win.Height = _main.Bounds.Height;
        win.Position = new PixelPoint(_main.Position.X - wpx - gapPx, _main.Position.Y);
    }

    /// <summary>Light-dismiss: if focus left the main flyout and both side panels, the user clicked
    /// outside the app, so hide everything. SuppressDismiss skips the one deactivation we cause by
    /// opening our own window/dialog.</summary>
    private void MaybeDismiss()
    {
        if (_main.SuppressDismiss) { _main.SuppressDismiss = false; return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_main.IsActive) return;
            if (_optionsWin is { IsActive: true }) return;
            if (_lightingWin is { IsActive: true }) return;
            HideAll();
        }, DispatcherPriority.Background);
    }

    private void HideAll()
    {
        _optionsWin?.Hide();
        _lightingWin?.Hide();
        _shownWin = null;
        _vm.IsDrawerOpen = false;
        _main.MarkDismissed();
        _main.Hide();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.IsVisible = false;
        _tray.Dispose();
        _svc.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _vm.Status = text;

    private void RunHwSet(Func<bool> set, string what) => Task.Run(() =>
    {
        bool ok;
        try { ok = set(); }
        catch { ok = false; }
        if (ok) return;
        var e = _svc.LastError;
        Dispatcher.UIThread.Post(() => Notify($"{what} failed{Err(e)}"));
    });

    private static string Err(string? e) => e != null ? $": {e}" : string.Empty;

    private WindowIcon ProfileIcon(PerformanceProfile p)
    {
        if (_icons.TryGetValue(p.Id, out var cached)) return cached;
        var c = p.Accent is { } a ? Color.FromRgb(a.R, a.G, a.B) : Colors.Gray;
        return _icons[p.Id] = MakeIcon(c);
    }

    private static WindowIcon MakeIcon(Color c)
    {
        const int sz = 32;
        var wb = new WriteableBitmap(new PixelSize(sz, sz), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = new byte[sz * sz * 4];
        for (var i = 0; i < sz * sz; i++)
        {
            var o = i * 4;
            px[o] = c.B; 
            px[o + 1] = c.G; 
            px[o + 2] = c.R; 
            px[o + 3] = 255;
        }
        using (var fb = wb.Lock()) Marshal.Copy(px, 0, fb.Address, px.Length);
        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
