namespace AcerHelper;

/// <summary>Fires <paramref name="onResume"/> when the machine wakes from sleep or hibernation. The firmware
/// drops the RGB lighting across a suspend/hibernate cycle, so the app must re-apply it on resume. Windows uses
/// the power-mode resume event; Linux tails systemd-logind's PrepareForSleep signal (degrades to a no-op if
/// unavailable). The event fires on a system thread, so the callback must marshal to the UI thread itself.
/// Per-OS implementation lives in ResumeWatcher.Windows.cs / ResumeWatcher.Linux.cs.</summary>
internal sealed partial class ResumeWatcher(Action onResume) : IDisposable
{
    private readonly Action _onResume = onResume;

    public void Start() => Subscribe();
    public void Dispose() => Unsubscribe();

    private partial void Subscribe();
    private partial void Unsubscribe();
}
