using System.Drawing;
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
    private readonly TurboKeyWatcher _turbo;
    private AcerProfile _lastNonTurbo = AcerProfile.Balanced;
    private DateTime _lastTurboPress = DateTime.MinValue;
    private readonly NotifyIcon _tray;
    private readonly MainForm _form;
    private readonly LightingForm _lighting;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<AcerProfile, ToolStripMenuItem> _menuItems = new();

    public TrayAppContext()
    {
        _lighting = new LightingForm(_rgb);
        _form = new MainForm(ApplyProfile, ApplyFan, ShowLighting,
                             () => _clamshell.Enabled, _clamshell.SetEnabled);

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

        _turbo = new TurboKeyWatcher(OnTurbo);

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
            _tray.ShowBalloonTip(3000, "Acer Helper",
                "Failed to set " + AcerProfileInfo.DisplayName(p) +
                (_wmi.LastError != null ? ": " + _wmi.LastError : string.Empty),
                ToolTipIcon.Error);
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
            _tray.ShowBalloonTip(3000, "Acer Helper",
                $"Fans: {what} failed" + (_wmi.LastError != null ? " — " + _wmi.LastError : string.Empty),
                ToolTipIcon.Error);
        }
        Refresh();
    }

    private void OnTurbo()
    {
        // debounce paired press/release reports
        DateTime now = DateTime.UtcNow;
        if ((now - _lastTurboPress).TotalMilliseconds < 350) return;
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
            _tray.ShowBalloonTip(1200, "Acer Helper", "Profile: " + AcerProfileInfo.DisplayName(target), ToolTipIcon.Info);
        Refresh();
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

        foreach (var kv in _menuItems)
        {
            kv.Value.Checked = current.HasValue && current.Value == kv.Key;
            kv.Value.Enabled = mask == 0 || AcerProfileInfo.IsSupported(mask, kv.Key);
        }
    }

    private void ShowForm()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
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
        _turbo.Dispose();
        ExitThread();
    }
}
