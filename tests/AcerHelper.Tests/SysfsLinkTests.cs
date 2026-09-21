using System.Diagnostics;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The one piece of path logic the Linux backends share: turning a /sys/class entry into the REAL path of the
/// device behind it. It has its own file because the alternative — the BCL's resolver — is measurably wrong here,
/// and because both Linux backends depend on it: the Acer profile port picks the acer-wmi node with it, and the
/// hwmon chip probe identifies its driver with it.
///
/// WHY THE BCL IS NOT USED, measured on the AN18-61 rather than argued:
/// <c>new DirectoryInfo("/sys/class/hwmon/hwmon10/device").ResolveLinkTarget(returnFinalTarget: true)</c> answers
/// <c>/sys/acer-wmi</c> while the kernel (and <c>readlink -f</c>) answer <c>/sys/devices/platform/acer-wmi</c>.
/// .NET combines a link's target with the path as SPELLED; the VFS combines it with the containing directory as
/// REACHED, and under /sys/class the containing directory is itself a link. The rows below therefore mirror that
/// shape exactly — a link inside a link, with a target written relative to the real directory — because a fixture
/// in which the two spellings agree would pass against a resolver that is wrong on the real thing. Every row
/// states the mutation that proves it is not that fixture.
///
/// THE TREE IS A MAP RATHER THAN A DIRECTORY TREE, and the walk's own spelling is why. It is POSIX — rooted at
/// "/", split on '/' — while this suite's gate is run on Windows, where the host refuses a faithful tree
/// outright: <c>Directory.CreateDirectory(...\devices\platform\AMDI0103:00)</c> throws IOException "Неверно
/// задано имя папки." (a colon is a drive separator, so the ACPI instance id cannot be a directory name), and
/// <c>%TEMP%</c> is spelled "C:\...", which a "/"-rooted probe path cannot name at all. Nor could the walk read
/// such a tree if one could be built: <c>DirectoryInfo.LinkTarget</c> answers on Windows with '\' as the
/// separator (a link created as "../../../acer-wmi" reads back as "..\..\..\acer-wmi"), and the walk splits a
/// target on '/' alone — the only spelling the kernel writes — so a Windows tree would need the resolver to
/// learn a spelling sysfs never produces.
///
/// The map is therefore not the cheaper fixture but the more faithful one: it is keyed by the kernel's own
/// spellings (<c>/sys/class/hwmon/hwmon10</c>, <c>/sys/devices/platform/acer-wmi</c>), it carries the AMD
/// handler's real name with its colon, and it needs no symlink privilege — the rows that used to return early on
/// a host without one now run everywhere.
///
/// What the map cannot exercise is the two filesystem answers themselves (<see cref="SysfsLink.Walk"/>'s
/// <c>LinkTargetOf</c> and <c>Names</c>), which is why they have a row of their own — on the real filesystem and
/// in the host's own spelling, since what they have to get right is <c>FileSystemInfo</c>'s behaviour and not the
/// walk's arithmetic. See <see cref="TheProbesAnswerTheRealFilesystem"/>.
/// </summary>
public class SysfsLinkTests
{
    /// <summary>The AN18-61's entries as data: the two class trees this logic walks, the links the kernel writes
    /// for them, and the device paths they resolve to. The relative targets are written the way sysfs writes them
    /// — measured with <c>readlink</c> on the machine — so the walk meets the same arithmetic it meets in the
    /// kernel, not a fixture convention.</summary>
    private static Sysfs An1861() => new Sysfs()
        .Link("/sys/class/hwmon/hwmon10", "../../devices/platform/acer-wmi/hwmon/hwmon10")
        .Link("/sys/devices/platform/acer-wmi/hwmon/hwmon10/device", "../../../acer-wmi")
        .Node("/sys/devices/platform/acer-wmi")
        .Node("/sys/devices/platform/acer-wmi/hwmon/hwmon10")
        .Link("/sys/class/platform-profile/platform-profile-1",
              "../../devices/platform/acer-wmi/platform-profile/platform-profile-1")
        .Node("/sys/devices/platform/acer-wmi/platform-profile/platform-profile-1")
        // The AMD node's real name, colon included: it is what the token rule has to reject, and it is the name a
        // materialised fixture could not carry on Windows.
        .Link("/sys/class/platform-profile/platform-profile-0",
              "../../devices/platform/AMDI0103:00/platform-profile/platform-profile-0")
        .Node("/sys/devices/platform/AMDI0103:00/platform-profile/platform-profile-0");

