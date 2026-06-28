using Microsoft.Win32;
using System.Windows.Forms;

namespace AcerHelper;

/// <summary>
/// Auto clamshell (like G-Helper): when enabled, keeps the laptop awake on
/// lid-close ONLY while an external display is connected AND on AC power.
/// Otherwise it restores normal "sleep on lid close". Re-evaluates whenever the
/// display configuration or power source changes — so unplugging the monitor or
/// the charger immediately reverts to sleeping on lid close.
/// </summary>
public sealed class ClamshellManager : IDisposable
{
    private readonly EventHandler _onDisplay;
    private readonly PowerModeChangedEventHandler _onPower;
    private bool _enabled;

    public bool Enabled   => _enabled;
    public bool Supported => Clamshell.Available;

    public ClamshellManager()
    {
        _onDisplay = (_, _) => Evaluate();
        _onPower   = (_, _) => Evaluate();
        SystemEvents.DisplaySettingsChanged += _onDisplay;
        SystemEvents.PowerModeChanged       += _onPower;
    }

    public void SetEnabled(bool value)
    {
        _enabled = value;
        if (!value) Clamshell.Disable();   // restore "sleep on lid close"
        Evaluate();
    }

    /// <summary>Apply the correct lid action for the current display + power state.</summary>
    public void Evaluate()
    {
        if (!_enabled) return;
        bool active = DisplayInfo.HasExternalDisplay() && OnAc();
        if (active) Clamshell.Enable();    // lid close = do nothing
        else        Clamshell.Disable();   // lid close = sleep
    }

    private static bool OnAc()
        => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= _onDisplay;
        SystemEvents.PowerModeChanged       -= _onPower;
        if (_enabled) Clamshell.Disable();  // never leave lid=do-nothing after exit
    }
}
