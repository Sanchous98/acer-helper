using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE TRANSPORT SDK'S LAYOUT (docs/vendor-plugins.md §4.4) — the slice that makes the vendor-agnostic
/// transports a source-shared, first-class contract instead of a corner of <c>Vendors/Generic/</c>.
///
/// WHY THIS EXISTS, and why it is a SOURCE-LAYOUT guard rather than a behavioural one: the whole point of the
/// move is that the host and every vendor plugin compile ONE copy of the transports and the ABI, so the WMI COM
/// layer cannot drift between them (§4.4 option B is rejected for exactly that reason). The compiler cannot
/// state that — it happily builds whichever folder a <c>using</c> happens to point at — and a future author
/// adding a feature to the Acer backend could quietly re-grow a private copy under <c>Vendors/Generic/</c>
/// without any build turning red. This file is the fence: the files live in <c>Infrastructure/Plugins/Sdk/</c>,
/// the OLD location holds none of them, and the props that hands them to a plugin names the same set.
///
/// A FAILURE HERE IS A DECISION, NOT A BUG: if a transport genuinely needs to leave the SDK, that is a rule
/// change to argue (and to make here) rather than a line to restore.
/// </summary>
public class PluginSdkLayoutTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The canonical SDK file set, by the names the props imports. Kept as ONE list so the two
    /// assertions below (present here, absent there) cannot disagree about what the SDK is.</summary>
    private static readonly string[] SdkFiles =
    [
        "WmiSession.Windows.cs", "WbemInterop.Windows.cs", "WmiInvoker.Windows.cs",
        "SysfsInvoker.Linux.cs", "FirmwareAttributes.Linux.cs", "Hwmon.Linux.cs",
        "SysfsLink.cs", "DelegatePorts.cs", "ProfileKind.cs",
    ];

    /// <summary>
    /// THE SDK OWNS THE TRANSPORTS AND THE OLD HOME DOES NOT. Both halves are asserted together because either
    /// one alone is satisfiable by a mistake: "the files exist in the SDK" stays green if a COPY is left behind
    /// in <c>Vendors/Generic/</c> (the drift this move exists to end), and "the old folder is empty of them"
    /// stays green if they were simply deleted. The pair is what says the move was a move.
    ///
    /// MUTATION that reddens it: copy any one of the nine files back under
    /// <c>Infrastructure/Vendors/Generic/</c>, or delete one from the SDK.
    /// </summary>
    [Fact]
    public void TheSdkHoldsTheTransportsAndVendorsGenericNoLongerDoes()
    {
        var sdkDir = Path.Combine(Root(), "Infrastructure", "Plugins", "Sdk");
        var oldDir = Path.Combine(Root(), "Infrastructure", "Vendors", "Generic");

        Assert.True(Directory.Exists(sdkDir), "the tree no longer has Infrastructure/Plugins/Sdk/ — this guard is looking at nothing");

        var missing = SdkFiles.Where(f => !File.Exists(Path.Combine(sdkDir, f))).ToArray();
        Assert.True(missing.Length == 0,
            "the transport SDK is missing a file the props list imports — the host and every plugin would compile "
            + "around it rather than fail, which is the silent drift §4.4 rejects:\n  " + string.Join("\n  ", missing));

        var leftBehind = SdkFiles.Where(f => File.Exists(Path.Combine(oldDir, f))).ToArray();
        Assert.True(leftBehind.Length == 0,
            "a transport SDK file reappeared under Infrastructure/Vendors/Generic/ — that is a second copy the "
            + "vendor plugins would not source-include, exactly the drift the source-shared SDK (§4.4) exists to "
            + "prevent:\n  " + string.Join("\n  ", leftBehind));
    }

    /// <summary>
    /// THE PROPS NAMES EVERY SDK FILE AND THE ABI. The props is the one source of truth a future plugin csproj
    /// imports (§4.4, §5.1) and it is deliberately NOT consumed in this slice, so nothing else in the build reads
    /// it: without this row a file added to the folder but not to the props would silently not be compiled into
    /// the plugin, and a file named in the props but moved away would fail with an obscure MSBuild error at P3's
    /// first plugin build instead of here.
    ///
    /// MUTATION that reddens it: add a tenth <c>.cs</c> to the SDK folder without adding its
    /// <c>&lt;Compile Include&gt;</c>, or rename a file without renaming its props entry.
    /// </summary>
    [Fact]
    public void ThePropsNamesEverySdkFileAndTheAbi()
    {
        var propsPath = Path.Combine(Root(), "Infrastructure", "Plugins", "Sdk", "PluginSdk.props");
        Assert.True(File.Exists(propsPath), "the SDK no longer carries its PluginSdk.props — this guard is looking at nothing");
        var props = File.ReadAllText(propsPath);

        var notInProps = SdkFiles
            .Where(f => !props.Contains(f, StringComparison.Ordinal))
            .ToArray();
        Assert.True(notInProps.Length == 0,
            "a transport SDK file is not named by PluginSdk.props, so a plugin importing the props would not "
            + "compile it (§4.4):\n  " + string.Join("\n  ", notInProps));

        // The ABI is carried by the same props: host and plugins share one Abi.cs, not two copies (§3.1, §4.4).
        foreach (var abi in new[] { "VendorAbi.cs", "AbiStatus.cs", "Capability.cs", "Operation.cs" })
            Assert.Contains(abi, props, StringComparison.Ordinal);
    }
}
