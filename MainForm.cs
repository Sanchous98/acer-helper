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
        var grpFans = new Gro