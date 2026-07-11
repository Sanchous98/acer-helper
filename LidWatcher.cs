namespace AcerHelper;

/// <summary>Fires <paramref name="onLidChanged"/> when the laptop lid opens or closes — the argument is
/// <c>true</c> when it opened, <c>false</c> when it closed. Used to blank the RGB backlight while the lid is
/// shut in clamshell (keep-awake) mode, where the machine stays on but the lit keyboard/lightbar are hidden.
/// Windows uses a hidden window + RegisterPowerSettingNotification (GUID_LIDSWITCH_STATE_CHANGE); Linux is a
/// no-op (clamshell keep-awake itself is unsupported there — see Clamshell.Linux.cs). The event fires on the
/// window-message thread, so the callback must marshal to the UI thread itself. Per-OS implementation lives in
/// LidWatcher.Windows.cs / LidWatcher.Linux.cs.</summary>
internal sealed partial class LidWatcher(Action<bool> onLidChanged) : IDisposable
{
    private readonly Action<bool> _onLidChanged = onLidChanged;

    public void Start() => Subscribe();
    public void Dispose() => Unsubscribe();

    private partial void Subscribe();
    private partial void Unsubscribe();
}
