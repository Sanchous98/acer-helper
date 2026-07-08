using Microsoft.Win32;

namespace AcerHelper;

// Windows resume hook: SystemEvents.PowerModeChanged with PowerModes.Resume, which fires on wake from BOTH
// sleep and hibernation (PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND). See ResumeWatcher.cs.
internal sealed partial class ResumeWatcher
{
    private partial void Subscribe()   => SystemEvents.PowerModeChanged += OnPowerModeChanged;
    private partial void Unsubscribe() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _onResume();
    }
}
