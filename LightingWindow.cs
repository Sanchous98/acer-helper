using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SDColor = System.Drawing.Color;

namespace AcerHelper;

/// <summary>Lighting window: keyboard (all zones), per-zone, lightbar.</summary>
public sealed class LightingWindow : Window
{
    private static readonly byte StaticMode = RgbEffects.Keyboard[0].ModeByte;

    public LightingWindow(IRgb rgb)
    {
        Title = "Acer Helper — Lighting";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 0 };

        if (!rgb.Available)
        {
            root.Children.Add(new TextBlock
            {
                Text = "RGB device not found: " + (rgb.LastError ?? string.Empty),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            root.Children.Add(DevicePanel("Keyboard (all zones)", RgbEffects.Keyboard,
                (e, c, b, s) => rgb.ApplyKeyboard(e.ModeByte, e.IsEffect, b, s, c)));
            root.Children.Add(ZonePanel(rgb));
            root.Children.Add(DevicePanel("Lightbar", RgbEffects.Lightbar,
                (e, c, b, s) => rgb.ApplyLightbar(e.ModeByte, e.IsEffect, b, s, c)));
        }

        Content = root;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    private static Control Section(string header, Control content) => new Border
    {
        BorderBrush = Brushes.Gray,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = header, FontWeight = FontWeight.SemiBold }, content } },
    };

    private static Control DevicePanel(string title, RgbEffect[] effects, Func<RgbEffect, SDColor, byte, byte, bool> apply)
    {
        var combo = new ComboBox { ItemsSource = effects.Select(e => e.Name).ToList(), SelectedIndex = 0, MinWidth = 160 };
        var picker = new ColorPicker { Color = Colors.Red };
        var bri = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 240, TickFrequency = 10 };
        var spd = new Slider { Minimum = 0, Maximum = 9, Value = 5, Width = 240 };
        var applyBtn = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };

        void UpdateEnabled()
        {
            var e = effects[Math.Max(combo.SelectedIndex, 0)];
            picker.IsEnabled = e.HasColor;
            spd.IsEnabled = e.HasSpeed;
        }
        combo.SelectionChanged += (_, _) => UpdateEnabled();
        applyBtn.Click += (_, _) =>
        {
            var e = effects[Math.Max(combo.SelectedIndex, 0)];
            var c = picker.Color;
            apply(e, SDColor.FromArgb(c.R, c.G, c.B), (byte)bri.Value, (byte)spd.Value);
        };
        UpdateEnabled();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { combo, picker } };
        var content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                top,
                new TextBlock { Text = "Brightness" }, bri,
                new TextBlock { Text = "Speed" }, spd,
                applyBtn,
            },
        };
        return Section(title, content);
    }

    private static Control ZonePanel(IRgb rgb)
    {
        var pickers = new ColorPicker[4];
        SDColor[] defaults = { SDColor.Red, SDColor.Lime, SDColor.Blue, SDColor.Magenta };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (int i = 0; i < 4; i++)
        {
            var p = new ColorPicker { Color = Color.FromRgb(defaults[i].R, defaults[i].G, defaults[i].B) };
            pickers[i] = p;
            row.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Zone " + (i + 1) }, p } });
        }

        var bri = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 240, TickFrequency = 10 };
        var applyBtn = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };
        applyBtn.Click += (_, _) =>
        {
            byte b = (byte)bri.Value;
            for (int i = 0; i < 4; i++)
            {
                var c = pickers[i].Color;
                rgb.ApplyKeyboardZone(i, StaticMode, isEffect: false, b, speed: 0, SDColor.FromArgb(c.R, c.G, c.B));
            }
        };

        var content = new StackPanel { Spacing = 6, Children = { row, new TextBlock { Text = "Brightness" }, bri, applyBtn } };
        return Section("Keyboard — per-zone (static)", content);
    }
}
