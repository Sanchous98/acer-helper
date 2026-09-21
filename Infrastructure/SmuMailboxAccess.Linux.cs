using System.IO;

namespace AcerHelper.Infrastructure;

// THE LINUX HALF: the two filesystem questions the policy is asked with, and nothing else. This file is compiled
// into the Linux build only (AcerHelper.csproj <Compile Remove="**/*.Linux.cs"> under the Windows TFM), which is
// why the RULE lives in the un-suffixed file beside it where the suite can reach it — the same split
// CardwireGpuAccess.cs makes.

internal static partial class SmuMailboxAccessHost
{
    /// <summary>This machine's answer, from the real filesystem: see
    /// <see cref="SmuMailboxAccess.GrantMissing"/> for the rule and why absence is not a lost grant.</summary>
    internal static partial bool GrantMissing() => SmuMailboxAccess.GrantMissing(File.Exists, CanWrite);

    /// <summary>Whether this user can write the node, probed by OPENING it for write and writing nothing — the
    /// technique <c>RyzenCurveOptimizer.CanWrite</c> and <c>Hwmon.CanWrite</c> established in this tree: a sysfs
    /// attribute does not act until a <c>write()</c> happens, so the probe has no side effect on the SMU. Kept
    /// beside its only caller rather than borrowed, so the probe and the rule are read together; it is asked
    /// about EXISTING files only (see the ordering in the policy), so a missing node cannot read as a refusal.
    /// A permission denial, an unreadable directory above it and a filesystem that vanished under us all answer
    /// false here — which the caller treats as "not writable", the safe direction: it can at worst offer an
    /// install that is idempotent.</summary>
    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }
}
