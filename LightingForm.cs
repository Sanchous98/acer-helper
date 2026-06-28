using System.Drawing;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>Lighting window: keyboard (all zones), per-zone keyboard, and lightbar.</summary>
public sealed class LightingForm : Form
{
    public LightingForm(AcerEneRgb rgb)
    {
        Text            = "Acer Helper — Lighting";
        Font            = new Font("Segoe UI", 9F);
        AutoScaleMode   = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;   // tray app; close (X) just hides the window
        StartPosition   = FormStartPosition.CenterScreen;
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;

        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock         = DockStyle.Fill,
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(12),
            MinimumSize  = new Size(392, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var keyboard = new RgbDevicePanel("Keyboard (all zones)", RgbEffects.Keyboard,
            (e, c, b, s) => rgb.ApplyKeyboard(e.ModeByte, e.IsEffect, b, s, c));
        var zones    = new KeyboardZonePanel(rgb);
        var lightbar = new RgbDevicePanel("Lightbar", RgbEffects.Lightbar,
            (e, c, b, s) => rgb.ApplyLightbar(e.ModeByte, e.IsEffect, b, s, c));

        root.Controls.Add(keyboard);
        root.Controls.Add(zones);
        root.Controls.Add(lightbar);

        if (!rgb.Available)
        {
            keyboard.Enabled = false;
            zones.Enabled    = false;
            lightbar.Enabled = false;
            root.Controls.Add(new Label
            {
                Text      = "RGB device not found: " + (rgb.LastError ?? string.Empty),
                AutoSize  = true,
                ForeColor = Color.Gray,
                Margin    = new Padding(2, 4, 2, 2),
            });
        }

        Controls.Add(root);
        ResumeLayout(true);
    }

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

    /// <summary>Mode / colour / brightness / speed controls for one light.</summary>
    private sealed class RgbDevicePanel : GroupBox
    {
        private readonly RgbEffect[] _effects;
        private readonly Func<RgbEffect, Color, byte, byte, bool> _apply;
        private readonly ComboBox _mode;
        private readonly Button   _colorBtn;
        private readonly TrackBar _bri;
        private readonly TrackBar _speed;
        private Color _color = Color.Red;

        public RgbDevicePanel(string title, RgbEffect[] effects, Func<RgbEffect, Color, byte, byte, bool> apply)
        {
            _effects     = effects;
            _apply       = apply;
            Text         = title;
            Dock         = DockStyle.Fill;
            AutoSize     = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding      = new Padding(10, 8, 10, 10);
            Margin       = new Padding(0, 0, 0, 10);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 5 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 6) };
            foreach (var e in effects) _mode.Items.Add(e.Name);
            _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += (_, _) => UpdateEnabled();

            _colorBtn = new Button { Text = "Colour…", Dock = DockStyle.Fill, BackColor = _color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 2, 2, 6) };
            _colorBtn.Click += (_, _) => PickColor();

            t.Controls.Add(_mode, 0, 0);
            t.Controls.Add(_colorBtn, 1, 0);

            t.Controls.Add(new Label { Text = "Brightness", AutoSize = true, Margin = new Padding(2, 4, 2, 0) }, 0, 1);
            _bri = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10, AutoSize = false, Height = 40 };
            t.Controls.Add(_bri, 0, 2);
            t.SetColumnSpan(_bri, 2);

            t.Controls.Add(new Label { Text = "Speed", AutoSize = true, Margin = new Padding(2, 4, 2, 0) }, 0, 3);
            _speed = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 9, Value = 5, AutoSize = false, Height = 40 };
            var applyBtn = new Button { Text = "Apply", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(12, 3, 12, 3), Margin = new Padding(2, 6, 2, 0) };
            applyBtn.Click += (_, _) => _apply(Current, _color, (byte)_bri.Value, (byte)_speed.Value);
            t.Controls.Add(_speed, 0, 4);
            t.Controls.Add(applyBtn, 1, 4);

            Controls.Add(t);
            UpdateEnabled();
        }

        private RgbEffect Current => _effects[_mode.SelectedIndex];

        private void UpdateEnabled()
        {
            _colorBtn.Enabled = Current.HasColor;
            _speed.Enabled    = Current.HasSpeed;
        }

        private void PickColor()
        {
            using var dlg = new ColorDialog { Color = _color, FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _color = dlg.Color;
                _colorBtn.BackColor = _color;
            }
        }
    }

    /// <summary>Set a static colour for each of the four keyboard zones independently.</summary>
    private sealed class KeyboardZonePanel : GroupBox
    {
        private static readonly byte StaticMode = RgbEffects.Keyboard[0].ModeByte;
        private readonly AcerEneRgb _rgb;
        private readonly Button[] _zoneBtns = new Button[4];
        private readonly Color[]  _colors   = { Color.Red, Color.Lime, Color.Blue, Color.Magenta };
        private readonly TrackBar _bri;

        public KeyboardZonePanel(AcerEneRgb rgb)
        {
            _rgb         = rgb;
            Text         = "Keyboard — per-zone (static)";
            Dock         = DockStyle.Fill;
            AutoSize     = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding      = new Padding(10, 8, 10, 10);
            Margin       = new Padding(0, 0, 0, 10);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            t.Controls.Add(new Label { Text = "Zone colours (left → right)", AutoSize = true, Margin = new Padding(2, 2, 2, 4) }, 0, 0);

            // row 1: four equal-width zone buttons
            var zonesRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1 };
            zonesRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                zonesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                var btn = new Button
                {
                    Text      = (i + 1).ToString(),
                    Dock      = DockStyle.Fill,
                    BackColor = _colors[i],
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Margin    = new Padding(2),
                };
                btn.Click += (_, _) => PickZone(idx);
                _zoneBtns[i] = btn;
                zonesRow.Controls.Add(btn);
            }
            t.Controls.Add(zonesRow, 0, 1);

            // row 2: brightness label + slider + apply
            _bri = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10, AutoSize = false, Height = 40 };
            var applyBtn = new Button { Text = "Apply", AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(12, 3, 12, 3), Margin = new Padding(2, 8, 2, 0) };
            applyBtn.Click += (_, _) => ApplyZones();

            var briRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1 };
            briRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            briRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            briRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            briRow.Controls.Add(new Label { Text = "Brightness", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 12, 8, 0) }, 0, 0);
            briRow.Controls.Add(_bri, 1, 0);
            briRow.Controls.Add(applyBtn, 2, 0);
            t.Controls.Add(briRow, 0, 2);

            Controls.Add(t);
        }

        private void PickZone(int i)
        {
            using var dlg = new ColorDialog { Color = _colors[i], FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _colors[i] = dlg.Color;
                _zoneBtns[i].BackColor = dlg.Color;
            }
        }

        private void ApplyZones()
        {
            byte bri = (byte)_bri.Value;
            for (int i = 0; i < 4; i++)
                _rgb.ApplyKeyboardZone(i, StaticMode, isEffect: false, bri, speed: 0, _colors[i]);
        }
    }
}
