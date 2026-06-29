namespace AcerHelper;

/// <summary>Windows backend: wires the concrete Acer WMI/HID/Win32 implementations.</summary>
public sealed class WindowsPlatform : IPlatform
{
    private readonly AcerWmi _wmi = new();
    private readonly AcerBattery _battery = new();
    private readonly AcerApGe _apge = new();
    private readonly AcerEneRgb _rgb = new();
    private readonly AcerHotkeyWatcher _hotkeys = new();
    private readonly ClamshellManager _clamshell = new();
    private readonly WindowsDisplayTint _tint = new();
    private readonly WindowsAutostart _autostart = new();

    public IPerformance Performance => _wmi;
    public IBattery     Battery     => _battery;
    public IPeripherals Peripherals => _apge;
    public IRgb         Rgb         => _rgb;
    public IHotkeys     Hotkeys     => _hotkeys;
    public IClamshell   Clamshell   => _clamshell;
    public IDisplayTint DisplayTint => _tint;
    public IAutostart   Autostart   => _autostart;

    public void Dispose()
    {
        _wmi.Dispose();
        _battery.Dispose();
        _apge.Dispose();
        _rgb.Dispose();
        _hotkeys.Dispose();
        _clamshell.Dispose();
    }
}

/// <summary>Wraps the static gamma helper as an instance service.</summary>
public sealed class WindowsDisplayTint : IDisplayTint
{
    public int Levels => Bluelight.Levels;
    public bool Apply(int level) => Bluelight.Apply(level);
}

/// <summary>Wraps the static autostart helper as an instance service.</summary>
public sealed class WindowsAutostart : IAutostart
{
    public bool IsEnabled() => Autostart.IsEnabled();
    public bool SetEnabled(bool enable) => Autostart.SetEnabled(enable);
}
