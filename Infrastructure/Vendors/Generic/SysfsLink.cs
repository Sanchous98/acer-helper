namespace AcerHelper.Infrastructure.Vendors.Generic;

// Why an UN-SUFFIXED file at all, when both callers are Linux-only (sysfs class nodes): the test project cannot
// compile a *.Linux.cs file — AcerHelper.csproj removes them from its net10.0-windows TFM, see the argument at the
// top of AcerProfilePorts.cs — and this resolver is subtle enough that leaving it unreachable by the suite would
// be the wrong trade. It uses nothing platform-specific (FileSystemInfo.LinkTarget exists on both TFMs), so both
// can compile it; only the Linux backends call it.
//
// COMPILING FOR THE SUITE IS NOT THE SAME AS BEING REACHABLE BY IT, which is what the probes below are for. Both
// callers are removed from the test TFM by their *.Linux.cs suffix, and the walk cannot be handed a real tree
// either, because its own spelling is POSIX: rooted at "/" and split on '/'. Measured on the owner's machine, a
// directory named "AMDI0103:00" — one of the two handler nodes the token rule must tell apart — cannot exist on
// Windows at all (Directory.CreateDirectory answers IOException "Неверно задано имя папки."), and %TEMP% is
// spelled "C:\...", which a "/"-rooted path cannot name. So the two questions the walk asks of the filesystem
// arrive as delegates — the shape DelegatePorts.cs and AcerProfilePorts.cs already use to make Linux-only policy
// testable on the machine this suite runs on — and SysfsLinkTests drives the walk with a map of the real /sys
// tree rather than with a tree.

/// <summary>
/// The kernel's path resolution for a sysfs class entry, because the BCL's is not.
///
/// MEASURED on the AN18-61, not suspected: <c>new DirectoryInfo("/sys/class/hwmon/hwmon10/device")
/// .ResolveLinkTarget(returnFinalTarget: true)</c> answers <c>/sys/acer-wmi</c>, where the kernel's own answer
/// (and <c>readlink -f</c>'s) is <c>/sys/devices/platform/acer-wmi</c>. The reason is that .NET resolves each hop
/// LEXICALLY — it combines a link's target with the path as SPELLED — while a symlink's target is relative to its
/// containing directory as REACHED. <c>/sys/class/hwmon/hwmon10</c> is itself a link into /sys/devices/..., so
/// "../../../acer-wmi" measured from the spelled path climbs to /sys/acer-wmi and measured from the real one lands
/// on the platform device.
///
/// So this walks the path one component at a time, resolving every link in the context of what has already been
/// resolved — the shape the VFS uses, and the only one that gets a /sys/class path right. The callers need the
/// REAL path because the real path is what names the driver: the class path says "platform-profile-1", the device
/// path says ".../acer-wmi/platform-profile/platform-profile-1", and the hwmon chip's device resolves to
/// /sys/devices/platform/acer-wmi. (The device path is the only identity a control may bind to — see
/// <c>AcerHwmonChip</c>.)
/// </summary>
internal static class SysfsLink
{
    /// <summary>A chain deeper than anything sysfs builds, and the loop guard for a link that points at itself.</summary>
    private const int MaxHops = 64;

    /// <summary>The real path of <paramref name="path"/> on the running kernel's filesystem: <see cref="Walk"/>
    /// with the filesystem's own two answers behind it.</summary>
    internal static string? RealPath(string path) => Walk(path, LinkTargetOf, Names);

    /// <summary>The walk <see cref="RealPath"/> is, with the two questions it asks of the filesystem injected —
    /// <paramref name="linkTargetOf"/> answers "is there a link at this path, and to what" once per component,
    /// <paramref name="names"/> answers "does this path name anything" once at the end — so that the walk's own
    /// arithmetic is what a test drives, rather than the filesystem's answer to it.
    ///
    /// Null means it could not be resolved (a component that does not exist, a link loop, or nothing at the end).
    /// Callers treat null as "not this device", which is the safe answer: a node whose driver cannot be named is
    /// not one to bind a control to.</summary>
    internal static string? Walk(string path, Func<string, string?> linkTargetOf, Func<string, bool> names)
    {
        try
        {
            var resolved = new List<string>();
            var todo = new List<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            var hops = 0;

            while (todo.Count > 0)
            {
                if (++hops > MaxHops) return null;

                var part = todo[0];
                todo.RemoveAt(0);
                if (part == ".") continue;
                if (part == "..")
                {
                    if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                    continue;
                }

                resolved.Add(part);
                if (linkTargetOf("/" + string.Join('/', resolved)) is not { } link) continue;

                // A link is not a path component of the result: it stands for its target, and the target is
                // relative to the link's CONTAINING directory — which is `resolved` without this last part, and
                // which is already fully resolved, because we got here one component at a time.
                resolved.RemoveAt(resolved.Count - 1);
                // Only the LINK'S OWN components are queued: the context it is relative to is already in
                // `resolved`, so its ".." (a sysfs link is full of them) pop that context directly — queuing the
                // context along with the target would add it a SECOND time and leave a path that names nothing.
                if (link.StartsWith('/')) resolved.Clear();   // an absolute target restarts from the root
                todo.InsertRange(0, link.Split('/', StringSplitOptions.RemoveEmptyEntries));
            }

            var result = "/" + string.Join('/', resolved);
            // A path that does not NAME anything is not a resolution: null is the callers' "not this device",
            // and answering with a well-formed path to nothing would send a control probe looking at a device
            // that is not there.
            return names(result) ? result : null;
        }
        catch { return null; }
    }

    /// <summary>Whether the path names anything at all. Directory first and file second for the same reason
    /// <see cref="LinkTargetOf"/> tries both: a sysfs class tree holds links to either.</summary>
    private static bool Names(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>The target of a symlink, or null when the path is not one (or does not exist). Tried as a
    /// directory first and as a file second, because <c>DirectoryInfo.Exists</c> is false for a link pointing at a
    /// file and <c>FileInfo.Exists</c> for one pointing at a directory — and a sysfs class tree has both.</summary>
    private static string? LinkTargetOf(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists) return dir.LinkTarget;
        var file = new FileInfo(path);
        return file.Exists ? file.LinkTarget : null;
    }
}