    /// <summary>The AN18-61's hwmon shape: <c>class/hwmon/hwmon10</c> is itself a link into the device tree, and
    /// the chip's own <c>device</c> link inside it is written relative to THAT real directory
    /// ("../../../acer-wmi") — exactly the case a lexical resolution gets wrong. The expected answers are the
    /// kernel's: the platform device for the chip's <c>device</c>, and the hwmon directory for the class entry
    /// itself (an intermediate link resolves too).
    ///
    /// MUTATION that reddens this row: leave the link's own component in `resolved`, so the target is resolved
    /// against a context that names the link twice — the doubling bug this file's first version shipped, and what
    /// the note above the queue in <see cref="SysfsLink.Walk"/> warns about. The walk then answers a path this
    /// tree does not name, so the first assertion reads null (measured) where the platform device is
    /// asserted.</summary>
    [Fact]
    public void AClassEntryResolvesToTheDeviceBehindIt()
    {
        var tree = An1861();

        Assert.Equal("/sys/devices/platform/acer-wmi",
                     SysfsLink.Walk("/sys/class/hwmon/hwmon10/device", tree.LinkTargetOf, tree.Names));
        Assert.Equal("/sys/devices/platform/acer-wmi/hwmon/hwmon10",
                     SysfsLink.Walk("/sys/class/hwmon/hwmon10", tree.LinkTargetOf, tree.Names));
    }

