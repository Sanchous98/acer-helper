using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AcerHelper;

/// <summary>Owns the platform layer, tray icon, windows and the refresh loop.</summary>
internal sealed class AppController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IPlatform _platform = new WindowsPlatform();
    private readonly Settings _settings = Settings.Load();
    private readonly Dictionary<AcerProfile, WindowIcon> _icons = new();
    private readonly Dictionary<AcerProfile, NativeMenuItem> _menuItems = new();

    private readonly MainWindow _main;
    private readonly LightingWindow _lighting;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer;

    private AcerProfile _lastNonTurbo = AcerProfile.Balanced;
    private DateTime _lastTurbo = DateTime.MinValue;
    private DateTime _lastNitro = DateTime.MinValue;

    public AppController(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        _lighting = new LightingWindow(_platform.Rgb);
        _main = new MainWindow(
            ApplyProfile, ApplyFan, ShowLighting,
            () => _platform.Clamshell.Enabled,
            b => { _platform.Clamshell.SetEnabled(b); _settings.Clamshell = b; _settings.Save(); },
            _settings.TurboToggles,
            b => { _settings.TurboToggles = b; _settings.Save(); },
            _platform.Autostart.IsEnabled,
            b => _platform.Autostart.SetEnabled(b),
            BuildHardwareToggles(), BuildHardwareChoices(),
            _settings.FanMode, _settings.CpuFan, _settings.GpuFan,
            (m, c, g) => { _settings.FanMode = m; _settings.CpuFan = c; _settings.GpuFan = g; _settings.Save(); });

        if (_settings.Clamshell) _platform.Clamshell.SetEnabled(true);
        if (_settings.Bluelight > 0) _platform.DisplayTint.Apply(_settings.Bluelight);

        _tray = new TrayIcon { ToolTipText = "Acer Helper", IsVisible = true, Icon = MakeIcon(Colors.Gray), Menu = BuildMenu() };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });

        _platform.Hotkeys.Pressed += OnHotkey;

        Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        ShowMain();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        foreach (var p in AcerProfileInfo.All)
        {
            var item = new NativeMenuItem { Header = AcerProfileInfo.DisplayName(p), ToggleType = NativeMenuItemToggleType.CheckBox };
            item.Click += (_, _) => ApplyProfile(p);
            _menuItems[p] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        var show = new NativeMenuItem { Header = "Show" }; show.Click += (_, _) => ShowMain(); menu.Items.Add(show);
        var light = new NativeMenuItem { Header = "Lighting…" }; light.Click += (_, _) => ShowLighting(); menu.Items.Add(light);
        var exit = new NativeMenuItem { Header = "Exit" }; exit.Click += (_, _) => ExitApp(); menu.Items.Add(exit);
        return menu;
    }

    // ---- actions ----

    private void ApplyProfile(AcerProfile p)
    {
        if (!_platform.Performance.SetProfile(p))
            Notify("Failed to set " + AcerProfileInfo.DisplayName(p) + Err(_platform.Performance.LastError));
        Refresh();
    }

    private void ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        bool ok = mode == FanMode.Custom ? _platform.Performance.SetCustomSpeeds(cpu, gpu) : _platform.Performance.SetFanMode(mode);
        if (!ok) Notify("Fans failed" + Err(_platform.Performance.LastError));
        Refresh();
    }

    private IReadOnlyList<OptionToggle> BuildHardwareToggles()
    {
        var b = _platform.Battery; var perf = _platform.Performance; var per = _platform.Peripherals;
        bool? batt = b.GetLimit(), cal = b.GetCalibration(), lcd = perf.GetLcdOverdrive(), kbd = per.GetBacklightTimeout();
        return new List<OptionToggle>
        {
            new("Battery charge limit (~80%)", batt.HasValue, batt ?? false,
                v => RunHwSet(() => b.SetLimit(v), "Battery limit", () => b.LastError)),
            new("LCD overdrive", lcd.HasValue, lcd ?? false,
                v => RunHwSet(() => perf.SetLcdOverdrive(v), "LCD overdrive", () => perf.LastError)),
            new("Keyboard backlight timeout", kbd.HasValue, kbd ?? false,
                v => RunHwSet(() => per.SetBacklightTimeout(v), "Backlight timeout", () => per.LastError)),
            // Calibration greyed out until an async confirm dialog is added (avoids starting
            // a multi-hour charge/discharge cycle on a single click). Logic kept ready.
            new("Battery calibration (full cycle)", false, cal ?? false,
                v => RunHwSet(() => b.SetCalibration(v), "Battery calibration", () => b.LastError)),
        };
    }

    private IReadOnlyList<OptionChoice> BuildHardwareChoices()
    {
        var per = _platform.Peripherals;
        int[] levels = { 0, 10, 20, 30 };
        int? usb = per.GetUsbChargingLevel();
        int idx = Array.IndexOf(levels, usb ?? 0); if (idx < 0) idx = 0;
        return new List<OptionChoice>
        {
            new("USB charging when off:", usb.HasValue, new[] { "Off", "10%", "20%", "30%" }, idx,
                i => RunHwSet(() => per.SetUsbChargingLevel(levels[i]), "USB charging", () => per.LastError)),
            new("Bluelight Shield:", true, new[] { "Off", "Low", "Medium", "High", "Long-use" }, _settings.Bluelight,
                i => { _platform.DisplayTint.Apply(i); _settings.Bluelight = i; _settings.Save(); }),
        };
    }

    // ---- hotkeys ----

    private void OnHotkey(AcerHotkey key) => Dispatcher.UIThread.Post(() =>
    {
        if (key == AcerHotkey.Turbo) OnTurbo();
        else OnNitro();
    });

    private void OnTurbo()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTurbo).TotalMilliseconds < 800) return;
        _lastTurbo = now;

        byte mask = _platform.Performance.GetSupportedMask();
        AcerProfile? cur = _platform.Performance.GetProfile();
        AcerProfile target;
        if (_main.TurboToggles)
        {
            if (cur == AcerProfile.Turbo) target = _lastNonTurbo;
            else { if (cur.HasValue) _lastNonTurbo = cur.Value; target = AcerProfile.Turbo; }
        }
        else target = NextSupported(cur, mask);

        if (_platform.Performance.SetProfile(target)) Notify("Profile: " + AcerProfileInfo.DisplayName(target));
        Refresh();
    }

    private void OnNitro()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNitro).TotalMilliseconds < 600) return;
        _lastNitro = now;
        if (_main.IsVisible) _main.Hide();
        else ShowMain();
    }

    private static AcerProfile NextSupported(AcerProfile? cur, byte mask)
    {
        var all = AcerProfileInfo.All;
        int start = 0;
        if (cur.HasValue) for (int i = 0; i < all.Length; i++) if (all[i] == cur.Value) { start = i; break; }
        for (int step = 1; step <= all.Length; step++)
        {
            var cand = all[(start + step) % all.Length];
            if (mask == 0 || AcerProfileInfo.IsSupported(mask, cand)) return cand;
        }
        return cur ?? AcerProfile.Balanced;
    }

    // ---- refresh / windows ----

    private void Refresh()
    {
        _platform.Clamshell.Evaluate();

        AcerProfile? current = _platform.Performance.GetProfile();
        byte mask = _platform.Performance.GetSupportedMask();
        SensorSnapshot sensors = _platform.Performance.ReadSensors();
        string status = _platform.Performance.Available ? string.Empty : "WMI unavailable — run as Administrator.";

        _main.RefreshState(current, mask, sensors, status);
        _tray.ToolTipText = "Acer Helper — " + (current.HasValue ? AcerProfileInfo.DisplayName(current.Value) : "?");
        if (current.HasValue) _tray.Icon = ProfileIcon(current.Value);

        foreach (var kv in _menuItems)
        {
            kv.Value.IsChecked = current.HasValue && current.Value == kv.Key;
            kv.Value.IsEnabled = mask == 0 || AcerProfileInfo.IsSupported(mask, kv.Key);
        }
    }

    private void ShowMain()
    {
        _main.Show();
        _main.PositionNearTray();
        _main.Activate();
    }

    private void ShowLighting()
    {
        _lighting.Show();
        _lighting.Activate();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.IsVisible = false;
        _tray.Dispose();
        _platform.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _main.SetStatus(text);

    private void RunHwSet(Func<bool> set, string what, Func<string?> err) => Task.Run(() =>
    {
        bool ok; try { ok = set(); } catch { ok = false; }
        if (!ok)
        {
            string? e = err();
            Dispatcher.UIThread.Post(() => Notify(what + " failed" + Err(e)));
        }
    });

    private static string Err(string? e) => e != null ? ": " + e : string.Empty;

    private WindowIcon ProfileIcon(AcerProfile p)
    {
        if (_icons.TryGetValue(p, out var cached)) return cached;
        Color c = p switch
        {
            AcerProfile.Quiet => Color.FromRgb(0x42, 0x85, 0xF4),
            AcerProfile.Balanced => Color.FromRgb(0x2E, 0x7D, 0x32),
            AcerProfile.Performance => Color.FromRgb(0xF5, 0x7C, 0x00),
            AcerProfile.Turbo => Color.FromRgb(0xD3, 0x2F, 0x2F),
            AcerProfile.Eco => Color.FromRgb(0x00, 0x89, 0x7B),
            _ => Colors.Gray,
        };
        var icon = MakeIcon(c);
        _icons[p] = icon;
        return icon;
    }

    private static WindowIcon MakeIcon(Color c)
    {
        const int sz = 32;
        var wb = new WriteableBitmap(new PixelSize(sz, sz), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        byte[] px = new byte[sz * sz * 4];
        for (int i = 0; i < sz * sz; i++) { int o = i * 4; px[o] = c.B; px[o + 1] = c.G; px[o + 2] = c.R; px[o + 3] = 255; }
        using (var fb = wb.Lock()) Marshal.Copy(px, 0, fb.Address, px.Length);
        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
