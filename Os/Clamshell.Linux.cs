namespace AcerHelper.Os;

// Linux clamshell is not implemented yet (it would drive systemd-logind's HandleLidSwitch and read
// DRM/upower for display + AC state). For now it reports unsupported, so the composition root omits
// the port and the build stays green. Implement these partials to enable it on Linux.
public sealed partial class Clamshell
{
    private partial void Subscribe() { }
    private partial void Unsubscribe() { }
    private partial bool CanManageLidAction() => false;
    private partial bool HasExternalDisplay() => false;
    private partial bool OnAc() => false;
    private partial void SetLidStayAwake(bool stayAwake) { }
}
