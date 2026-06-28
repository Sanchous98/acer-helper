using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AcerHelper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // single instance
        using var mutex = new Mutex(true, "AcerHelper_SingleInstance_8F1C", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

/// <summary>Owns the WMI layer, the tray icon and the window.</summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly AcerWmi _wmi = new();
    private readonly AcerEneRgb _rgb = new();
    private readonly ClamshellManager _clamshell = new();
    private readonly AcerBattery _battery = new();
    private readonly AcerApGe _apge = new();
    private readonly AcerHotkeyWatcher _hotkeys;
    private readonly Settings _settings = Settings.Load();
    private readonly Dictionary<AcerProfile, Icon> _icons = new();
    private AcerProfile _lastNonTurbo = AcerProfile.Balanced;
    private DateTime _lastTurboPress = DateTime.MinValue;
    private DateTime _lastNotify = DateTime.MinValue;
    private readonly NotifyIcon _tray;
    private readonly MainForm _form;
    private readonly LightingForm _lighting;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<AcerProfile, ToolStripMenuItem> _menuItems = new();

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayAppContext()
    {
        _lighting = new LightingForm(_rgb);
        _form = new MainForm(ApplyProfile, ApplyFan, ShowLighting,
                             () => _clamshell.Enabled,
                             b => { _clamshell.SetEnabled(b); _settings.Clamshell = b; _settings.Save(); },
                             _settings.TurboToggles,
                             b => { _settings.TurboToggles = b; _settings.Save(); },
                             Autostart.IsEnabled,
                             b => Autostart.SetEnabled(b),
                             BuildHardwareToggles());

        // restore persisted clamshell preference
        if (_settings.Clamshell) _clamshell.SetEnabled(true);

        var menu = new ContextMenuStrip();
        foreach (var p in AcerProfileInfo.All)
        {
            var item = new ToolStripMenuItem(AcerProfileInfo.DisplayName(p)) { Tag = p };
            item.Click += (s, _) => ApplyProfile((AcerProfile)((ToolStripMenuItem)s!).Tag!);
            _menuItems[p] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Show", null, (_, _) => ShowForm()));
        menu.Items.Add(new ToolStripMenuItem("Lighting…", null, (_, _) => ShowLighting()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Visible          = true,
            Text             = "Acer Helper",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        if (!_wmi.Available)
        {
            MessageBox.Show(
                "Acer gaming WMI interface (AcerGamingFunction) was not found.\n\n" +
                (_wmi.LastError ?? string.Empty) +
                "\n\nThis app must run as Administrator on an Acer gaming laptop.",
                "Acer Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _hotkeys = new AcerHotkeyWatcher();
        _hotkeys.Pressed += key =>
        {
            switch (key)
            {
                case AcerHotkey.Turbo: OnTurbo(); break;
                case AcerHotkey.Nitro: OnNitro(); break;
            }
        };

        Refresh();

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        ShowForm();
    }

    private void ApplyProfile(AcerProfile p)
    {
        if (!_wmi.SetProfile(p))
        {
            Notify("Failed to set " + AcerProfileInfo.DisplayName(p) +
                   (_wmi.LastError != null ? ": " + _wmi.LastError : string.Empty), ToolTipIcon.Error);
        }
        Refresh();
    }

    private void ApplyFan(FanMode mode, byte cpuPercent, byte gpuPercent)
    {
        bool ok = mode == FanMode.Custom
            ? _wmi.SetCustomSpeeds(cpuPercent, gpuPercent)
            : _wmi.SetFanMode(mode);

        if (!ok)
        {
            string what = mode == FanMode.Custom ? $"Custom {cpuPercent}%/{gpuPercent}%" : mode.ToString();
            Notify($"Fans: {what} failed" + (_wmi.LastError != null ? " - " + _wmi.LastError : string.Empty), ToolTipIcon.Error);
        }
        Refresh();
    }

    private void OnTurbo()
    {
        // debounce: the Turbo key emits a burst of reports per press
        DateTime now = DateTime.UtcNow;
        if ((now - _lastTurboPress).TotalMilliseconds < 800) return;
        _lastTurboPress = now;

        byte mask = _wmi.GetSupportedMask();
        AcerProfile? cur = _wmi.GetProfile();
        AcerProfile target;

        if (_form.TurboToggles)
        {
            if (cur == AcerProfile.Turbo)
            {
                target = _lastNonTurbo;
            }
            else
            {
                if (cur.HasValue) _lastNonTurbo = cur.Value;
                target = AcerProfile.Turbo;
            }
        }
        else
        {
            target = NextSupported(cur, mask);
        }

        if (_wmi.SetProfile(target))
            Notify("Profile: " + AcerProfileInfo.DisplayName(target));
        Refresh();
    }

    /// <summary>Show a tray balloon, throttled so bursts don't spam notifications.</summary>
    private void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastNotify).TotalMilliseconds < 1500) return;
        _lastNotify = now;
        _tray.ShowBalloonTip(1500, "Acer Helper", text, icon);
    }

    private static AcerProfile NextSupported(AcerProfile? cur, byte mask)
    {
        AcerProfile[] all = AcerProfileInfo.All;
        int start = 0;
        if (cur.HasValue)
            for (int i = 0; i < all.Length; i++) if (all[i] == cur.Value) { start = i; break; }

        for (int step = 1; step <= all.Length; step++)
        {
            AcerProfile cand = all[(start + step) % all.Length];
            if (mask == 0 || AcerProfileInfo.IsSupported(mask, cand)) return cand;
        }
        return cur ?? AcerProfile.Balanced;
    }

    private void Refresh()
    {
        _clamshell.Evaluate();   // re-apply clamshell for current display/power state

        AcerProfile? current = _wmi.GetProfile();
        byte mask = _wmi.GetSupportedMask();
        SensorSnapshot sensors = _wmi.ReadSensors();

        string status = _wmi.Available
            ? string.Empty
            : "WMI unavailable — run as Administrator on an Acer gaming laptop.";

        _form.RefreshState(current, mask, sensors, status);

        _tray.Text = "Acer Helper — " +
            (current.HasValue ? AcerProfileInfo.DisplayName(current.Value) : "?");

        if (current.HasValue) _tray.Icon = ProfileIcon(current.Value);

        foreach (var kv in _menuItems)
        {
            kv.Value.Checked = current.HasValue && current.Value == kv.Key;
            kv.Value.Enabled = mask == 0 || AcerProfileInfo.IsSupported(mask, kv.Key);
        }
    }

    private DateTime _lastNitroPress = DateTime.MinValue;

    /// <summary>Nitro key: toggle our window next to the tray.</summary>
    private void OnNitro()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastNitroPress).TotalMilliseconds < 600) return;
        _lastNitroPress = now;

        if (_form.Visible) _form.Hide();
        else               ShowForm();
    }

    /// <summary>Build the hardware on/off toggles, reading current state and support.</summary>
    private IReadOnlyList<OptionToggle> BuildHardwareToggles()
    {
        bool? batt = _battery.GetLimit();
        bool? usb  = _apge.GetUsbCharging();
        bool? lcd  = _wmi.GetLcdOverdrive();
        bool? kbd  = _apge.GetBacklightTimeout();

        return new List<OptionToggle>
        {
            new("Battery charge limit (~80%)",   batt.HasValue, batt ?? false,
                b => { if (!_battery.SetLimit(b))          NotifyFail("Battery limit",    _battery.LastError); }),
            new("USB charging when powered off", usb.HasValue,  usb  ?? false,
                b => { if (!_apge.SetUsbCharging(b))       NotifyFail("USB charging",     _apge.LastError); }),
            new("LCD overdrive",                 lcd.HasValue,  lcd  ?? false,
                b => { if (!_wmi.SetLcdOverdrive(b))       NotifyFail("LCD overdrive",    _wmi.LastError); }),
            new("Keyboard backlight timeout",    kbd.HasValue,  kbd  ?? false,
                b => { if (!_apge.SetBacklightTimeout(b))  NotifyFail("Backlight timeout", _apge.LastError); }),
        };
    }

    private void NotifyFail(string what, string? err)
        => Notify(what + " failed" + (err != null ? ": " + err : string.Empty), ToolTipIcon.Error);

    /// <summary>A small colour-coded tray icon for the active profile (cached).</summary>
    private Icon ProfileIcon(AcerProfile p)
    {
        if (_icons.TryGetValue(p, out Icon? cached)) return cached;

        Color c = p switch
        {
            AcerProfile.Quiet       => Color.FromArgb(0x42, 0x85, 0xF4), // blue
            AcerProfile.Balanced    => Color.FromArgb(0x2E, 0x7D, 0x32), // green
            AcerProfile.Performance => Color.FromArgb(0xF5, 0x7C, 0x00), // orange
            AcerProfile.Turbo       => Color.FromArgb(0xD3, 0x2F, 0x2F), // red
            AcerProfile.Eco         => Color.FromArgb(0x00, 0x89, 0x7B), // teal
            _                       => Color.Gray,
        };
        char letter = AcerProfileInfo.DisplayName(p) is { Length: > 0 } n ? n[0] : '?';

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var br = new SolidBrush(c);
            g.FillEllipse(br, 0, 0, 15, 15);
            using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(letter.ToString(), f, Brushes.White, new RectangleF(0, 0, 16, 16), fmt);
        }
        Icon icon = Icon.FromHandle(bmp.GetHicon());
        _icons[p] = icon;
        return icon;
    }

    private void ShowForm()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.PositionNearTray();   // re-anchor next to the tray on every show
        _form.Activate();
    }

    private void ShowLighting()
    {
        _lighting.Show();
        _lighting.WindowState = FormWindowState.Normal;
        _lighting.Activate();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _wmi.Dispose();
        _rgb.Dispose();
        _clamshell.Dispose();
        _battery.Dispose();
        _apge.Dispose();
        _hotkeys.Dispose();
        foreach (Icon i in _icons.Values) { DestroyIcon(i.Handle); i.Dispose(); }
        ExitThread();
    }
}
