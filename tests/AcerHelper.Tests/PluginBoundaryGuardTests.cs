using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Adapters;
using AcerHelper.Infrastructure.Plugins.Distribution;
using AcerHelper.Infrastructure.Plugins.Sdk;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// THE PHASE 0 BOUNDARY GUARDS (docs/vendor-plugins.md §6 "Keeping the suite green, and the guards") — the
/// invariants that make the vendor-plugin split an ENFORCED contract rather than a coincidence. Three things are
/// pinned here and nowhere else in a general (directory-driven) form:
///
///   1. THE HOST CORE NAMES NO VENDOR TYPE. The whole point of the plugin architecture is that a non-Acer build
///      no longer carries the Acer integration and vice versa (§0). Phase 1 moves the vendor backends OUT; this
///      guard fences the area they must never reappear in and pins the plugin foundation as vendor-free TODAY.
///   2. EVERY PLUGIN EXPORTS THE ABI ENTRY POINTS. A plugin's <c>[UnmanagedCallersOnly(EntryPoint = "ah_…")]</c>
///      strings are a linker contract the compiler cannot check across the plugin boundary: a rename is a
///      load-time miss (§2.1, §3.2). AOT cannot run in this suite, so the contract is checked from the SOURCE
///      side — the same technique <c>ProofPluginSourceTests</c>/<c>AcerLinuxWiringTests</c> use.
///   3. THE ABI ROUND-TRIP. One composition test drives the REAL adapter stack
///      (<c>PluginAbiRegistry.CreateDefault</c> → <c>AbiV1Adapter</c>) over the fake binding through the FULL path
///      — load → match → create → decode manifest → invoke → decode response → dispose — which the unit tests of
///      <c>PluginLoaderTests</c>/<c>PluginSessionTests</c>/<c>PluginAdapterTests</c> each cover only in pieces.
///
/// OVERLAP, STATED SO IT IS NOT RE-ADDED. <c>ProofPluginSourceTests</c> already pins the export contract and the
/// AOT settings for the PROOF plugin specifically; this file's Guard 2 is the GENERAL, directory-driven version
/// (a new <c>plugins/*/</c> is covered automatically). <c>PluginAdapterTests</c> already pins "DeviceFactory does
/// not reference PluginVendorDevice"; Guard 1 does not repeat it. <c>PluginLoaderTests</c> already covers the
/// unsupported-major gate 2 and the <c>-2</c> gate 3 as unit tests; Guard 3's second fact only re-exercises it
/// through the full composition, not as a substitute.
///
/// MUTATIONS. Each test names the one edit that reddens it in its own docstring; the ones cheap enough to run were
/// verified locally (see the report accompanying this change). A FAILURE HERE IS A DESIGN DECISION, not a bug:
/// if a new cross-boundary dependency is genuinely wanted, the rule must change first.
/// </summary>
public class PluginBoundaryGuardTests
{
    // ---- shared mechanics (the repo idiom of ArchitectureMapTests/DomainNeutralityTests) ---------------------

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Every C# file under a repo-relative directory, with build intermediates excluded so a stale
    /// <c>obj/</c> copy can never be the thing a guard reads.</summary>
    private static List<string> SourcesIn(string relativeDir)
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.Combine(Root(), relativeDir.Replace('/', sep));
        Assert.True(Directory.Exists(root), $"the tree no longer has '{relativeDir}/' — this guard is looking at nothing");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
            .ToList();
    }

    private static string Relative(string path) => Path.GetRelativePath(Root(), path).Replace('\\', '/');

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>Drop <c>//</c> line comments and <c>/* */</c> block comments before searching for a vendor name.
    /// THE TREE'S PROSE NAMES VENDORS ON PURPOSE: Domain/Rgb.cs, Domain/GpuMux.cs and several adapters cite
    /// <c>AcerDevice</c>/<c>EneHidController</c>/<c>AcerModel</c> in &lt;see&gt;/comment text to explain WHY a rule
    /// exists. A name in a comment is documentation, not a dependency — the compiler never resolves it — so the
    /// guard reads CODE only. (This is deliberately narrower than a bare word search: a word search would redden
    /// on the tree's own explanations, which is the guard being wrong, not the code.)</summary>
    private static string StripComments(string text)
    {
        var noBlock = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", "");
    }

    // ---- the forbidden vocabulary (§6) ------------------------------------------------------------------------

    /// <summary>The vendor-specific type names §6 forbids in the host core. Word-boundary matched, so
    /// <c>AcerModel</c> does not match <c>AcerModelJsonContext</c> and a longer, unrelated identifier is not a
    /// false positive.</summary>
    private static readonly string[] VendorTypeIdentifiers =
    [
        "AcerDevice", "DellDevice", "AsusDevice", "AcerEcHidController", "EneHidController",
        "AcerProfiles", "AcerProfilePorts", "AcerFanPort", "AcerModel", "AcerModels", "AcerGpuMux",
        "DellBiosWmi",
    ];

    /// <summary>The vendor namespaces §6 forbids (the <c>using</c> form and the namespace declaration).</summary>
    private static readonly string[] VendorNamespaces =
    [
        "AcerHelper.Infrastructure.Vendors.Acer",
        "AcerHelper.Infrastructure.Vendors.Dell",
        "AcerHelper.Infrastructure.Vendors.Asus",
    ];

    // ==========================================================================================================
    // GUARD 1 — the host core contains no vendor type names
    // ==========================================================================================================

    /// <summary>
    /// THE PHASE 0 INVARIANT THAT HOLDS TODAY: the plugin foundation under <c>Infrastructure/Plugins/</c> names
    /// NO vendor. This is the "no vendor in the host" rule at its narrowest and strongest — the foundation is the
    /// NEW code this phase adds, so it must be vendor-free from its first commit, and it is the subtree §6 means
    /// when it excludes "Infrastructure/Plugins" from the broader source-text rule.
    ///
    /// WHY IT MATTERS: the plugin foundation is the generic loader/session/adapter machinery every vendor plugin
    /// is driven through (§4). If a vendor type name leaks into it, the host has silently re-acquired vendor
    /// knowledge it must not carry — the exact failure the architecture exists to prevent, and one the single
    /// assembly's compiler cannot see because the type is nameable from anywhere (<c>ArchitectureMapTests</c>'
    /// stated limit).
    ///
    /// MUTATION that reddens it: add <c>using AcerHelper.Infrastructure.Vendors.Acer;</c> to
    /// <c>Infrastructure/Plugins/VendorPluginLoader.cs</c>, or name <c>AcerDevice</c> in any foundation source.
    /// </summary>
    [Fact]
    public void ThePluginFoundationNamesNoVendor()
    {
        var offenders = new List<string>();

        foreach (var path in SourcesIn("Infrastructure/Plugins"))
        {
            var code = StripComments(File.ReadAllText(path));

            offenders.AddRange(VendorTypeIdentifiers
                .Where(id => Regex.IsMatch(code, $@"\b{Regex.Escape(id)}\b"))
                .Select(id => $"{Relative(path)}: {id}"));

            offenders.AddRange(VendorNamespaces
                .Where(ns => code.Contains(ns, StringComparison.Ordinal))
                .Select(ns => $"{Relative(path)}: {ns}"));
        }

        Assert.True(offenders.Count == 0,
            "Infrastructure/Plugins/ is the vendor-agnostic plugin foundation (§4) and must name no vendor. A vendor "
            + "name here means the host re-acquired the vendor knowledge the plugin split removes (§0, §6):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE PHASE 0 FORWARD RULE, SCOPED PRECISELY SO IT IS GREEN TODAY. Forbid vendor type names across the host
    /// layers (<c>Domain/</c>, <c>Application/</c>, <c>Infrastructure/</c>, <c>UI/</c>) EXCEPT in the two places
    /// where a vendor name is legitimate RIGHT NOW:
    ///
    ///   * <c>Infrastructure/Vendors/</c> — the vendor backends that still compile into the one assembly (§0);
    ///     Phase 1/3 MOVES them into plugins, it does not rename them in place.
    ///   * <c>Infrastructure/Plugins/</c> — the foundation, handled by the stricter test above; excluded here so
    ///     the two rules do not double-report.
    ///   * <c>Infrastructure/Composition/DeviceFactory.cs</c> — the ONE non-vendor file that names vendors today
    ///     (its <c>AcerDevice</c>/<c>DellDevice</c> branches, <c>DeviceFactory.cs:33-34</c>). This is the file
    ///     §4.1 rewrites in Phase 1 ("the Acer <c>if</c> is deleted"), so the exemption is temporary by design.
    ///     The separate <see cref="DeviceFactorysVendorExemptionIsStillTheKnownPhase1Branches"/> keeps that hole
    ///     from quietly widening.
    ///
    /// WHAT IT CATCHES: a NEW leak — a vendor type named in a service, a view model, a Domain record, or a future
    /// host file — which compiles fine in one assembly and is exactly the drift this phase must stop before the
    /// backends move. It is deliberately NOT a claim that today's tree is vendor-free (it is not; see §0).
    ///
    /// MUTATION that reddens it: <c>using AcerHelper.Infrastructure.Vendors.Acer;</c> added to
    /// <c>UI/AppController.cs</c>, or an <c>AcerDevice</c> reference in any file outside the three exemptions.
    /// </summary>
    [Fact]
    public void NoVendorTypeNameOutsideTheVendorPluginAndDeviceFactorySubtrees()
    {
        var offenders = new List<string>();

        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "UI" })
        {
            foreach (var path in SourcesIn(layer))
            {
                var rel = Relative(path);

                // The exemptions, each with the reason above. Keep them as prefixes so a whole subtree (e.g. a
                // future Infrastructure/Vendors/Acer2) stays covered.
                if (rel.StartsWith("Infrastructure/Vendors/", StringComparison.Ordinal)) continue;
                if (rel.StartsWith("Infrastructure/Plugins/", StringComparison.Ordinal)) continue;
                if (rel == "Infrastructure/Composition/DeviceFactory.cs") continue;

                var code = StripComments(File.ReadAllText(path));

                offenders.AddRange(VendorTypeIdentifiers
                    .Where(id => Regex.IsMatch(code, $@"\b{Regex.Escape(id)}\b"))
                    .Select(id => $"{rel}: {id}"));

                offenders.AddRange(VendorNamespaces
                    .Where(ns => code.Contains(ns, StringComparison.Ordinal))
                    .Select(ns => $"{rel}: {ns}"));
            }
        }

        Assert.True(offenders.Count == 0,
            "a vendor type name appeared in the host core outside Infrastructure/Vendors/ and "
            + "Infrastructure/Plugins/. The vendor backends move into plugins (§4.1, §6); the host must not "
            + "acquire NEW vendor knowledge in the meantime. A failure here is a design decision to argue, not a "
            + "line to restore:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// <c>DeviceFactory.cs</c> is the ONE documented Phase 0 exemption from the rule above, and this test is what
    /// stops the exemption from being a loophole: it asserts the file names EXACTLY the two legacy branches
    /// (<c>AcerDevice</c>, <c>DellDevice</c>) and their two namespaces — no third vendor — and that the plugin
    /// path is still absent.
    ///
    /// WHY: a new vendor added to the factory would otherwise pass <see cref="NoVendorTypeNameOutsideTheVendorPluginAndDeviceFactorySubtrees"/>
    /// (the whole file is skipped), silently widening the hole §4.1 says Phase 1 closes. The final line is the §6
    /// Phase 0 invariant "the adapters are not yet wired into <c>DeviceFactory</c>" — also pinned by
    /// <c>PluginAdapterTests.DeviceFactoryDoesNotReferencePluginVendorDevice</c>, cited rather than duplicated; it
    /// is kept here too because THIS test is the one that reasons about the exemption.
    ///
    /// MUTATION that reddens it: add <c>AsusDevice</c> (or any third vendor) to <c>DeviceFactory.Create</c>, or
    /// add the Phase 1 plugin branch prematurely.
    /// </summary>
    [Fact]
    public void DeviceFactorysVendorExemptionIsStillTheKnownPhase1Branches()
    {
        var code = StripComments(Source("Infrastructure/Composition/DeviceFactory.cs"));

        var found = VendorTypeIdentifiers
            .Where(id => Regex.IsMatch(code, $@"\b{Regex.Escape(id)}\b"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "AcerDevice", "DellDevice" }, found);
        Assert.Contains("AcerHelper.Infrastructure.Vendors.Acer", code, StringComparison.Ordinal);
        Assert.Contains("AcerHelper.Infrastructure.Vendors.Dell", code, StringComparison.Ordinal);

        // §6 Phase 0: the plugin path is built but NOT composed. The factory must not name the plugin device.
        Assert.DoesNotContain("PluginVendorDevice", code, StringComparison.Ordinal);
    }

    // ==========================================================================================================
    // GUARD 2 — every plugin exports the ABI entry points (source side; AOT cannot run here)
    // ==========================================================================================================

    /// <summary>The eight <c>ah_*</c> entry points (§3.2), taken from the shared ABI constants rather than typed
    /// a second time, so a rename in <c>VendorAbi</c> flows here and a plugin that follows the rename cannot drift
    /// from the contract.</summary>
    private static readonly string[] PluginAbiEntryPoints =
    [
        VendorAbi.AbiVersion, VendorAbi.PluginId, VendorAbi.Matches, VendorAbi.Create,
        VendorAbi.Invoke, VendorAbi.Free, VendorAbi.Dispose, VendorAbi.SetEventSink,
    ];

    /// <summary>Every plugin project directory under <c>plugins/</c> (§5.1), asserted to exist so a renamed or
    /// deleted tree cannot turn the rules below into an empty enumeration that asserts nothing.</summary>
    private static List<string> PluginProjectDirs()
    {
        var root = Path.Combine(Root(), "plugins");
        Assert.True(Directory.Exists(root), "the tree no longer has plugins/ — this guard is looking at nothing");
        var dirs = Directory.EnumerateDirectories(root).ToList();
        Assert.NotEmpty(dirs);
        return dirs;
    }

    /// <summary>The plugin's own C# sources (build intermediates excluded).</summary>
    private static List<string> PluginSources(string dir)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
            .ToList();
    }

    /// <summary>
    /// THE EXPORT CONTRACT, GENERALISED. Every plugin under <c>plugins/*/</c> declares all eight
    /// <c>EntryPoint = "ah_…"</c> names, and declares NOTHING the ABI does not define. Native AOT only exports
    /// <c>[UnmanagedCallersOnly]</c> methods in the published assembly (§2.1), and the host resolves each name by
    /// string with <c>NativeLibrary.GetExport</c> — so a missing or renamed entry point is a load-time miss no
    /// compiler catches. AOT cannot be built or run in this suite, so the contract is read from the source.
    ///
    /// GENERAL, NOT PROOF-SPECIFIC: <c>ProofPluginSourceTests.ProofPluginExportsAllEightAbiEntryPoints</c> covers
    /// the proof plugin with the same technique; this test is the directory-driven version that a future
    /// <c>acer-nitro</c>/<c>dell-xps</c> plugin is covered by automatically the day its directory appears.
    ///
    /// A FUTURE EMPTY DIRECTORY (a scaffolded folder with no source yet) is skipped gracefully, as the task
    /// requires — there is no source to read, so there is nothing to assert.
    ///
    /// MUTATION that reddens it: rename one <c>EntryPoint</c> in <c>plugins/AcerHelper.Vendor.Proof/ProofPlugin.cs</c>
    /// (e.g. <c>ah_matches</c> → <c>ah_match</c>), or add an <c>EntryPoint = "ah_extra"</c>.
    /// </summary>
    [Fact]
    public void EveryPluginExportsEveryAbiEntryPointAndNothingElse()
    {
        var offenders = new List<string>();

        foreach (var dir in PluginProjectDirs())
        {
            var sources = PluginSources(dir);
            if (sources.Count == 0) continue; // a scaffolded-but-empty plugin dir: nothing to check

            var text = string.Join("\n", sources.Select(File.ReadAllText));

            foreach (var name in PluginAbiEntryPoints)
                if (!text.Contains($"EntryPoint = \"{name}\"", StringComparison.Ordinal))
                    offenders.Add($"{Relative(dir)}: missing EntryPoint = \"{name}\"");

            // And nothing EXTRA: a stray EntryPoint is dead surface, or a sign the ABI set drifted.
            var declared = Regex.Matches(text, @"EntryPoint\s*=\s*""([a-zA-Z0-9_]+)""")
                .Select(m => m.Groups[1].Value)
                .Distinct();
            foreach (var declaredName in declared)
                if (!PluginAbiEntryPoints.Contains(declaredName))
                    offenders.Add($"{Relative(dir)}: undeclared entry point \"{declaredName}\"");
        }

        Assert.True(offenders.Count == 0,
            "every plugin must export exactly the eight ABI entry points of §3.2 — the host resolves them by "
            + "string and a typo is a load-time miss the compiler cannot see (§2.1, §6):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// EVERY PLUGIN PROJECT IS A NATIVE AOT SHARED LIBRARY THAT SOURCE-INCLUDES THE ABI (§2.1, §3.1, §4.4).
    /// <c>PublishAot</c> is what makes the classlib native; <c>NativeLib=Shared</c> is the shared-library artefact
    /// (static libraries are unsupported); <c>IsAotCompatible</c> turns on the AOT/trim analyzers that surface
    /// warnings the host build would not show for the moved code (§7.1 risk 5). The shared ABI is compiled INTO
    /// the plugin by <c>&lt;Compile Include=…&gt;</c> — a <c>ProjectReference</c> to the host would both fail to
    /// export (§2.1) and drag the whole host into the plugin.
    ///
    /// Same overlap note as above: this is the general form of
    /// <c>ProofPluginSourceTests.PluginProjectDeclaresNativeAotSharedLibrarySettings</c>/
    /// <c>…SourceIncludesTheAbiFilesAndDoesNotReferenceTheHost</c>.
    ///
    /// MUTATION that reddens it: delete <c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c> or an ABI
    /// <c>&lt;Compile Include&gt;</c> from a plugin csproj, or add a <c>&lt;ProjectReference&gt;</c>.
    /// </summary>
    [Fact]
    public void EveryPluginProjectDeclaresNativeAotSharedAndSourceIncludesTheAbi()
    {
        string[] abiFiles = ["VendorAbi.cs", "AbiStatus.cs", "Capability.cs", "Operation.cs"];
        var offenders = new List<string>();

        foreach (var dir in PluginProjectDirs())
        {
            if (PluginSources(dir).Count == 0) continue; // no source => not a real project yet; skip

            foreach (var csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(csproj);
                var rel = Relative(csproj);

                if (!text.Contains("<PublishAot>true</PublishAot>", StringComparison.Ordinal))
                    offenders.Add($"{rel}: missing <PublishAot>true</PublishAot> (not a native image, §2.1)");
                if (!text.Contains("<NativeLib>Shared</NativeLib>", StringComparison.Ordinal))
                    offenders.Add($"{rel}: missing <NativeLib>Shared</NativeLib> (not a shared library, §2.1)");
                if (!text.Contains("<IsAotCompatible>true</IsAotCompatible>", StringComparison.Ordinal))
                    offenders.Add($"{rel}: missing <IsAotCompatible>true</IsAotCompatible> (AOT analyzers off, §7.1)");

                foreach (var file in abiFiles)
                    if (!text.Contains($"Compile Include=\"..\\..\\Infrastructure\\Plugins\\Abi\\{file}\"",
                                       StringComparison.Ordinal))
                        offenders.Add($"{rel}: does not source-include Abi/{file} (§3.1/§4.4)");

                if (text.Contains("<ProjectReference", StringComparison.Ordinal))
                    offenders.Add($"{rel}: has a <ProjectReference> — the ABI must be source-included, and the host "
                                  + "must not be dragged into the plugin (§2.1, §4.4)");
            }
        }

        Assert.True(offenders.Count == 0,
            "a plugin project lost its Native AOT / source-include shape (§2.1, §3.1, §4.4, §5.1):\n  "
            + string.Join("\n  ", offenders));
    }

    // ==========================================================================================================
    // GUARD 3 — the ABI round-trip
    // ==========================================================================================================

    /// <summary>The §3.4 manifest the fake plugin hands back, built once so both round-trip facts share it.</summary>
    private const string RoundTripManifestJson =
        """
        {
          "abi": "1.0",
          "pluginId": "proof",
          "vendor": "proof",
          "vendorName": "Proof Laptop",
          "capabilities": {
            "powerProfiles": {
              "all": [{"id":"1","displayName":"profile.balanced"}],
              "traits": [{"id":"1","kind":"Balanced"}]
            }
          }
        }
        """;

    private static VendorPluginLoader RoundTripLoader(FakeNativePluginBinding binding) =>
        new(["proof.dll"], _ => binding, PluginAbiRegistry.CreateDefault(),
            new MachineDescriptor(Manufacturer: "Proof", Product: "Proof Laptop", Board: "Proof",
                                  BoardProduct: "P1"),
            hostAppVersion: "0.37.0", confidenceThreshold: 50);

    /// <summary>
    /// THE FULL ABI ROUND-TRIP, ONE COMPOSITION (§6 Phase 0 verification; §3.2, §3.4, §3.8.4, §4.1-§4.3). This
    /// drives the REAL adapter stack — the shipped <see cref="PluginAbiRegistry.CreateDefault"/> (major 1,
    /// <c>AbiV1Adapter</c>) — over <see cref="FakeNativePluginBinding"/> through the complete path the pieces are
    /// individually tested for but never together:
    ///
    ///   load → version gate → <c>ah_matches</c> → <c>ah_create</c> (handshake) → decode the manifest through the
    ///   WINNING adapter → build a capability adapter over the session → <c>ah_invoke</c> → decode the response →
    ///   dispose (<c>ah_dispose</c>).
    ///
    /// WHY THE COMPOSITION AND NOT MORE UNIT TESTS: <c>PluginLoaderTests</c> proves selection and
    /// <c>PluginAdapterTests</c>/<c>PluginSessionTests</c> prove mapping/forwarding against a fake SESSION — but
    /// nothing proves that a loader-built session, its decoded manifest and a capability adapter actually compose
    /// end to end. A break in the seam (the session's handle not being the one passed to <c>ah_invoke</c>, the
    /// manifest not surviving the adapter decode into a usable port, dispose not reaching the binding) is exactly
    /// the class of defect the seam tests miss.
    ///
    /// <c>ah_free</c> has NO runtime hook here (it runs inside the unsafe <c>NativePluginBinding</c>, which needs a
    /// real AOT module), so it is pinned by <see cref="TheBindingFreesEveryPluginBufferExactlyOnce"/> instead;
    /// <c>ah_dispose</c> IS observable here through <see cref="FakeNativePluginBinding.DisposeCount"/> and is
    /// asserted exactly once.
    ///
    /// MUTATION that reddens it: have <c>VendorPluginLoader</c> pass handle 0 to <c>PluginSession</c>, or stop
    /// decoding the manifest through the winning adapter's <c>DecodeManifest</c>.
    /// </summary>
    [Fact]
    public void FullAdapterRoundTripThroughTheDefaultRegistry()
    {
        var binding = new FakeNativePluginBinding
        {
            PluginIdValue = "proof",
            MatchResult = new PluginMatchResult(Matched: true, Confidence: 80, Reason: "test"),
            CreateManifestUtf8 = Encoding.UTF8.GetBytes(RoundTripManifestJson),
            CreateHandle = 0x7777,
        };
        binding.SetInvokeResult(Capability.Power, Operation.Power.Current, AbiStatus.Ok, """{"id":"1"}""");

        var result = RoundTripLoader(binding).Load();

        // load → match → create → decode: the loader returned a live session carrying the decoded manifest.
        Assert.True(result.IsLoaded);
        Assert.Equal(PluginLoadReason.Loaded, result.Reason);
        Assert.Equal("proof", result.PluginId);
        Assert.Equal(80, result.Confidence);

        var session = result.Session!;
        Assert.Equal("proof", session.Manifest.PluginId);
        Assert.Equal("Proof Laptop", session.Manifest.VendorName);

        // The descriptor fed to ah_matches and the handshake body fed to ah_create are the §3.2/§3.4 shapes; the
        // create body also carries the host's GENERATED API version (§3.8.4 gate 3), so the host and plugin cannot
        // drift on the encoding.
        var desc = Assert.Single(binding.MatchesDescriptors);
        Assert.Contains("\"manufacturer\":\"Proof\"", desc, StringComparison.Ordinal);
        var create = Assert.Single(binding.CreateDescriptors);
        Assert.Contains($"\"major\":{PluginApi.Major}", create, StringComparison.Ordinal);
        Assert.Contains($"\"minor\":{PluginApi.Minor}", create, StringComparison.Ordinal);

        // build a capability adapter over the REAL session (identity V1 adapter) and invoke.
        var port = new PluginPowerProfiles(session, session.Manifest.Capabilities!.PowerProfiles!);
        Assert.Equal(new[] { "1" }, port.All.Select(p => p.Id));
        Assert.Equal(ProfileKind.Balanced, port.Traits(port.All[0]).Kind);

        var current = port.Current();
        Assert.Equal("1", current!.Id);
        Assert.Equal("profile.balanced", current.DisplayName);

        // invoke → decode: it crossed with the handle ah_create returned, on the (capability, op) the port chose.
        var call = Assert.Single(binding.InvokeCalls);
        Assert.Equal(0x7777UL, call.Handle);
        Assert.Equal(Capability.Power, call.Capability);
        Assert.Equal(Operation.Power.Current, call.Op);
        Assert.Equal("{}", call.RequestJson);

        // dispose → ah_dispose exactly once, even across a double dispose (§4.3).
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, binding.DisposeCount);
    }

    /// <summary>
    /// THE VERSION GATE THROUGH THE SAME COMPOSITION (§3.8.4 gate 2). A plugin whose <c>ah_abi_version</c> major
    /// is not in <c>SupportedMajors</c> is refused with <see cref="PluginLoadReason.UnsupportedAbi"/> — the
    /// "matched but incompatible" status-line case (§4.1.1) — and NOTHING past the version gate runs: neither
    /// <c>ah_matches</c> nor <c>ah_create</c> is called, so no hardware is touched for a plugin this host cannot
    /// adapt.
    ///
    /// OVERLAP NOTE: <c>PluginLoaderTests.UnsupportedMajorIsDiscardedWithUnsupportedAbi</c> already covers the gate
    /// as a unit test. This fact does not substitute for it; it exists so Guard 3's round-trip suite carries the
    /// "right reason" assertion beside the happy path, on the same loader construction.
    ///
    /// MUTATION that reddens it: make <c>AbiCompatibility.Decide</c> accept any major, so the refusal is no longer
    /// <c>UnsupportedAbi</c>.
    /// </summary>
    [Fact]
    public void UnsupportedMajorIsRefusedBeforeMatchesOrCreate()
    {
        var binding = new FakeNativePluginBinding
        {
            AbiVersion = (7u << 16) | 0, // major 7: CreateDefault supports only major 1
            MatchResult = new PluginMatchResult(Matched: true, Confidence: 100, Reason: "never reached"),
            CreateManifestUtf8 = Encoding.UTF8.GetBytes(RoundTripManifestJson),
        };

        var result = RoundTripLoader(binding).Load();

        Assert.False(result.IsLoaded);
        Assert.Null(result.Session);
        Assert.Equal(PluginLoadReason.UnsupportedAbi, result.Reason);
        // The version gate precedes the DMI gate and the handshake.
        Assert.Empty(binding.MatchesDescriptors);
        Assert.Empty(binding.CreateDescriptors);
    }

    /// <summary>
    /// "A TEST THAT <c>ah_free</c>/<c>ah_dispose</c> ARE CALLED EXACTLY ONCE" (§6 Phase 0 verification), from the
    /// only side this suite can reach. <c>ah_dispose</c> is asserted at runtime in
    /// <see cref="FullAdapterRoundTripThroughTheDefaultRegistry"/>; <c>ah_free</c> runs inside the unsafe
    /// <c>NativePluginBinding</c>, which requires a real Native AOT module and cannot execute here (§2.1). So this
    /// pins the CALL SITES as text: exactly two <c>_exports.Free(...)</c> calls — the create manifest and the
    /// invoke response, the only two plugin-allocated buffers the host must release (§3.2) — each inside a
    /// <c>finally</c> so a decode failure cannot leak, and exactly one <c>_exports.Dispose(...)</c>.
    ///
    /// WHY IT IS WORTH A GUARD: a forgotten or duplicated <c>ah_free</c> is a native leak or a double-free that
    /// no managed test can observe, and a third free site would mean a buffer path this reasoning does not cover.
    ///
    /// MUTATION that reddens it: drop the response <c>finally { _exports.Free(responsePtr); }</c>, or add a second
    /// <c>_exports.Dispose</c>.
    /// </summary>
    [Fact]
    public void TheBindingFreesEveryPluginBufferExactlyOnce()
    {
        var source = Source("Infrastructure/Plugins/NativePluginBinding.cs");

        // The two plugin-allocated buffers (§3.2): the ah_create manifest and the ah_invoke response. Each is
        // released exactly once, inside a finally so it runs even when the body fails to decode.
        var freesInFinally = Regex.Matches(source, @"finally\s*\{\s*_exports\.Free\(").Count;
        Assert.Equal(2, freesInFinally);

        var freeCalls = Regex.Matches(source, @"_exports\.Free\(").Count;
        Assert.Equal(2, freeCalls);

        // ah_dispose runs once, guarded by the binding's _disposed flag (§4.3).
        var disposeCalls = Regex.Matches(source, @"_exports\.Dispose\(").Count;
        Assert.Equal(1, disposeCalls);
    }
}
