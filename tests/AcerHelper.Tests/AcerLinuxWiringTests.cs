using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE LINUX BACKEND'S WIRING, READ AS TEXT — the only way this suite can reach a <c>*.Linux.cs</c> file.
/// <c>AcerHelper.csproj</c> removes <c>**/*.Linux.cs</c> from the test project's TFM (net10.0-windows), so
/// <c>AcerDevice.Linux.cs</c> is not compiled into this assembly at all: it cannot be instantiated, called or
/// reflected on here. It can be READ, and that is what these guards do — the same technique
/// <see cref="AppArgsWiringTests"/> uses for the autostart registration and <see cref="ArchitectureMapTests"/> for
/// the layer map, and for the same reason: the alternative is a claim nothing checks.
///
/// WHAT A GUARD LIKE THIS IS WORTH, stated honestly because it is easy to over-read: it pins that the wiring is
/// PRESENT in the source, not that it behaves. <c>SysfsPowerProfiles("acer-wmi")</c> being spelled at the call
/// site says nothing about what that port does with the token — which is why the token's own selection mode is
/// argued in <c>PowerProfiles.Linux.cs</c> and the acceptance check for it is on hardware (the node this port
/// chose is exposed as <c>SysfsPowerProfiles.ProfilePath</c>). What these rows DO catch is the class of mistake
/// this change is made of: a port that silently kept the inherited wiring, a chip probe that drifted off the
/// acer device, and a deleted tier quietly growing back.
///
/// THE DELETION GUARD IS THE POINT of the second half. The Linuwu-Sense tier was removed by an owner decision,
/// and its return would otherwise be invisible: an out-of-tree module check reads as harmless hardening, and the
/// five controls behind it read as extra features. Making it a decision means reddening a test that has to be
/// reasoned about — and deleted on purpose.
/// </summary>
public class AcerLinuxWiringTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>Every C# file under a subtree of the repository. The subtree is asserted to exist for the same
    /// reason <see cref="Source"/> asserts a file: a guard over a renamed or deleted directory would otherwise
    /// pass by having nothing to read.</summary>
    private static string[] SourcesUnder(string relativeDir)
    {
        var root = Path.Combine(Root(), relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(root), $"the tree no longer has '{relativeDir}' — this guard is looking at nothing");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        return files;
    }

    // ---- the wiring that must be there ----

    /// <summary>The profile port asks for the Acer handler BY NAME. This is the row that catches the failure mode
    /// the token exists for: a port built with no argument takes the legacy ACPI alias when it is writable, and a
    /// write to the alias fans out to every registered handler while reading back as <c>custom</c> — so the
    /// machine would report a profile the app cannot name, on a node two drivers share.</summary>
    [Fact]
    public void TheAcerBackendBindsTheProfilePortToItsOwnHandler()
        => Assert.Contains("SysfsPowerProfiles(\"acer-wmi\")",
                           Source("Infrastructure/Vendors/Acer/AcerDevice.Linux.cs"), StringComparison.Ordinal);

    /// <summary>The fan port and the sensors both find the chip through the shared identity check — the name AND
    /// the driver behind the device path. Reading it here rather than looking for the string "acer" keeps the
    /// guard meaningful: a probe that went back to <c>Hwmon.BestFanSource</c> (the read-only telemetry heuristic,
    /// which would bind a CONTROL to whichever chip has the most fans) stops naming this and reddens.</summary>
    [Theory]
    [InlineData("Infrastructure/Vendors/Acer/AcerDevice.Linux.cs", "AcerHwmonChip.Matches")]
    [InlineData("Infrastructure/Vendors/Acer/AcerFanPort.cs", "/sys/devices/platform/acer-wmi")]
    public void TheAcerChipIsFoundByNameAndDriver(string relativePath, string required)
        => Assert.Contains(required, Source(relativePath), StringComparison.Ordinal);

    /// <summary>
    /// NO INHERITED FAN PORT MAY SURVIVE ON AN ACER CHIP.
    ///
    /// The generic <c>HwmonFanControl</c> is a fallback that CAN bind to this very chip: on this kernel the acer
    /// duty node really is <c>pwmN</c> (pwm1/pwm2), which is precisely the pair that class looks for. It only
    /// needs ONE channel, so <c>AcerFanPort</c> wins whenever both are writable — but on a model that
    /// exposes one fan, or with a partial udev rule, the inherited port would stay and drive that fan with the
    /// GENERIC encoding, whose numbers mean something else on acer-wmi: its "Max" is <c>pwm_enable = 1</c>, which
    /// the kernel reads as CUSTOM, so the fan would be put into manual mode while the UI said Max, and its
    /// <c>Dispose</c> writes enable 2 (auto) into a latch the user set. Silent, and wrong in the one place where
    /// being wrong is audible.
    ///
    /// So the wiring nulls the port when the acer chip is present but the Acer port could not be built. The
    /// pattern is the LINE that does it rather than the two tokens on it, because the suppression is a single
    /// decision and a guard satisfied by <c>FanControl = null</c> anywhere in the file would not be about this
    /// branch at all. Deleting or reformatting that line reddens this row, which is intended: it is the line the
    /// hazard lives in, and the repair is to state the new reason in its place. (The chip-absent case must NOT be
    /// suppressed — there the inherited port belongs to whatever other chip the generic path found — which is why
    /// the pattern carries its own condition rather than matching a bare assignment.)
    /// </summary>
    [Fact]
    public void AnAcerChipNeverKeepsTheInheritedFanPort()
        => Assert.Contains("else if (AcerChip() != null) FanControl = null;",
                           Source("Infrastructure/Vendors/Acer/AcerDevice.Linux.cs"), StringComparison.Ordinal);

    /// <summary>
    /// BOTH TEMPERATURE ROWS GO THROUGH THE HOLD, one row per channel, because either channel wired straight to
    /// its node would be the reported bug back again.
    ///
    /// What the guard is for: the HOLD is pinned by <see cref="AcerTemperatureHoldTests"/>, but the hold being
    /// right says nothing about the wiring using it — this file is not compiled into this suite, so nothing here
    /// can observe the snapshot. Two regressions it catches, both one line and both invisible to every other test
    /// in the tree: the row reading its node directly (the EC's intermittent 0 reaching the Monitor, which is what
    /// the owner saw as "GPU 0 °C"), and a hold being SHARED between the channels (the CPU's real reading standing
    /// in for a GPU channel that has never answered — the memory must be the channel's own). Requiring the
    /// assignment itself, per channel, is what makes the rows about THIS wiring rather than about the type name
    /// appearing somewhere in the file; reformatting them reddens the rows on purpose, and the repair is to state
    /// the reason in the new shape.
    ///
    /// The CPU row is here for the symmetry the hold itself keeps: "the EC has no value" means one thing on this
    /// backend, and an exception on the channel where it does not trigger in practice is exactly how the two
    /// drift apart later.
    /// </summary>
    [Theory]
    [InlineData("CpuTempC = _cpuTemp.Reading(")]
    [InlineData("GpuTempC = _gpuTemp.Reading(")]
    public void TheTemperatureRowsGoThroughTheHold(string required)
        => Assert.Contains(required, Source("Infrastructure/Vendors/Acer/AcerDevice.Linux.cs"), StringComparison.Ordinal);

    /// <summary>
    /// THE STATUS LINE IS CHOSEN FROM WHAT THE WIRING BUILT, not asserted once for every machine.
    ///
    /// The line that used to stand here was ONE sentence set unconditionally, and it claimed that profiles, fans
    /// and temperatures all come from the driver. A user without the hardware-access grant gets no fan section at
    /// all — the pwm nodes are not writable, no Acer port is built, and the generic port is suppressed so it
    /// cannot take over with the wrong encoding — and was shown a message saying the fan side works. It lied in
    /// exactly the case where the user needs to be told what to do, which is why the actionable state has its own
    /// sentence.
    ///
    /// Three rows, and each fails on its own kind of loss: the flag disappears (the line stops depending on what
    /// was built), the chip-absent condition disappears (the "parameters not installed" state inherits the
    /// promise that Acer features are live), or the actionable sentence disappears (the read-only case falls back
    /// to the generic line, which is the original lie). The third row pins a FRAGMENT of the English key rather
    /// than the whole sentence: rewording the tail should not redden anything, but replacing the sentence with a
    /// different one is the change this row exists to make visible — and it is the string the localization entry
    /// is keyed by, so the two must be updated together.
    /// </summary>
    [Theory]
    [InlineData("_fanPortBuilt")]
    [InlineData("if (_acerHwmon == null)")]
    [InlineData("The fans are read-only")]
    public void TheStatusLineIsChosenFromWhatTheWiringBuilt(string required)
        => Assert.Contains(required, Source("Infrastructure/Vendors/Acer/AcerDevice.Linux.cs"), StringComparison.Ordinal);

    /// <summary>
    /// THE POLICY FILE DOES NO I/O — and this is the structural half of the deletion pin above, because the
    /// behavioural half is not enough on its own. A verifier inlined the deleted AC walk straight into
    /// <c>AcerMappedProfiles.Selectable()</c> and the whole suite stayed GREEN: the container passes /sys through,
    /// the host is on AC, and a gate that reads "on AC" is therefore a no-op in exactly the environment the tests
    /// run in. No test that EXERCISES the port can see a gate whose input is the machine it is running on.
    ///
    /// What closes it is the file's own rule, which its header comment states: this is the injectable-policy half
    /// of the two decorators, and I/O arrives as delegates (the shape DelegatePorts.cs uses) precisely so the
    /// policy can be driven by a test. A gate reading the power source here would break that rule twice over — it
    /// would make the port untestable in the environment that matters, and it would put a Linux-only input into a
    /// port whose whole purpose is parity with Windows, which has no power-source input anywhere on the profile
    /// path. So the absence of any I/O token is asserted rather than trusted: no node path, no file API, no path
    /// arithmetic. The strings are the tokens themselves and not a regex over "File" or "Path", because the
    /// comment text of a file may name what it must not do — as this very file's header does — and a guard that
    /// tripped on prose would be deleted by the first person it annoyed rather than obeyed.
    ///
    /// A mutation that reddens a row: put the AC walk (or any other filesystem read) back into
    /// Infrastructure/Vendors/Acer/AcerProfilePorts.cs. The repair is to inject the input as a delegate.
    /// </summary>
    [Theory]
    [InlineData("/sys/")]          // a node path
    [InlineData("System.IO")]      // the file APIs' namespace
    [InlineData("File.")]          // File.ReadAllText / File.Exists / …
    [InlineData("Directory.")]     // Directory.EnumerateFiles / …
    [InlineData("Path.Combine")]   // path arithmetic
    public void TheInjectablePolicyFileDoesNoIo(string forbidden)
    {
        var source = Source("Infrastructure/Vendors/Acer/AcerProfilePorts.cs");

        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ---- the tier that must NOT come back ----

    /// <summary>
    /// The deleted Linuwu-Sense tier, and the two dangling references that survived it for a while.
    ///
    /// <c>LinuwuRoot</c> is the module's path constant and <c>BatteryGatedProfiles</c> is the decorator that
    /// greyed the profile set out on battery — both removed by owner decision on 2026-09-20, the first because
    /// the Linux backend is module-free BY CONSTRUCTION (everything it offers comes from mainline acer-wmi and
    /// hidraw) and the second because it had no Windows analogue and its stated justification was measured
    /// against a different handler on a different node (see AcerProfilePorts.cs).
    ///
    /// THE <c>OnAc</c> ROW WAS DELETED WHEN THE POLICY CAME BACK, and that is a decision rather than relaxation.
    /// The owner later asked for NitroSense parity (on battery only Eco and Balanced; on AC Quiet, Balanced,
    /// Performance and Turbo), so a per-source policy is real again — but it is a PURE vendor declaration
    /// (<c>AcerProfiles.IsAvailable</c>, surfaced through <c>IProfileAvailability</c>), with the decision and
    /// the apply in <c>LaptopService</c>. Keeping the raw token row would forbid the legitimate naming the
    /// capability needs while catching nothing of the deleted I/O walk, which the rows above still do.
    ///
    /// A mutation that reddens each row: re-add the constant or call the deleted decorator again. Both are
    /// caught anywhere under Infrastructure/, including in a file that does not exist yet.
    /// </summary>
    [Theory]
    [InlineData("Infrastructure", "LinuwuRoot")]
    [InlineData("Infrastructure", "/sys/module/linuwu_sense")]   // the same path written out rather than named
    [InlineData("Infrastructure", "BatteryGatedProfiles")]
    public void TheDeletedLinuwuTierAndThePowerGateStayDeleted(string subtree, string token)
    {
        var offenders = SourcesUnder(subtree)
            .Where(file => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Root(), file))
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"'{token}' is back under {subtree}/ — the Linuwu-Sense tier and the AC/battery profile gate were "
            + "deleted on purpose (the Linux backend is module-free by construction and Windows has no such "
            + "gate), so returning either one is a decision to argue rather than a line to restore:\n  "
            + string.Join("\n  ", offenders));
    }
}
