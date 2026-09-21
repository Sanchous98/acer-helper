namespace AcerHelper.Infrastructure;

// The SMU-mailbox permission POLICY lives in this UN-SUFFIXED file deliberately, and moving it into
// RyzenCurveOptimizer.Linux.cs (or into HardwareAccess.Linux.cs) would be a silent loss of coverage rather than a
// tidy-up: the test project targets net10.0-windows while AcerHelper.csproj excludes **/*.Linux.cs from that TFM,
// so any rule left in a Linux file cannot be reached by the suite at all. The rule is therefore here, the I/O
// stays per-OS behind SmuMailboxAccessHost, and the two meet through delegates — the same shape
// CardwireGpuAccess.cs and AcerProfilePorts.cs use.

/// <summary>
/// THE GRANT, ASKED AS A QUESTION NO FILE COMPARISON CAN ANSWER. The app offers "Grant hardware access" when an
/// installed file differs from the bundled bytes (<c>HardwareAccess.RulesNeeded</c>), and that test is blind to
/// the failure that actually happened on this machine: the four <c>/sys/kernel/ryzen_smu_drv</c> attributes are
/// created by the kernel at every boot with root-only permissions, the grant that makes them group-writable was
/// never re-applied (the udev rule's bind event does not fire for a module inserted from the initramfs), and
/// nothing about the FILES in /etc changed. Byte-identical files, no offer, and the owner's Tuning tab gone — the
/// app could not even offer to repair what it had lost.
///
/// SO THE QUESTION ASKED HERE IS ABOUT THE PERMISSION, not about the package: "does any node this package covers
/// exist, and fail to be writable by this user?" True means the grant is missing right now and an install would
/// place it; false means either that the grant is already in place or that THERE IS NOTHING TO GRANT HERE.
///
/// THE SECOND CASE IS THE TRAP, and it is the one an "is it writable?" predicate gets wrong: on a Dell, on a
/// generic laptop, or on an Acer whose <c>ryzen_smu</c> is not installed, none of these files exist at all.
/// Absence is not a lost grant — it is a machine this grant has no meaning for — so a node that does not exist
/// must never make the app offer to install anything. Only PRESENT and UNWRITABLE counts, and the two facts
/// therefore arrive as two separate delegates rather than one "is it writable" that would read a missing file as
/// a refusal (which is exactly what <c>File.Exists</c>-less probing does).
///
/// ONE NODE IS ENOUGH, and that is deliberate rather than lax: the four attributes are granted together by every
/// mechanism in this tree, but they are separate files the kernel may expose independently — a driver build
/// without one of the command nodes must not hide a lost grant on the node that IS there. The list is the whole
/// of what the package covers, and it is the ONE definition of it: the source-text guards in the suite read these
/// names and require the udev rule and the systemd unit to spell out the same four, so a fifth attribute added
/// here without being granted there (or a grant for a file this list does not know) reddens the suite instead of
/// drifting.</summary>
internal static class SmuMailboxAccess
{
    /// <summary>The driver's sysfs tree. A BARE kobject — not a device, no uevent — which is why nothing can
    /// reach these files by matching a device and why the packaging files name them by absolute path.</summary>
    internal const string DriverDir = "/sys/kernel/ryzen_smu_drv";

    /// <summary>The attributes this package grants, as the driver's whole writable surface. The app drives
    /// <c>smn</c> alone (the command nodes cannot report a refusal — see docs/curve-optimizer-linux.md); the other
    /// three travel with it because a half-granted driver is worse for the other tools that share it.</summary>
    internal static readonly string[] Nodes = ["smu_args", "mp1_smu_cmd", "rsmu_cmd", "smn"];

    /// <summary>The same names as the absolute paths the packaging files write out.</summary>
    internal static IEnumerable<string> NodePaths => Nodes.Select(node => DriverDir + "/" + node);

    /// <summary>Whether the grant this package places is MISSING on this machine: some covered node exists and
    /// this user cannot write it. Pure — the two filesystem questions arrive as delegates — so every branch is
    /// pinned by the suite, including the one that matters most (no node at all → false, nothing to offer).
    ///
    /// <paramref name="nodeWritable"/> is asked only about a node that exists, and that ordering is load-bearing:
    /// it keeps a missing node out of the probe entirely, so an implementation that answers "not writable" for a
    /// file that is not there cannot turn absence into an offer.</summary>
    internal static bool GrantMissing(Func<string, bool> nodeExists, Func<string, bool> nodeWritable)
    {
        foreach (var path in NodePaths)
            if (nodeExists(path) && !nodeWritable(path)) return true;
        return false;
    }
}

/// <summary>
/// The machine's own probe, supplied by the file that knows the OS (<c>SmuMailboxAccess.Linux.cs</c> /
/// <c>SmuMailboxAccess.Windows.cs</c>). Declared here so <c>HardwareAccess.RulesNeeded</c> — a cross-platform file
/// compiled for both TFMs — can name one member for both builds: the same shape <c>MachineInfo</c> and
/// <c>CardwireGpuAccessHost</c> use.
///
/// The Windows half answers false and is never consulted in practice: <c>RulesNeeded()</c> returns before it on a
/// non-Linux OS, and Windows has no sysfs at all. It exists because a partial method needs an implementation part
/// in every TFM, and a stub that says so is more honest than a build error.</summary>
internal static partial class SmuMailboxAccessHost
{
    /// <summary>Whether a node this package covers is present here and not writable by this user — i.e. the
    /// grant is missing now. See <see cref="SmuMailboxAccess.GrantMissing"/> for what the answer means.</summary>
    internal static partial bool GrantMissing();
}
