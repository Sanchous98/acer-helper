using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests;

/// <summary>
/// THE ACER-NITRO VENDOR PLUGIN SCAFFOLD, READ AS TEXT (docs/vendor-plugins.md §5.1, §4.4, §3.1, §3.2).
///
/// WHY SOURCE-TEXT AND NOT A RUNTIME TEST. The plugin is a SEPARATE Native AOT shared library
/// (<c>plugins/AcerHelper.Vendor.acer-nitro/</c>): its thunks are <c>[UnmanagedCallersOnly]</c>, which the
/// runtime FORBIDS calling from managed C#, and the test project does not (and must not) reference it. So the
/// export contract, the source-include shape and the "the wiring files are NOT here yet" boundary cannot be
/// exercised by executing the plugin here — they can be READ, which is what these guards do. The same technique
/// and the same reasoning as <see cref="ProofPluginSourceTests"/>.
///
/// WHAT THE GUARDS PIN (each is a claim the scaffold makes, and the class of mistake a change would make):
/// <list type="bullet">
/// <item>the plugin shares code by SOURCE-INCLUDE (Domain + the SDK props + an explicit Acer list), never by a
/// <c>ProjectReference</c> to the host — a reference would both fail to export (§2.1) and drag the host in;</item>
/// <item>all eight <c>ah_*</c> entry points are declared with exactly the shared <see cref="VendorAbi"/> names
/// (a typo is a load-time miss with no compile error);</item>
/// <item>every thunk is try/catch-wrapped (an escaping managed exception across the C boundary is UB, §3.2);</item>
/// <item>the Acer WIRING files (<c>AcerDevice.*</c>, <c>AcerBattery.Windows.cs</c>) are NOT among the included
/// Acer files: they extend the host-only <c>GenericDevice</c> and are P3/P4 work, so their accidental inclusion
/// here would mean the plugin had started to swallow the host's wiring.</item>
/// </list>
/// </summary>
public class AcerPluginScaffoldTests
{
    private const string PluginCsproj = "plugins/AcerHelper.Vendor.acer-nitro/AcerHelper.Vendor.acer-nitro.csproj";
    private const string PluginSource = "plugins/AcerHelper.Vendor.acer-nitro/AcerPlugin.cs";

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
    /// here is a load-time miss the compiler cannot see. Every name must be present literally, and nothing extra
    /// must be claimed (a stray EntryPoint is dead surface or a sign the set drifted).</summary>
    [Fact]
    public void AcerPluginExportsAllEightAbiEntryPoints()
    {
        var source = Source(PluginSource);

        foreach (var name in AbiEntryPoints)
            Assert.Contains($"EntryPoint = \"{name}\"", source);

        var declared = Regex.Matches(source, @"EntryPoint\s*=\s*""([a-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(AbiEntryPoints.OrderBy(n => n), declared.OrderBy(n => n));
    }

    /// <summary>NO EXCEPTION MAY ESCAPE A THUNK (§3.2). Two textual facts pin it: every thunk body contains a
    /// <c>try</c>+<c>catch</c>, and the whole file has no <c>throw</c> (a throw is either escaping a thunk or an
    /// exception the helpers would have to catch anyway).</summary>
    [Fact]
    public void EveryThunkIsWrappedAndNothingThrows()
    {
        var source = Source(PluginSource);

        var thunkBodies = source.Split("[UnmanagedCallersOnly(").Skip(1).ToArray();
        Assert.Equal(AbiEntryPointCount, thunkBodies.Length);

        foreach (var body in thunkBodies)
        {
            Assert.Contains("try", body);
            Assert.Contains("catch", body);
        }

        Assert.DoesNotContain("throw ", source);
        Assert.DoesNotContain("throw;", source);
    }

    /// <summary>The plugin is its own assembly: a <c>ProjectReference</c> to the host would drag the whole host
    /// (Avalonia, UI, Composition) into the plugin AND export nothing (Native AOT exports only
    /// <c>[UnmanagedCallersOnly]</c> methods in the PUBLISHED assembly, §2.1). All shared code must arrive by
    /// source-include: Domain/**, the SDK props, and an explicit Acer file list.</summary>
    [Fact]
    public void AcerPluginSourceIncludesDomainSdkAndAcerFilesWithoutReferencingTheHost()
    {
        var csproj = Source(PluginCsproj);

        // Domain, as a tree (it is dependency-free; §4.4's SDK is the transport half).
        Assert.Contains("Compile Include=\"..\\..\\Domain\\**\\*.cs\"", csproj);

        // The SDK transport/ABI list, by Import (not one file at a time here — the list is the props' job).
        Assert.Contains("Import Project=\"..\\..\\Infrastructure\\Plugins\\Sdk\\PluginSdk.props\"", csproj);

        // The Acer files are an EXPLICIT list, so what is in this slice (and what P3 defers) is obvious.
        foreach (var file in new[]
        {
            "AcerProfiles.cs", "AcerModel.cs", "AcerFanPort.cs", "AcerGpuMux.cs",
            "AcerHotkeyReports.cs", "RgbEffects.cs", "AcerEcHidController.cs",
            "AcerHotkeys.Windows.cs", "AcerHotkeys.Linux.cs",
        })
        {
            Assert.Contains($"Compile Include=\"..\\..\\Infrastructure\\Vendors\\Acer\\{file}\"", csproj);
        }

        Assert.DoesNotContain("<ProjectReference", csproj);
    }

    /// <summary>THE P3/P4 BOUNDARY, PINNED. <c>AcerDevice.cs</c> / <c>.Windows.cs</c> / <c>.Linux.cs</c> and
    /// <c>AcerBattery.Windows.cs</c> extend the host-only <c>GenericDevice</c> and cannot compile standalone, so
    /// this slice must not include them; if a later change adds one, the plugin has started swallowing the host's
    /// wiring and the standalone-build property this slice exists to prove is gone. Checked against the Compile
    /// items (not raw substrings) because the csproj's own comments deliberately NAME these files to explain the
    /// exclusion.</summary>
    [Fact]
    public void AcerPluginDoesNotIncludeTheWiringFiles()
    {
        var csproj = Source(PluginCsproj);

        foreach (var wiring in new[] { "AcerDevice.cs", "AcerDevice.Windows.cs", "AcerDevice.Linux.cs", "AcerBattery.Windows.cs" })
            Assert.DoesNotContain($"Compile Include=\"..\\..\\Infrastructure\\Vendors\\Acer\\{wiring}\"", csproj);
    }

    /// <summary>The eight §3.2 entry points (kept as a named constant so the two guards that count them agree).</summary>
    private const int AbiEntryPointCount = 8;
}
