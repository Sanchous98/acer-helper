using System.Drawing;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>
/// Compact window: performance profile, live monitoring, fan control and options.
/// Layout is built entirely from TableLayoutPanel/FlowLayoutPanel so it stays
/// aligned at any DPI / text length (no hand-placed pixel coordinates).
/// </summary>
public sealed class MainForm : Form
{
    private readonly Action<AcerProfile> _onApplyProfile;
    private readonly Action<FanMode, byte, byte> _onApplyFan;
    private readonly Action _onOpenLighting;
    private readonly Action<bool> _setClamshell;
    private readonly Action<bool> _onTurboToggleChanged;
    private readonly Action<bool> _setAutostart;
    private readonly System.Windows.Forms.Timer _fanDebounce = new() { Interval = 400 };

    private readonly Dictionary<AcerProfile, Button> _profileButtons = new();
    private CheckBox    _turboToggle = null!;
    private Label       _cpuLabel    = null!;
    private Label       _gpuLabel    = null!;
    private RadioButton _rbAuto      = null!;
    private RadioButton _rbMax       = null!;
    private RadioButton _rbCustom    = null!;
    private TrackBar    _cpuBar      = null!;
    private TrackBar    _gpuBar      = null!;
    private Label       _cpuPct      = null!;
    private Label       _gpuPct      = null!;
    private Label       _statusLabel = null!;

    private static readonly Color Accent = Color.FromArgb(0x2E, 0x7D, 0x32);

    /// <summary>If true, the Turbo key toggles Turbo; otherwise it cycles profiles.</summary>
    public bool TurboToggles => _turboToggle.Checked;

