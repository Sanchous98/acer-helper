using System.Drawing;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>Compact window: performance profile, live monitoring, fan control.</summary>
public sealed class MainForm : Form
{
    private readonly Action<AcerProfile> _onApplyProfile;
    private readonly Action<FanMode, byte, byte> _onApplyFan;
    private readonly Action _onOpenLighting;
    private readonly Action<bool> _setClamshell;
    private readonly System.Windows.Forms.Timer _fanDebounce = new() { Interval = 400 };
    private CheckBox _turboToggle = null!;

    /// <summary>If true, the Turbo key toggles Turbo; otherwise it cycles profiles.</summary>
    public bool TurboToggles => _turboToggle.Checked;

    private readonly Dictionary<AcerProfile, Button> _profileButtons = new();
    private readonly Label _cpuLabel;
    private readonly Label _gpuLabel;
    private readonly RadioButton _rbAuto;
    private readonly RadioButton _rbMax;
    private readonly RadioButton _rbCustom;
    private readonly TrackBar _cpuBar;
    private readonly TrackBar _gpuBar;
    private readonly Label _cpuPct;
    private readonly Label _gpuPct;
    private readonly Label _statusLabel;

    private static readonly Color Accent = Color.FromArgb(0x2E, 0x7D, 0x32);

    public MainForm(Action<AcerProfile> onApplyProfile, Action<FanMode, byte, byte> onApplyFan,
                    Action onOpenLighting, Func<bool> clamshellEnabled, Action<bool> setClamshell)
    {
        _onApplyProfile = onApplyProfile;
        _onApplyFan     = onApplyFan;
        _onOpenLighting = onOpenLighting;
        _setClamshell   = setClamshell;

        // debounce: apply fan speed once, ~400 ms after the slider stops moving
        _fanDebounce.Tick += (_, _) => { _fanDebounce.Stop(); ApplyNow(); };

        Text            = "Acer Helper";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(340, 500);

        // ---- Performance profile ----
        var grpProfile = new GroupBox { Text = "Performance profile", Location = new Point(10, 8), Size = new Size(320, 84) };
        int bx = 10;
        foreach (var p in AcerProfileInfo.All)
        {
            var btn = new Button
            {
                Text      = AcerProfileInfo.DisplayName(p),
                Location  = new Point(bx, 26),
                Size      = new Size(58, 44),
                Tag       = p,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font(Font.FontFamily, 7.5f),
            };
            btn.Click += (s, _) => _onApplyProfile((AcerProfile)((Button)s!).Tag!);
            _profileButtons[p] = btn;
            grpProfile.Controls.Add(btn);
            bx += 61;
        }
        Controls.Add(grpProfile);

        // ---- Monitoring ----
        var grpSensors = new GroupBox { Text = "Monitoring", Location = new Point(10, 98), Size = new Size(320, 62) };
        _cpuLabel = new Label { Text = "CPU:  …", Location = new Point(14, 22), AutoSize = true };
        _gpuLabel = new Label { Text = "GPU:  …", Location = new Point(14, 40), AutoSize = true };
        grpSensors.Controls.Add(_cpuLabel);
        grpSensors.Controls.Add(_gpuLabel);
        Controls.Add(grpSensors);

        // ---- Fans ----
        var grpFans = new GroupBox { Text = "Fans", Location = new Point(10, 166), Size = new Size(320, 212) };

        _rbAuto   = new RadioButton { Text = "Auto",   Location = new Point(14, 24),  AutoSize = true, Checked = true };
        _rbMax    = new RadioButton { Text = "Max",    Location = new Point(96, 24),  AutoSize = true };
        _rbCustom = new RadioButton { Text = "Custom", Location = new Point(170, 24), AutoSize = true };
        _rbAuto.CheckedChanged   += (s, _) => OnFanModeChanged(s);
        _rbMax.CheckedChanged    += (s, _) => OnFanModeChanged(s);
        _rbCustom.CheckedChanged += (s, _) => OnFanModeChanged(s);
        grpFans.Controls.Add(_rbAuto);
        grpFans.Controls.Add(_rbMax);
        grpFans.Controls.Add(_rbCustom);

        grpFans.Controls.Add(new Label { Text = "CPU fan", Location = new Point(14, 58), AutoSize = true });
        _cpuPct = new Label { Text = "70%", Location = new Point(272, 58), AutoSize = true };
        _cpuBar = new TrackBar { Location = new Point(12, 76), Size = new Size(296, 40), Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10 };
        _cpuBar.ValueChanged += (_, _) => { _cpuPct.Text = _cpuBar.Value + "%"; DebounceFanApply(); };
        grpFans.Controls.Add(_cpuPct);
        grpFans.Controls.Add(_cpuBar);

        grpFans.Controls.Add(new Label { Text = "GPU fan", Location = new Point(14, 118), AutoSize = true });
        _gpuPct = new Label { Text = "70%", Location = new Point(272, 118), AutoSize = true };
        _gpuBar = new TrackBar { Location = new Point(12, 136), Size = new Size(296, 40), Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10 };
        _gpuBar.ValueChanged += (_, _) => { _gpuPct.Text = _gpuBar.Value + "%"; DebounceFanApply(); };
        grpFans.Controls.Add(_gpuPct);
        grpFans.Controls.Add(_gpuBar);

        var applyFan = new Button { Text = "Apply fans", Location = new Point(208, 178), Size = new Size(100, 26) };
        applyFan.Click += (_, _) => _onApplyFan(SelectedMode(), (byte)_cpuBar.Value, (byte)_gpuBar.Value);
        grpFans.Controls.Add(applyFan);

        Controls.Add(grpFans);

        var clamshell = new CheckBox
        {
            Text     = "Keep awake with lid closed (when docked on AC)",
            Location = new Point(14, 384),
            AutoSize = true,
            Checked  = clamshellEnabled(),
        };
        clamshell.CheckedChanged += (s, _) => _setClamshell(((CheckBox)s!).Checked);
        Controls.Add(clamshell);

        _turboToggle = new CheckBox
        {
            Text     = "Turbo key: toggle Turbo (else cycle profiles)",
            Location = new Point(14, 408),
            AutoSize = true,
        };
        Controls.Add(_turboToggle);

        var lightingBtn = new Button { Text = "Lighting…", Location = new Point(236, 436), Size = new Size(92, 28) };
        lightingBtn.Click += (_, _) => _onOpenLighting();
        Controls.Add(lightingBtn);

        _statusLabel = new Label { Text = string.Empty, Location = new Point(12, 440), Size = new Size(216, 40), ForeColor = Color.Gray };
        Controls.Add(_statusLabel);

        UpdateFanEnabled();
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
