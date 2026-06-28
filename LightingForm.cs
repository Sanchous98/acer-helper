using System.Drawing;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>Lighting window: keyboard (4-zone) and lightbar mode/colour/brightness/speed.</summary>
public sealed class LightingForm : Form
{
    public LightingForm(AcerEneRgb rgb)
    {
        Text            = "Acer Helper — Lighting";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(356, 416);

        var keyboard = new RgbDevicePanel(
            "Keyboard (4 zones)", RgbEffects.Keyboard,
            (e, c, b, s) => rgb.ApplyKeyboard(e.ModeByte, e.IsEffect, b, s, c))
        { Location = new Point(12, 10) };

        var lightbar = new RgbDevicePanel(
            "Lightbar", RgbEffects.Lightbar,
            (e, c, b, s) => rgb.ApplyLightbar(e.ModeByte, e.IsEffect, b, s, c))
        { Location = new Point(12, 202) };

        Controls.Add(keyboard);
        Controls.Add(lightbar);

        if (!rgb.Available)
        {
            keyboard.Enabled = false;
            lightbar.Enabled = false;
            Controls.Add(new Label
            {
                Text      = "RGB device not found: " + (rgb.LastError ?? string.Empty),
                Location  = new Point(14, 394),
                Size      = new Size(330, 18),
                ForeColor = Color.Gray,
            });
        }
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

    /// <summary>A group box with mode/colour/brightness/speed controls for one light.</summary>
    private sealed class RgbDevicePanel : GroupBox
    {
        private readonly RgbEffect[] _effects;
        private readonly Func<RgbEffect, Color, byte, byte, bool> _apply;
        private readonly ComboBox _mode;
        private readonly Button _colorBtn;
        private readonly TrackBar _bri;
        private readonly TrackBar _speed;
        private Color _color = Color.Red;

        public RgbDevicePanel(string title, RgbEffect[] effects, Func<RgbEffect, Color, byte, byte, bool> apply)
        {
            Text     = title;
            _effects = effects;
            _apply   = apply;
            Size     = new Size(332, 182);

            _mode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(14, 26),
                Size          = new Size(184, 24),
            };
            foreach (var e in effects) _mode.Items.Add(e.Name);
            _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += (_, _) => UpdateEnabled();
            Controls.Add(_mode);

            _colorBtn = new Button
            {
                Text      = "Colour",
                Location  = new Point(214, 25),
                Size      = new Size(104, 26),
                BackColor = _color,
                FlatStyle = FlatStyle.Flat,
            };
            _colorBtn.Click += (_, _) => PickColor();
            Controls.Add(_colorBtn);

            Controls.Add(new Label { Text = "Brightness", Location = new Point(14, 60), AutoSize = true });
            _bri = new TrackBar { Location = new Point(12, 78), Size = new Size(306, 40), Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            Controls.Add(_bri);

            Controls.Add(new Label { Text = "Speed", Location = new Point(14, 120), AutoSize = true });
            _speed = new TrackBar { Location = new Point(12, 138), Size = new Size(228, 40), Minimum = 0, Maximum = 9, Value = 5 };
            Controls.Add(_speed);

            var applyBtn = new Button { Text = "Apply", Location = new Point(246, 142), Size = new Size(72, 28) };
            applyBtn.Click += (_, _) => _apply(Current, _color, (byte)_bri.Value, (byte)_speed.Value);
            Controls.Add(applyBtn);

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
}
