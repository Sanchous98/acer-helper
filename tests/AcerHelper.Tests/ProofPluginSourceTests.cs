using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests;

/// <summary>
/// THE PHASE 0 PROOF PLUGIN, READ AS TEXT (docs/vendor-plugins.md §3.2, §5.1, §6 Phase 0 item 4).
///
/// WHY SOURCE-TEXT AND NOT A RUNTIME TEST. The proof plugin is a SEPARATE Native AOT shared library
/// (<c>plugins/AcerHelper.Vendor.Proof/</c>): its thunks are <c>[UnmanagedCallersOnly]</c>, which the runtime
/// FORBIDS calling from managed C#, and the test project does not (and must not) reference it. So the export
/// contract, the no-exception-escaping rule and the project's AOT settings cannot be exercised by executing the
/// plugin here — they can be READ, which is what these guards do. This is the same technique
/// <see cref="AcerLinuxWiringTests"/> uses for a file excluded from its TFM, and for the same reason: the
/// alternative is a claim nothing checks.
///
/// WHAT A GUARD LIKE THIS IS WORTH, stated honestly: it pins that the contract is SPELLED in the source, not
/// that the compiled module behaves. The behaviour is exercised by the §6 ABI round-trip smoke test that loads
/// the BUILT library and calls every export, and by the Phase 0 soak; these guards catch the class of mistake
/// that change is made of — a renamed or missing entry point (a load-time miss with no compile-time error), a
/// thunk that can leak an exception across the C boundary, and a csproj that lost its AOT shape.
/// </summary>
public class ProofPluginSourceTests
{
    private const string PluginCsproj = "plugins/AcerHelper.Vendor.Proof/AcerHelper.Vendor.Proof.csproj";
    private const string PluginSource = "plugins/AcerHelper.Vendor.Proof/ProofPlugin.cs";

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

    /// <summary>The eight <c>ah_*</c> entry points (§3.2), taken from the shared ABI constants rather than typed
    /// a second time, so this guard fails if the source and the contract ever separate.</summary>
    private static readonly string[] AbiEntryPoints =
    [
        VendorAbi.AbiVersion, VendorAbi.PluginId, VendorAbi.Matches, VendorAbi.Create,
        VendorAbi.Invoke, VendorAbi.Free, VendorAbi.Dispose, VendorAbi.SetEventSink,
    ];

    /// <summary>THE EXPORT CONTRACT. A plugin's <c>[UnmanagedCallersOnly(EntryPoint = "...")]</c> strings ARE the
    /// linker contract (§2.1, §3.2): the host resolves each name with <c>NativeLibrary.GetExport</c>, so a typo
    /// here is a load-time miss the compiler cannot see. Every name must be present literally in the source.</summary>
    [Fact]
    public void ProofPluginExportsAllEightAbiEntryPoints()
    {
        var source = Source(PluginSource);

        foreach (var name in AbiEntryPoints)
            Assert.Contains($"EntryPoint = \"{name}\"", source);

        // And nothing extra: the plugin must not claim exports the ABI does not define (a stray EntryPoint would
        // be dead surface, or a sign the set drifted).
        var declared = Regex.Matches(source, @"EntryPoint\s*=\s*""([a-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(AbiEntryPoints.OrderBy(n => n), declared.OrderBy(n => n));
    }

    /// <summary>NO EXCEPTION MAY ESCAPE A THUNK (§3.2). The runtime requires every export body to be wrapped in
    /// try/catch and return a status code; a managed exception crossing into the host is undefined behaviour.
    /// Two textual facts pin it: each thunk's body contains a <c>try</c>+<c>catch</c>, and the whole file has no
    /// <c>throw</c> (a throw is either escaping a thunk or an exception the helpers would have to catch anyway).</summary>
    [Fact]
    public void EveryThunkIsWrappedAndNothingThrows()
    {
        var source = Source(PluginSource);

        // Split the file at each export attribute; every slice but the last is one thunk's body.
        var thunkBodies = source.Split("[UnmanagedCallersOnly(").Skip(1).ToArray();
        Assert.Equal(8, thunkBodies.Length);

        foreach (var body in thunkBodies)
        {
            Assert.Contains("try", body);
            Assert.Contains("catch", body);
        }

        // A throw anywhere in the plugin would be an exception path the boundary does not allow.
        Assert.DoesNotContain("throw ", source);
        Assert.DoesNotContain("throw;", source);
    }

    /// <summary>The project is a Native AOT SHARED library (§2.1, §5.1). <c>PublishAot</c> is what makes the
    /// classlib native; <c>NativeLib=Shared</c> is explicit even though it is the OutputType=Library default;
    /// <c>IsAotCompatible</c> turns on the AOT/trim analyzers that surface warnings the host build would not show
    /// for the same code (§7.1 risk 5).</summary>
    [Fact]
    public void PluginProjectDeclaresNativeAotSharedLibrarySettings()
    {
        var csproj = Source(PluginCsproj);

        Assert.Contains("<PublishAot>true</PublishAot>", csproj);
        Assert.Contains("<NativeLib>Shared</NativeLib>", csproj);
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", csproj);
    }

    /// <summary>THE SOURCE-INCLUDE, NOT A REFERENCE (§2.1, §3.1, §4.4). Native AOT exports only methods in the
    /// PUBLISHED assembly, so the shared ABI is compiled INTO the plugin by <c>&lt;Compile Include=...&gt;</c>;
    /// a <c>ProjectReference</c> to the host would both fail to export and drag the whole host into the plugin.
    /// The four ABI files the task names must each be source-included.</summary>
    [Fact]
    public void PluginProjectSourceIncludesTheAbiFilesAndDoesNotReferenceTheHost()
    {
        var csproj = Source(PluginCsproj);

        foreach (var file in new[] { "VendorAbi.cs", "AbiStatus.cs", "Capability.cs", "Operation.cs" })
            Assert.Contains($"Compile Include=\"..\\..\\Infrastructure\\Plugins\\Abi\\{file}\"", csproj);

        Assert.DoesNotContain("<ProjectReference", csproj);
    }

    /// <summary>THE HOST IS UNAFFECTED. The plugin tree is a sibling of the host and the SDK's implicit
    /// <c>**/*.cs</c> glob would otherwise compile it into the shipping app; <c>AcerHelper.csproj</c> excludes
    /// <c>plugins/**</c> for exactly the reason it excludes <c>tests/**</c> (§5). This guard fails if that
    /// exclusion is ever removed — which would mean the host started swallowing a Native AOT plugin's sources.</summary>
    [Fact]
    public void HostProjectStillExcludesThePluginsTree()
    {
        var hostCsproj = Source("AcerHelper.csproj");

        Assert.Contains("Compile Remove=\"plugins/**\"", hostCsproj);
    }
}