    public MainForm(Action<AcerProfile> onApplyProfile, Action<FanMode, byte, byte> onApplyFan,
                    Action onOpenLighting, Func<bool> clamshellEnabled, Action<bool> setClamshell,
                    bool turboToggles, Action<bool> onTurboToggleChanged,
                    Func<bool> autostartEnabled, Action<bool> setAutostart)
    {
        _onApplyProfile       = onApplyProfile;
        _onApplyFan           = onApplyFan;
        _onOpenLighting       = onOpenLighting;
        _setClamshell         = setClamshell;
        _onTurboToggleChanged = onTurboToggleChanged;
        _setAutostart         = setAutostart;

        // debounce: apply fan speed once, ~400 ms after the slider stops moving
        _fanDebounce.Tick += (_, _) => { _fanDebounce.Stop(); ApplyNow(); };

        SuspendLayout();

        Text            = "Acer Helper";
        Font            = new Font("Segoe UI", 9F);
        AutoScaleMode   = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;   // app lives in the tray; close (X) just hides it
        StartPosition   = FormStartPosition.Manual;   // we place it next to the tray
        ShowInTaskbar   = false;
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            Dock         = DockStyle.Fill,
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(12),
            MinimumSize  = new Size(384, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(WrapGroup("Performance profile", BuildProfiles()));
        root.Controls.Add(WrapGroup("Monitoring",          BuildMonitor()));
        root.Controls.Add(WrapGroup("Fans",                BuildFans()));
        root.Controls.Add(WrapGroup("Options",             BuildOptions(clamshellEnabled, turboToggles, autostartEnabled)));
        root.Controls.Add(BuildBottom());

        Controls.Add(root);
        ResumeLayout(true);

        UpdateFanEnabled();
    }

    /// <summary>Wrap a control in an auto-sizing GroupBox that fills its row width.</summary>
    private static GroupBox WrapGroup(string title, Control inner)
    {
        inner.Dock = DockStyle.Fill;
        var g = new GroupBox
        {
            Text         = title,
            Dock         = DockStyle.Fill,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(10, 8, 10, 10),
            Margin       = new Padding(0, 0, 0, 10),
        };
        g.Controls.Add(inner);
        return g;
    }

    private Control BuildProfiles()
    {
        int n = AcerProfileInfo.All.Length;
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = n, RowCount = 1 };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        foreach (var p in AcerProfileInfo.All)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
            var btn = new Button
            {
                Text      = AcerProfileInfo.DisplayName(p),
                Tag       = p,
                Dock      = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Margin    = new Padding(2),
            };
            btn.Click += (s, _) => _onApplyProfile((AcerProfile)((Button)s!).Tag!);
            _profileButtons[p] = btn;
            t.Controls.Add(btn);
        }
        return t;
    }

    private Control BuildMonitor()
    {
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2 };
        _cpuLabel = new Label { Text = "CPU:  …", AutoSize = true, Margin = new Padding(2, 3, 2, 3) };
        _gpuLabel = new Label { Text = "GPU:  …", AutoSize = true, Margin = new Padding(2, 3, 2, 3) };
        t.Controls.Add(_cpuLabel, 0, 0);
        t.Controls.Add(_gpuLabel, 0, 1);
        return t;
    }

    private Control BuildFans()
    {
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));

        // row 0: mode radios
        var modes = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4) };
        _rbAuto   = new RadioButton { Text = "Auto",   AutoSize = true, Checked = true };
        _rbMax    = new RadioButton { Text = "Max",    AutoSize = true };
        _rbCustom = new RadioButton { Text = "Custom", AutoSize = true };
        _rbAuto.CheckedChanged   += (s, _) => OnFanModeChanged(s);
        _rbMax.CheckedChanged    += (s, _) => OnFanModeChanged(s);
        _rbCustom.CheckedChanged += (s, _) => OnFanModeChanged(s);
        modes.Controls.Add(_rbAuto);
        modes.Controls.Add(_rbMax);
        modes.Controls.Add(_rbCustom);
        t.Controls.Add(modes, 0, 0);
        t.SetColumnSpan(modes, 3);

        // row 1: CPU fan
        _cpuBar = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, AutoSize = false, Height = 40 };
        _cpuPct = new Label { Text = "70%", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 10, 2, 0) };
        _cpuBar.ValueChanged += (_, _) => { _cpuPct.Text = _cpuBar.Value + "%"; DebounceFanApply(); };
        t.Controls.Add(new Label { Text = "CPU fan", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 10, 2, 0) }, 0, 1);
        t.Controls.Add(_cpuBar, 1, 1);
        t.Controls.Add(_cpuPct, 2, 1);

        // row 2: GPU fan
        _gpuBar = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, AutoSize = false, Height = 40 };
        _gpuPct = new Label { Text = "70%", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 10, 2, 0) };
        _gpuBar.ValueChanged += (_, _) => { _gpuPct.Text = _gpuBar.Value + "%"; DebounceFanApply(); };
        t.Controls.Add(new Label { Text = "GPU fan", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 10, 2, 0) }, 0, 2);
        t.Controls.Add(_gpuBar, 1, 2);
        t.Controls.Add(_gpuPct, 2, 2);

        // row 3: apply
        var apply = new Button { Text = "Apply fans", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(2, 6, 2, 0), Padding = new Padding(10, 3, 10, 3) };
        apply.Click += (_, _) => _onApplyFan(SelectedMode(), (byte)_cpuBar.Value, (byte)_gpuBar.Value);
        t.Controls.Add(apply, 1, 3);
        t.SetColumnSpan(apply, 2);

        return t;
    }

    private Control BuildOptions(Func<bool> clamshellEnabled, bool turboToggles, Func<bool> autostartEnabled)
    {
        var t = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };

        var clamshell = new CheckBox { Text = "Stay awake when lid closed (docked, on AC)", AutoSize = true, Checked = clamshellEnabled(), Margin = new Padding(2) };
        clamshell.CheckedChanged += (s, _) => _setClamshell(((CheckBox)s!).Checked);

        _turboToggle = new CheckBox { Text = "Turbo key toggles Turbo (otherwise cycles profiles)", AutoSize = true, Checked = turboToggles, Margin = new Padding(2) };
        _turboToggle.CheckedChanged += (s, _) => _onTurboToggleChanged(((CheckBox)s!).Checked);

        var autostart = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = autostartEnabled(), Margin = new Padding(2) };
        autostart.CheckedChanged += (s, _) => _setAutostart(((CheckBox)s!).Checked);

        t.Controls.Add(clamshell);
        t.Controls.Add(_turboToggle);
        t.Controls.Add(autostart);
        return t;
    }

    private Control BuildBottom()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel = new Label { Text = string.Empty, AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft };
        var lighting = new Button { Text = "Lighting…", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(2) };
        lighting.Click += (_, _) => _onOpenLighting();

        t.Controls.Add(_statusLabel, 0, 0);
        t.Controls.Add(lighting, 1, 0);
        return t;
    }

    private FanMode SelectedMode() =>
        _rbMax.Checked ? FanMode.Max : _rbCustom.Checked ? FanMode.Custom : FanMode.Auto;

    private void UpdateFanEnabled()
    {
        bool custom = _rbCustom.Checked;
        _cpuBar.Enabled = custom;
        _gpuBar.Enabled = custom;
    }

    private void OnFanModeChanged(object? sender)
    {
        UpdateFanEnabled();
        if (sender is RadioButton rb && rb.Checked) ApplyNow();   // apply live on mode switch
    }

    private void DebounceFanApply()
    {
        if (!_rbCustom.Checked) return;      // only custom uses the sliders
        _fanDebounce.Stop();
        _fanDebounce.Start();                // restart; applies once after the slider settles
    }

    private void ApplyNow()
        => _onApplyFan(SelectedMode(), (byte)_cpuBar.Value, (byte)_gpuBar.Value);

    /// <summary>Place the window in the bottom-right corner, just above the tray.</summary>
    public void PositionNearTray()
    {
        Size sz = AutoSize ? PreferredSize : Size;
        Screen screen = Screen.PrimaryScreen ?? Screen.FromControl(this);
        Rectangle wa = screen.WorkingArea;
        const int margin = 12;
        Location = new Point(
            Math.Max(wa.Left, wa.Right  - sz.Width  - margin),
            Math.Max(wa.Top,  wa.Bottom - sz.Height - margin));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionNearTray();   // final size is known once shown
    }

    /// <summary>Closing the window hides it to tray instead of exiting.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void RefreshState(AcerProfile? current, byte mask, SensorSnapshot s, string? status)
    {
        foreach (var kv in _profileButtons)
        {
            bool supported = mask == 0 || AcerProfileInfo.IsSupported(mask, kv.Key);
            bool active    = current.HasValue && current.Value == kv.Key;

            kv.Value.Enabled = supported;
            kv.Value.BackColor = active ? Accent : SystemColors.Control;
            kv.Value.ForeColor = active ? Color.White : SystemColors.ControlText;
            kv.Value.FlatAppearance.BorderColor = active ? Accent : SystemColors.ControlDark;
        }

        _cpuLabel.Text = $"CPU:   {Fmt(s.CpuTempC, "°C")}    {Fmt(s.CpuFanRpm, "rpm")}";
        _gpuLabel.Text = $"GPU:   {Fmt(s.GpuTempC, "°C")}    {Fmt(s.GpuFanRpm, "rpm")}";
        _statusLabel.Text = status ?? string.Empty;
    }

    private static string Fmt(int v, string unit) => v < 0 ? "—" : $"{v} {unit}";
}
