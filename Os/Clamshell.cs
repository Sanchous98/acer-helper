using AcerHelper.Features;

namespace AcerHelper.Os;

/// <summary>
/// Keep the laptop awake on lid-close, but only while an external display is connected AND on AC
/// (like G-Helper). The state machine — enable/disable and "write the lid action only when the
/// desired state changes" — is shared here; the OS hooks (reading AC/display, writing the lid
/// action, subscribing to display/power change events) are the partial methods in Clamshell.*.cs.
/// </summary>
public sealed partial class Clamshell : IClamshell
{
    private bool _enabled;
    private bool? _applied;   // last lid action written (true = stay awake); null = unknown

    public Clamshell() => Subscribe();

    public string Label => "Stay awake when lid closed (docked, on AC)";
    public bool Enabled => _enabled;
    public bool Supported => CanManageLidAction();

    public void SetEnabled(bool value)
    {
        _enabled = value;
        if (!value) { SetLidStayAwake(false); _applied = false; }   // restore "sleep on lid close"
        else _applied = null;                                       // force re-apply on next Evaluate
        Evaluate();
    }

    public void Evaluate()
    {
        if (!_enabled) return;
        bool active = HasExternalDisplay() && OnAc();
        if (_applied == active) return;   // already in this state - don't rewrite power policy
        SetLidStayAwake(active);
        _applied = active;
    }

    public void Dispose()
    {
        Unsubscribe();
        if (_enabled) SetLidStayAwake(false);   // never leave lid=stay-awake after exit
    }

    // OS-specific points:
    private partial void Subscribe();
    private partial void Unsubscribe();
    private partial bool CanManageLidAction();
    private partial bool HasExternalDisplay();
    private partial bool OnAc();
    /// <summary>true = lid close does nothing (stay awake); false = sleep on lid close.</summary>
    private partial void SetLidStayAwake(bool stayAwake);
}
