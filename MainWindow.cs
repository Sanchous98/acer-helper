using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcerHelper;

/// <summary>Compact main window: profile, monitoring, fans, options. Built in code.</summary>
public sealed class MainWindow : Window
{
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x7D, 0x32);

    private readonly Action<AcerProfile> _onApplyProfile;
    private readonly Action<FanMode, byte, byte> _onApplyFan;
    private readonly Action<bool> _onTurboToggleChanged;
    private readonly Action<int, int, int> _onFanSettingsChanged;

    private readonly Dictionary<AcerProfile, Button> _profileButtons = new();
    private readonly RadioButton _rbAuto = new() { Content = "Auto", GroupName = "fan" };
    private readonly RadioButton _rbMax = new() { Content = "Max", GroupName = "fan" };
    private readonly RadioButton _rbCustom = new() { Content = "Custom", GroupName = "fan" };
    private readonly Slider _cpuBar = new() { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, Width = 230 };
    private readonly Slider _gpuBar = new() { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 10, Width = 230 };
    private readonly TextBlock _cpuPct = new() { Text = "70%", Width = 40 };
    private readonly TextBlock _gpuPct = new() { Text = "70%", Width = 40 };
    private readonly TextBlock _cpuLabel = new() { Text = "CPU:  …" };
    private readonly TextBlock _gpuLabel = new() { Text = "GPU:  …" };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _turboToggle = new() { Content = "Turbo key toggles Turbo (otherwise cycles profiles)" };

    private readonly DispatcherTimer _fanDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading = true;

    public bool TurboToggles => _turboToggle.IsChecked == true;

    public MainWindow(Action<AcerProfile> onApplyProfile, Action<FanMode, byte, byte> onApplyFan,
                      Action onOpenLighting, Func<bool> clamshellEnabled, Action<bool> setClamshell,
                      bool turboToggles, Action<bool> onTurboToggleChanged,
                      Func<bool> autostartEnabled, Action<bool> setAutostart,
                      IReadOnlyList<OptionToggle> hwToggles, IReadOnlyList<OptionChoice> hwChoices,
                      int fanModeInit, int cpuFanInit, int gpuFanInit, Action<int, int, int> onFanSettingsChanged)
    {
        _onApplyProfile = onApplyProfile;
        _onApplyFan = onApplyFan;
        _onTurboToggleChanged = onTurboToggleChanged;
        _onFanSettingsChanged = onFanSettingsChanged;

        _fanDebounce.Tick += (_, _) => { _fanDebounce.Stop(); ApplyNow(); };

        Title = "Acer Helper";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 0 };
        root.Children.Add(Section("Performance profile", BuildProfiles()));
        root.Children.Add(Section("Monitoring", BuildMonitor()));
        root.Children.Add(Section("Fans", BuildFans()));
        root.Children.Add(Section("Options", BuildOptions(clamshellEnabled, setClamshell, turboToggles,
                                                          autostartEnabled, setAutostart, hwToggles, hwChoices)));
        root.Children.Add(BuildBottom(onOpenLighting));
        Content = root;

        // restore saved fan selection (guarded -> no apply during restore)
        _cpuBar.Value = Math.Clamp(cpuFanInit, 0, 100);
        _gpuBar.Value = Math.Clamp(gpuFanInit, 0, 100);
        _cpuPct.Text = (int)_cpuBar.Value + "%";
        _gpuPct.Text = (int)_gpuBar.Value + "%";
        (fanModeInit == (int)FanMode.Max ? _rbMax : fanModeInit == (int)FanMode.Custom ? _rbCustom : _rbAuto).IsChecked = true;
        UpdateFanEnabled();
        _loading = false;

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

    private Control BuildProfiles()
    {
        var grid = new UniformGrid { Columns = AcerProfileInfo.All.Length };
        foreach (var p in AcerProfileInfo.All)
        {
            var btn = new Button
            {
                Content = AcerProfileInfo.DisplayName(p),
                Margin = new Thickness(2),
                Padding = new Thickness(2, 8, 2, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = p,
            };
            btn.Click += (s, _) => _onApplyProfile((AcerProfile)((Button)s!).Tag!);
            _profileButtons[p] = btn;
            grid.Children.Add(btn);
        }
        return grid;
    }

    private Control BuildMonitor()
        => new StackPanel { Spacing = 3, Children = { _cpuLabel, _gpuLabel } };

    private Control BuildFans()
    {
        _rbAuto.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _rbMax.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _rbCustom.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _cpuBar.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _cpuPct.Text = (int)_cpuBar.Value + "%"; DebounceFanApply(); } };
        _gpuBar.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _gpuPct.Text = (int)_gpuBar.Value + "%"; DebounceFanApply(); } };

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _rbAuto, _rbMax, _rbCustom } };
        var cpuRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "CPU fan", Width = 56, VerticalAlignment = VerticalAlignment.Center }, _cpuBar, _cpuPct } };
        var gpuRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "GPU fan", Width = 56, VerticalAlignment = VerticalAlignment.Center }, _gpuBar, _gpuPct } };

        var apply = new Button { Content = "Apply fans", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) => ApplyNow();

        return new StackPanel { Spacing = 6, Children = { modes, cpuRow, gpuRow, apply } };
    }

    private Control BuildOptions(Func<bool> clamshellEnabled, Action<bool> setClamshell, bool turboToggles,
                                 Func<bool> autostartEnabled, Action<bool> setAutostart,
                                 IReadOnlyList<OptionToggle> hwToggles, IReadOnlyList<OptionChoice> hwChoices)
    {
        var panel = new StackPanel { Spacing = 6 };

        foreach (OptionToggle tog in hwToggles)
        {
            var cb = new CheckBox { Content = tog.Label, IsChecked = tog.Initial, IsEnabled = tog.Supported };
            Action<bool> onChange = tog.OnChange;
            Func<bool>? confirm = tog.Confirm;
            bool guard = false;
            cb.IsCheckedChanged += (s, _) =>
            {
                if (guard) return;
                var box = (CheckBox)s!;
                if (box.IsChecked == true && confirm != null && !confirm())
                {
                    guard = true; box.IsChecked = false; guard = false;
                    return;
                }
                onChange(box.IsChecked == true);
            };
            panel.Children.Add(cb);
        }

        foreach (OptionChoice ch in hwChoices)
        {
            var combo = new ComboBox { ItemsSource = ch.Options, SelectedIndex = Math.Clamp(ch.InitialIndex, 0, ch.Options.Count - 1), MinWidth = 96 };
            Action<int> onPick = ch.OnChange;
            combo.SelectionChanged += (s, _) => onPick(((ComboBox)s!).SelectedIndex);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, IsEnabled = ch.Supported, Children = { new TextBlock { Text = ch.Label, VerticalAlignment = VerticalAlignment.Center }, combo } };
            panel.Children.Add(row);
        }

        var clamshell = new CheckBox { Content = "Stay awake when lid closed (docked, on AC)", IsChecked = clamshellEnabled() };
        clamshell.IsCheckedChanged += (s, _) => setClamshell(((CheckBox)s!).IsChecked == true);
        panel.Children.Add(clamshell);

        _turboToggle.IsChecked = turboToggles;
        _turboToggle.IsCheckedChanged += (s, _) => _onTurboToggleChanged(((CheckBox)s!).IsChecked == true);
        panel.Children.Add(_turboToggle);

        var autostart = new CheckBox { Content = "Start with Windows", IsChecked = autostartEnabled() };
        autostart.IsCheckedChanged += (s, _) => setAutostart(((CheckBox)s!).IsChecked == true);
        panel.Children.Add(autostart);

        return panel;
    }

    private Control BuildBottom(Action onOpenLighting)
    {
        _status.Width = 230;
        var lighting = new Button { Content = "Lighting…", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };
        lighting.Click += (_, _) => onOpenLighting();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _status, lighting } };
    }

    private FanMode SelectedMode() =>
        _rbMax.IsChecked == true ? FanMode.Max : _rbCustom.IsChecked == true ? FanMode.Custom : FanMode.Auto;

    private void UpdateFanEnabled()
    {
        bool custom = _rbCustom.IsChecked == true;
        _cpuBar.IsEnabled = custom;
        _gpuBar.IsEnabled = custom;
    }

    private void OnFanModeChanged()
    {
        UpdateFanEnabled();
        if (_loading) return;
        ApplyNow();
    }

    private void DebounceFanApply()
    {
        if (_loading || _rbCustom.IsChecked != true) return;
        _fanDebounce.Stop();
        _fanDebounce.Start();
    }

    private void ApplyNow()
    {
        _onApplyFan(SelectedMode(), (byte)_cpuBar.Value, (byte)_gpuBar.Value);
        _onFanSettingsChanged((int)SelectedMode(), (int)_cpuBar.Value, (int)_gpuBar.Value);
    }

    public void PositionNearTray()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var wa = screen.WorkingArea;             // physical pixels
        double s = screen.Scaling;
        int w = (int)(Bounds.Width * s);
        int h = (int)(Bounds.Height * s);
        Position = new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - w - 12),
            Math.Max(wa.Y, wa.Y + wa.Height - h - 12));
    }

    public void RefreshState(AcerProfile? current, byte mask, SensorSnapshot s, string? status)
    {
        foreach (var kv in _profileButtons)
        {
            bool supported = mask == 0 || AcerProfileInfo.IsSupported(mask, kv.Key);
            bool active = current.HasValue && current.Value == kv.Key;
            kv.Value.IsEnabled = supported;
            kv.Value.Background = active ? new SolidColorBrush(Accent) : null;
            kv.Value.Foreground = active ? Brushes.White : null;
        }

        _cpuLabel.Text = $"CPU:   {Fmt(s.CpuTempC, "°C")}    {Fmt(s.CpuFanRpm, "rpm")}";
        _gpuLabel.Text = $"GPU:   {Fmt(s.GpuTempC, "°C")}    {Fmt(s.GpuFanRpm, "rpm")}";
        if (status != null) _status.Text = status;
    }

    public void SetStatus(string text) => _status.Text = text;

    private static string Fmt(int v, string unit) => v < 0 ? "—" : $"{v} {unit}";
}
