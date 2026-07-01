namespace AcerHelper.Vendors.Generic;

// Linux clamshell is reported unsupported (pending a decision). Empirically, on a DE the lid is owned by
// the DE's power manager, not logind: e.g. KDE PowerDevil holds a *block* inhibitor on handle-lid-switch
// ("KDE handles power events") and suspends per its own config — so a third-party logind inhibitor is
// redundant and does NOT stop the DE, and /etc/systemd/logind.conf is likewise overridden. There is no
// DE-agnostic runtime lever that overrides the DE; only the DE's own config works (per-DE), which is ugly.
// So for now the port is omitted on Linux (the Windows build keeps clamshell via the real lid power action).
public sealed partial class Clamshell
{
    private partial void Subscribe() { }
    private partial void Unsubscribe() { }
    private partial bool CanManageLidAction() => false;
    private partial bool HasExternalDisplay() => false;
    private partial bool OnAc() => false;
    private partial void SetLidStayAwake(bool stayAwake) { }
}
