namespace AcerHelper;

// Linux lid-state monitoring is omitted: clamshell keep-awake itself is unsupported on Linux (the DE's power
// manager owns the lid — see Clamshell.Linux.cs), so there is never a keep-awake state in which a lit keyboard
// sits under a shut lid. If clamshell lands on Linux, wire lid detection here (logind's PrepareForSleep won't
// serve — the machine actually sleeps; a udev / /proc/acpi/button/lid watch would be the lever). See LidWatcher.cs.
internal sealed partial class LidWatcher
{
    private partial void Subscribe() { }
    private partial void Unsubscribe() { }
}
