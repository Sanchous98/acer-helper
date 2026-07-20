using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Keep the laptop awake on lid-close, but only while an external display is connected AND on AC
/// (like G-Helper). The state machine — enable/disable and "write the lid action only when the
/// desired state changes" — is shared here; the OS hooks (reading AC/display, writing the lid
/// action, subscribing to display/power change events) are the partial methods in Clamshell.*.cs.
/// </summary>
public sealed partial class Clamshell : IClamshell
{
    private bool? _applied;   // last lid action written (true = stay awake); null = unknown

    // Evaluate() runs from THREE threads now — the SystemEvents display/power callbacks (a dedicated thread),
    // the UI thread (SetEnabled), and the background refresh pass — so the read-modify-write of _applied (and
    // the Windows _originalLidAction inside SetLidStayAwake) must be serialized. Re-entrant, so SetEnabled's
    // SetLidStayAwake+Evaluate nesting is fine.
    private readonly Lock _sync = new();

    public Clamshell() => Subscribe();

    public string Label => "Stay awake when lid closed (docked, on AC)";
    public bool Enabled { get; private set; }

    public bool Supported => CanManageLidAction();

    public void SetEnabled(bool value)
    {
        lock (_sync)
        {
            Enabled = value;
            if (!value) { SetLidStayAwake(false); _applied = false; }   // put back the pre-takeover lid action
            else _applied = null;                                       // force re-apply on next Evaluate
            Evaluate();
        }
    }

    public void Evaluate()
    {
        lock (_sync)
        {
            if (!Enabled) return;
            var active = HasExternalDisplay() && OnAc();
            if (_applied == active) return;   // already in this state - don't rewrite power policy
            SetLidStayAwake(active);
            _applied = active;
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        lock (_sync)
        {
            if (Enabled) SetLidStayAwake(false);   // never leave lid=stay-awake after exit
            Enabled = false;                        // so a racing Evaluate (e.g. an in-flight background pass) can't re-arm it
        }
    }

    // OS-specific points:
    private partial void Subscribe();
    private partial void Unsubscribe();
    private partial bool CanManageLidAction();
    private partial bool HasExternalDisplay();
    private partial bool OnAc();
    /// <summary>true = lid close does nothing (stay awake); false = put back the lid action captured at
    /// take-over (never stay-awake — see the Windows implementation for the crash-leftover guard).</summary>
    private partial void SetLidStayAwake(bool stayAwake);
}
