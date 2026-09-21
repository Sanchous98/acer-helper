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
/// REACHED, and under /sys/class the containing directory is itself a link. The tree below therefore mirrors that
/// shape exactly — a link inside a link, with a target written relative to the real directory — because a fixture
/// in which the two spellings agree would pass against a resolver that is wrong on the real thing. That is not
/// hypothetical: this fixture caught a doubling bug in the first version of the helper.
///
/// The rows arrange a throwaway tree rather than reading /sys: a test that read this machine's /sys tree would
/// assert something about the machine, not about the code.
/// </summary>
public class SysfsLinkTests
{
    /// <summary>Creates a throwaway tree and returns its root, or null when this host cannot create symlinks at
    /// all (a Windows host without developer mode). xunit 2 has no skip, so the rows below return early on null:
    /// the suite is run on Linux, where the links can be made and the rows are live — on a host that cannot make
    /// them there is genuinely nothing to check, and a red row there would be about the runner.</summary>
    private static string? NewTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "acer-sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "probe"), "somewhere-else");
            try { File.Delete(Path.Combine(root, "probe")); } catch { /* a leftover link in a temp dir is harmless */ }
            return root;
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { /* nothing to clean */ }
            return null;
        }
    }

    /// <summary>The AN18-61's hwmon shape: <c>class/hwmon/hwmon10</c> is itself a link into the device tree, and
    /// the chip's own <c>device</c> link inside it is written relative to THAT real directory
    /// ("../../../acer-wmi") — exactly the case a lexical resolution gets wrong. The expected answers are the
    /// kernel's: the platform device for the chip's <c>device</c>, and the hwmon directory for the class entry
    /// itself (an intermediate link resolves too).</summary>
    [Fact]
    public void AClassEntryResolvesToTheDeviceBehindIt()
    {
        if (NewTree() is not { } root) return;
        try
        {
            var device = Path.Combine(root, "devices", "platform", "acer-wmi");
            var hwmon = Path.Combine(device, "hwmon", "hwmon10");
            Directory.CreateDirectory(hwmon);
            File.WriteAllText(Path.Combine(hwmon, "name"), "acer");
            Directory.CreateSymbolicLink(Path.Combine(hwmon, "device"), "../../../acer-wmi");
            Directory.CreateDirectory(Path.Combine(root, "class", "hwmon"));
            Directory.CreateSymbolicLink(Path.Combine(root, "class", "hwmon", "hwmon10"),
                                        "../../devices/platform/acer-wmi/hwmon/hwmon10");

            var spelled = Path.Combine(root, "class", "hwmon", "hwmon10");

            Assert.Equal(device, SysfsLink.RealPath(Path.Combine(spelled, "device")));
            Assert.Equal(hwmon, SysfsLink.RealPath(spelled));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The vendor-token selection, driven the way SysfsPowerProfiles' handler mode drives it against a
    /// mirror of this machine's two handlers: the AMD node (AMDI0103:00) and the Acer one. The token has to pick
    /// exactly the Acer node — that is what keeps a profile write off the legacy alias, which fans out to every
    /// registered handler — and the assertion is on the CHOICE rather than on a path string, so it stays about the
    /// rule instead of about the fixture's spelling.</summary>
    [Fact]
    public void TheVendorTokenPicksItsOwnHandlerOnly()
    {
        if (NewTree() is not { } root) return;
        try
        {
            var classes = Path.Combine(root, "class", "platform-profile");
            Directory.CreateDirectory(classes);
            foreach (var (device, entry) in new[]
                     {
                         ("acer-wmi", "platform-profile-1"),
                         ("AMDI0103:00", "platform-profile-0"),
                     })
            {
                var node = Path.Combine(root, "devices", "platform", device, "platform-profile", entry);
                Directory.CreateDirectory(node);
                Directory.CreateSymbolicLink(Path.Combine(classes, entry),
                                            $"../../devices/platform/{device}/platform-profile/{entry}");
            }

            var picked = Directory.EnumerateDirectories(classes)
                .Where(dir => SysfsLink.RealPath(dir) is { } real
                              && real.Contains("acer-wmi", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal("platform-profile-1", Path.GetFileName(Assert.Single(picked)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A path with no links in it resolves to itself (nothing is invented), and a path that names nothing
    /// is null rather than a well-formed guess: the callers read null as "not this device", which is the safe
    /// answer for a chip probe or a profile node.</summary>
    [Fact]
    public void AnOrdinaryPathResolvesToItself_AndAnUnresolvableOneToNull()
    {
        if (NewTree() is not { } root) return;
        try
        {
            var plain = Path.Combine(root, "devices", "platform", "acer-wmi");
            Directory.CreateDirectory(plain);

            Assert.Equal(plain, SysfsLink.RealPath(plain));
            Assert.Null(SysfsLink.RealPath(Path.Combine(root, "class", "hwmon", "hwmon99", "device")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
