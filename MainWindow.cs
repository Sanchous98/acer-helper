using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcerHelper;

/// <summary>Compact main window. Sections are built only for the features the device exposes,
/// so one window adapts to any vendor × OS backend. Built in code (no XAML view).</summary>
public sealed class MainWindow : Window
{
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x7D, 0x32);

    private readonly Action<PerformanceProfile> _onApplyProfile;
    private readonly Action<FanMode, byte, byte> _onApplyFan;
    private readonly Action<int, int, int> _onFanSettingsChanged;

    private readonly Dictionary<string, Button> _profileButtons = new();
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

    private readonly DispatcherTimer _fanDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading = true;

    public MainWindow(IDevice device,
                      Action<PerformanceProfile> onApplyProfile, Action<FanMode, byte, byte> onApplyFan,
                      Action onOpenLighting,
                      IReadOnlyList<OptionToggle> hwToggles, IReadOnlyList<OptionChoice> hwChoices,
                      Func<bool> clamshellEnabled, Action<bool> setClamshell,
                      bool turboToggles, Action<bool> onTurboToggleChanged,
                      Func<bool> autostartEnabled, Action<bool> setAutostart,
                      int fanModeInit, int cpuFanInit, int gpuFanInit, Action<int, int, int> onFanSettingsChanged)
    {
        _onApplyProfile = onApplyProfile;
        _onApplyFan = onApplyFan;
        _onFanSettingsChanged = onFanSettingsChanged;

        _fanDebounce.Tick += (_, _) => { _fanDebounce.Stop(); ApplyNow(); };

        Title = "Acer Helper";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 0 };

        if (device.PowerProfiles is { } pp)
            root.Children.Add(Section("Performance profile", BuildProfiles(pp.All)));
        if (device.Sensors != null)
            root.Children.Add(Section("Monitoring", BuildMonitor()));
        if (device.FanControl is { } fc)
            root.Children.Add(Section("Fans", BuildFans(fc.Capability)));

        var options = BuildOptions(device, hwToggles, hwChoices, clamshellEnabled, setClamshell,
                                   turboToggles, onTurboToggleChanged, autostartEnabled, setAutostart);
        if (options != null)
            root.Children.Add(Section("Options", options));

        root.Children.Add(BuildBottom(onOpenLighting, showLighting: device.Lighting != null));
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

    private Control BuildProfiles(IReadOnlyList<PerformanceProfile> profiles)
    {
        var grid = new UniformGrid { Columns = Math.Max(profiles.Count, 1) };
        foreach (var p in profiles)
        {
            var btn = new Button
            {
                Content = p.DisplayName,
                Margin = new Thickness(2),
                Padding = new Thickness(2, 8, 2, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = p,
            };
            btn.Click += (s, _) => _onApplyProfile((PerformanceProfile)((Button)s!).Tag!);
            _profileButtons[p.Id] = btn;
            grid.Children.Add(btn);
        }
        return grid;
    }

    private Control BuildMonitor()
        => new StackPanel { Spacing = 3, Children = { _cpuLabel, _gpuLabel } };

    private Control BuildFans(FanCapability cap)
    {
        _rbAuto.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _rbMax.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _rbCustom.IsCheckedChanged += (_, _) => OnFanModeChanged();
        _cpuBar.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _cpuPct.Text = (int)_cpuBar.Value + "%"; DebounceFanApply(); } };
        _gpuBar.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _gpuPct.Text = (int)_gpuBar.Value + "%"; DebounceFanApply(); } };

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        modes.Children.Add(_rbAuto);
        if (cap.HasMax) modes.Children.Add(_rbMax);
        if (cap.HasCustom) modes.Children.Add(_rbCustom);

        var rows = new StackPanel { Spacing = 6, Children = { modes } };
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "CPU fan", Width = 56, VerticalAlignment = VerticalAlignment.Center }, _cpuBar, _cpuPct } });
        if (cap.HasGpuFan)
            rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "GPU fan", Width = 56, VerticalAlignment = VerticalAlignment.Center }, _gpuBar, _gpuPct } });

        var apply = new Button { Content = "Apply fans", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) => ApplyNow();
        rows.Children.Add(apply);
        return rows;
    }

    private Control? BuildOptions(IDevice device,
                                  IReadOnlyList<OptionToggle> hwToggles, IReadOnlyList<OptionChoice> hwChoices,
                                  Func<bool> clamshellEnabled, Action<bool> setClamshell,
                                  bool turboToggles, Action<bool> onTurboToggleChanged,
                                  Func<bool> autostartEnabled, Action<bool> setAutostart)
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

        if (device.Clamshell is { } clam)
        {
            var cb = new CheckBox { Content = clam.Label, IsChecked = clamshellEnabled() };
            cb.IsCheckedChanged += (s, _) => setClamshell(((CheckBox)s!).IsChecked == true);
            panel.Children.Add(cb);
        }

        bool hasTurbo = device.PowerProfiles?.All.Any(p => p.Kind == ProfileKind.Turbo) ?? false;
        if (hasTurbo)
        {
            var cb = new CheckBox { Content = "Turbo key toggles Turbo (otherwise cycles profiles)", IsChecked = turboToggles };
            cb.IsCheckedChanged += (s, _) => onTurboToggleChanged(((CheckBox)s!).IsChecked == true);
            panel.Children.Add(cb);
        }

        if (device.Autostart is { } auto)
        {
            var cb = new CheckBox { Content = auto.Label, IsChecked = autostartEnabled() };
            cb.IsCheckedChanged += (s, _) => setAutostart(((CheckBox)s!).IsChecked == true);
            panel.Children.Add(cb);
        }

        return panel.Children.Count > 0 ? panel : null;
    }

    private Control BuildBottom(Action onOpenLighting, bool showLighting)
    {
        _status.Width = 230;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(_status);
        if (showLighting)
        {
            var lighting = new Button { Content = "Lighting…", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4) };
            lighting.Click += (_, _) => onOpenLighting();
            panel.Children.Add(lighting);
        }
        return panel;
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

    public void RefreshState(PerformanceProfile? current, IReadOnlyList<PerformanceProfile> available, SensorSnapshot s, string? status)
    {
        foreach (var kv in _profileButtons)
        {
            bool supported = available.Any(p => p.Id == kv.Key);
            bool active = current?.Id == kv.Key;
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