    /// <summary>The vendor-token selection, driven the way SysfsPowerProfiles' handler mode drives it against a
    /// mirror of this machine's two handlers: the AMD node (AMDI0103:00) and the Acer one. The token has to pick
    /// exactly the Acer node — that is what keeps a profile write off the legacy alias, which fans out to every
    /// registered handler — and the assertion is on the CHOICE rather than on a path string, so it stays about the
    /// rule instead of about the fixture's spelling.
    ///
    /// MUTATION that reddens this row: make the walk answer with the path it was given, without resolving anything.
    /// Neither class entry then contains "acer-wmi", the filter picks nothing, and <c>Assert.Single</c> throws —
    /// which is what says the token is matched against the RESOLVED path.</summary>
    [Fact]
    public void TheVendorTokenPicksItsOwnHandlerOnly()
    {
        var tree = An1861();

        var picked = tree.EntriesOf("/sys/class/platform-profile")
            .Where(dir => SysfsLink.Walk(dir, tree.LinkTargetOf, tree.Names) is { } real
                          && real.Contains("acer-wmi", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal("platform-profile-1", Path.GetFileName(Assert.Single(picked)));
    }

    /// <summary>A path with no links in it resolves to itself (nothing is invented), and a path that names nothing
    /// is null rather than a well-formed guess: the callers read null as "not this device", which is the safe
    /// answer for a chip probe or a profile node.
    ///
    /// MUTATION that reddens this row: return the walked path without asking whether it names anything (drop the
    /// <c>names</c> check in <see cref="SysfsLink.Walk"/>). The missing hwmon10 then comes back as a path instead
    /// of null, and a probe would go on to read nodes that are not there.</summary>
    [Fact]
    public void AnOrdinaryPathResolvesToItself_AndAnUnresolvableOneToNull()
    {
        var tree = An1861();

        Assert.Equal("/sys/devices/platform/acer-wmi",
                     SysfsLink.Walk("/sys/devices/platform/acer-wmi", tree.LinkTargetOf, tree.Names));
        Assert.Null(SysfsLink.Walk("/sys/class/hwmon/hwmon99/device", tree.LinkTargetOf, tree.Names));
    }

    /// <summary>
    /// THE TWO PROBES THEMSELVES, on the real filesystem — the half of this file that the map above cannot reach,
    /// because they are the only place that asks the HOST and a host-native answer is exactly what a map replaces.
    /// Leaving them uncovered is not a gap on paper: a mutation of either one keeps every row above green while
    /// breaking a real machine, which is why this row exists.
    ///
    /// WHAT IT DRIVES, and why each half is here:
    ///   * a real FILE, because <c>Directory.Exists</c> is false for one — the <c>File.Exists</c> half of
    ///     <c>Names</c> is the only thing that can answer true, and dropping it makes every file-naming path
    ///     resolve to null (a control that silently disappears);
    ///   * a real DIRECTORY LINK, because that is what a sysfs class entry is
    ///     (<c>/sys/class/hwmon/hwmon10</c> → <c>…/devices/platform/acer-wmi/hwmon/hwmon10</c>), and
    ///     <c>LinkTargetOf</c> has to answer for it from the DIRECTORY half — <c>FileInfo</c> answers false and
    ///     null for a link to a directory, so a single-attempt rewrite resolves the class entry to nothing,
    ///     <c>AcerHwmonChip.Matches</c> never matches, <c>AcerFanPort</c> is never built and the fan rows vanish.
    ///
    /// THE LINK IS A JUNCTION (<c>mklink /J</c>), not a symlink, because a junction is the one directory reparse
    /// point Windows grants without the symlink privilege — and <c>DirectoryInfo.LinkTarget</c> answers its target
    /// the same way. A host that refuses to make one fails this row on its own assertion rather than passing
    /// quietly, since a probe that is never exercised is the thing the row removes.
    ///
    /// MUTATIONS that redden it, both run: gate <c>LinkTargetOf</c> on the file side
    /// (<c>new FileInfo(path).Exists ? new FileInfo(path).LinkTarget : null</c> — what a rewrite that forgets the
    /// directory case looks like), or drop the <c>File.Exists</c> half of <c>Names</c>. Measured here while
    /// writing it, and worth knowing before trusting a "simplification" of that method: the BARE
    /// <c>new FileInfo(path).LinkTarget</c> does NOT redden this row, because <c>LinkTarget</c> reads the reparse
    /// point whether or not <c>FileInfo.Exists</c> — which is false for a directory link — agrees; what breaks a
    /// real machine is the <c>Exists</c> GATE, not the read.
    /// </summary>
    [Fact]
    public void TheProbesAnswerTheRealFilesystem()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "acer-helper-sysfslink-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(baseDir, "target");
        var link = Path.Combine(baseDir, "link");
        var file = Path.Combine(baseDir, "pwm1");
        Directory.CreateDirectory(target);
        File.WriteAllText(file, "");
        try
        {
            Assert.True(SysfsLink.Names(file), "a path naming a FILE names something — Directory.Exists alone says it does not");
            Assert.False(SysfsLink.Names(file + ".absent"));

            Assert.Null(SysfsLink.LinkTargetOf(file));       // a plain file is not a link
            Assert.Null(SysfsLink.LinkTargetOf(target));     // nor is a plain directory

            Assert.True(TryJunction(link, target),
                        $"this host could not create a junction at '{link}' — the directory half of LinkTargetOf cannot be exercised without one");

            var reported = SysfsLink.LinkTargetOf(link);
            Assert.NotNull(reported);
            Assert.Equal(target.TrimEnd(Path.DirectorySeparatorChar),
                         reported!.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        }
        finally
        {
            // The junction goes FIRST and on its own: a recursive delete of a tree containing one throws
            // ("the parameter is incorrect" — measured), where deleting the reparse point leaves its target alone.
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { /* the temp dir below still goes */ }
            try { Directory.Delete(baseDir, recursive: true); } catch { /* a leftover temp dir is not a failure */ }
        }
    }

    /// <summary>Create a directory junction, which needs no privilege where a symlink does. False when the host
    /// would not make one (no cmd.exe, a filesystem without reparse points, a policy that forbids it).</summary>
    private static bool TryJunction(string link, string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
        }
        catch { return false; }
    }

    /// <summary>A sysfs-shaped tree as DATA, because the walk's spelling is POSIX and this suite's gate is on
    /// Windows (see the class comment). It answers exactly the two questions the walk asks of the filesystem —
    /// "is there a link at this path, and to what" once per component, and "does this path name anything" once at
    /// the end — and it names nothing it was not told about, so a path absent from the map is absent from the tree.
    ///
    /// A link NAMES something, as it does on the real filesystem (a class entry is a symlink and it exists), and
    /// every ancestor of a registered path is registered too: a map in which /sys/devices had children but no
    /// existence of its own would be a tree no kernel could build, and the walk would be reading it.</summary>
    private sealed class Sysfs
    {
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        internal Sysfs Link(string path, string target)
        {
            _links[path] = target;
            return Register(path);
        }

        internal Sysfs Node(string path) => Register(path);

        internal string? LinkTargetOf(string path) => _links.GetValueOrDefault(path);

        internal bool Names(string path) => _paths.Contains(path);

        /// <summary>The entries directly under a directory: every path the map knows whose parent it is. The ports
        /// enumerate with <c>Directory.EnumerateDirectories</c>; what the rows need is the same SET, not the same
        /// call. Sliced on '/' rather than asked of <c>Path</c>, so the map stays in the kernel's spellings
        /// whatever the host makes of them.</summary>
        internal string[] EntriesOf(string parent) => _paths
            .Where(p => p[..p.LastIndexOf('/')] == parent)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        private Sysfs Register(string path)
        {
            for (var p = path; p.Length > 1; p = p[..p.LastIndexOf('/')]) _paths.Add(p);
            return this;
        }
    }
}
