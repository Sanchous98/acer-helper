using System.Runtime.CompilerServices;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests;

/// <summary>
/// The Phase 0 two-AOT-runtime smoke+soak HOST and ITS WORKFLOW, read as text
/// (docs/vendor-plugins.md §2.4, §6 Phase 0 verification).
///
/// WHY SOURCE-TEXT AND NOT A RUNTIME TEST. The measurement this task exists for needs Native AOT on both sides
/// and a real operating system; it cannot run in this suite (the SDK here builds JIT, there is no MSVC/clang,
/// and the app requires administrator). What a unit test CAN hold is that the pieces which make the CI run
/// meaningful are present and correctly wired: the host source-includes the real ABI contract, asserts all eight
/// entry points, and has the non-zero-on-failure exit path; and the workflow publishes both artefacts AOT and
/// runs the host. The same technique — read the file, pin the contract — that
/// <see cref="ProofPluginSourceTests"/> uses, for the same reason: the alternative is a claim nothing checks.
///
/// HONEST LIMIT, stated plainly: a green guard here means the sources SPELL the contract; it does NOT mean the
/// AOT publish succeeded or the soak was clean. That is proven only by dispatching .github/workflows/plugin-soak.yml.
/// </summary>
public class PluginSoakHostSourceTests
{
    private const string HostCsproj = "tests/PluginSoakHost/PluginSoakHost.csproj";
    private const string HostSource = "tests/PluginSoakHost/Program.cs";
    private const string Workflow = ".github/workflows/plugin-soak.yml";

    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The eight <c>ah_*</c> entry points (§3.2), taken from the shared ABI constants rather than typed
    /// a second time, so this guard fails if the host and the contract ever separate.</summary>
    private static readonly string[] AbiEntryPoints =
    [
        VendorAbi.AbiVersion, VendorAbi.PluginId, VendorAbi.Matches, VendorAbi.Create,
        VendorAbi.Invoke, VendorAbi.Free, VendorAbi.Dispose, VendorAbi.SetEventSink,
    ];

    /// <summary>THE SMOKE ASSERTS EVERY EXPORT (§6 Phase 0 verification: "a smoke test that loads the built
    /// proof plugin and asserts every export"). The host resolves each name through the shared constants, so the
    /// eight <c>VendorAbi.*</c> names must appear in its source — a name the host forgot to resolve is a load
    /// the smoke would not cover.</summary>
    [Fact]
    public void HostResolvesAllEightAbiEntryPoints()
    {
        var source = Source(HostSource);

        // The host writes its export table as VendorAbi.<Name>, so each constant's PascalCase member must be
        // referenced — a name the host forgot to resolve is a load the smoke would not cover.
        foreach (var member in new[]
                 {
                     "VendorAbi.AbiVersion", "VendorAbi.PluginId", "VendorAbi.Matches", "VendorAbi.Create",
                     "VendorAbi.Invoke", "VendorAbi.Free", "VendorAbi.Dispose", "VendorAbi.SetEventSink",
                 })
        {
            Assert.Contains(member, source, StringComparison.Ordinal);
        }

        // And the eight literal ah_* strings must live in the shared ABI contract the host source-includes: the
        // test brief's "mentions all eight ah_ strings", made robust by reading them from VendorAbi.cs itself.
        var abiContract = Source("Infrastructure/Plugins/Abi/VendorAbi.cs");
        foreach (var name in AbiEntryPoints)
            Assert.Contains(name, abiContract, StringComparison.Ordinal);
    }

    /// <summary>THE HOST IS DEPENDENCY-FREE (§2.4): the whole point is that the ONLY AOT runtime besides the
    /// plugin's is the host's own. A reference to Avalonia, or to the app's Domain/UI, would drag a large managed
    /// surface (and Avalonia's native stack) into the process and make the two-runtime measurement a different,
    /// muddier experiment. Pinned as: no ProjectReference in the csproj, and no Avalonia/Domain/UI namespace in
    /// the source.</summary>
    [Fact]
    public void HostHasNoAppOrAvaloniaDependency()
    {
        var csproj = Source(HostCsproj);
        Assert.DoesNotContain("<ProjectReference", csproj);
        Assert.DoesNotContain("<PackageReference", csproj);

        var source = Source(HostSource);
        // Match on the USING/namespace forms, not the words: the file's own header COMMENT explains why Avalonia
        // and Domain/UI are absent, and a bare-word check would read that explanation as a dependency.
        Assert.DoesNotContain("using Avalonia", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AcerHelper.Domain", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AcerHelper.UI", source, StringComparison.Ordinal);
        // The ABI contract is the only AcerHelper namespace the host may touch.
        Assert.Contains("AcerHelper.Infrastructure.Plugins.Abi", source);
    }

    /// <summary>The host SOURCE-INCLUDES the four ABI files rather than referencing the host assembly (§3.1): that
    /// is what keeps it dependency-free while still asserting against the real contract. Mirrors the plugin's
    /// own source-include (ProofPluginSourceTests).</summary>
    [Fact]
    public void HostProjectSourceIncludesTheAbiFiles()
    {
        var csproj = Source(HostCsproj);

        foreach (var file in new[] { "VendorAbi.cs", "AbiStatus.cs", "Capability.cs", "Operation.cs" })
            Assert.Contains($"Compile Include=\"..\\..\\Infrastructure\\Plugins\\Abi\\{file}\"", csproj);
    }

    /// <summary>A FAILED SMOKE MUST FAIL CI. The workflow's whole value is that a load that does not work, or a
    /// soak that crashes, is a RED job — so the host must exit non-zero on failure. Pinned as the two exit paths
    /// present in the source: a non-zero return from the failure catch, and the throw-based assertion helper the
    /// checks funnel through.</summary>
    [Fact]
    public void HostExitsNonZeroOnFailure()
    {
        var source = Source(HostSource);

        // The top-level catch converts a failure into a non-zero exit code.
        Assert.Contains("catch (SmokeFailure", source);
        Assert.Contains("catch (Exception", source);
        // The assertion helper throws; every Require failing reaches the catch above.
        Assert.Contains("private static void Require(", source);
        Assert.Contains("throw new SmokeFailure", source);
        // And at least one literal non-zero return, so the "return 0 on success" is paired with a real failure exit.
        Assert.Contains("return 1;", source);
        Assert.Contains("return 2;", source);
    }

    /// <summary>THE WORKFLOW PUBLISHES BOTH SIDES NATIVE AOT AND RUNS THE HOST (§2.4/§6). Pinned as the four
    /// load-bearing facts: the plugin publish command, the host publish command, <c>PublishAot=true</c> on both,
    /// and a step that actually RUNS the built host against the built plugin. A workflow that only published
    /// would prove nothing — the run is the measurement.</summary>
    [Fact]
    public void WorkflowPublishesBothAotAndRunsTheHost()
    {
        var workflow = Source(Workflow);

        Assert.Contains("plugins/AcerHelper.Vendor.Proof/AcerHelper.Vendor.Proof.csproj", workflow);
        Assert.Contains("tests/PluginSoakHost/PluginSoakHost.csproj", workflow);
        Assert.Contains("PublishAot=true", workflow);

        // The host is actually executed, with the soak window passed in. Both OSes run it (win .exe / linux
        // bare name); assert the executable name and the flag are present at all.
        Assert.Contains("PluginSoakHost", workflow);
        Assert.Contains("--soak-minutes", workflow);
    }

    /// <summary>THE WORKFLOW YAML IS STRUCTURALLY SOUND. A full YAML parse is preferred, but the parse must not be
    /// the only thing that fails when the schema is wrong; these substring checks pin the shape the trigger and
    /// jobs must have. Kept robust (substring, not formatting) so re-indentation of the file never reddens a
    /// guard whose subject is the contract, not the layout.</summary>
    [Fact]
    public void WorkflowDeclaresDispatchAndScheduleAndBothOsJobs()
    {
        var workflow = Source(Workflow);

        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("schedule:", workflow);
        Assert.Contains("soak_minutes", workflow);
        Assert.Contains("runs-on: windows-2022", workflow);
        Assert.Contains("runs-on: ubuntu-latest", workflow);
        // A timeout is what turns a soak HANG into a failed job instead of an indefinitely hung runner.
        Assert.Contains("timeout-minutes:", workflow);
        // Not on push: the deliberate absence is part of the design, so pin it.
        Assert.DoesNotContain("push:", workflow);
    }

    /// <summary>The workflow's plugin artefact guard must fail loudly on a missing/empty artefact (the build.yml
    /// precedent checks by name). Pinned as a presence check with a non-zero exit, so a job that published
    /// nothing cannot proceed to "run the host against nothing" and look green.</summary>
    [Fact]
    public void WorkflowGuardsThePluginArtefactByName()
    {
        var workflow = Source(Workflow);

        Assert.Contains("AcerHelper.Vendor.Proof.dll", workflow);
        Assert.Contains("AcerHelper.Vendor.Proof.so", workflow);
        // The failure path of the guard: an error annotation plus a non-zero exit.
        Assert.Contains("::error::", workflow);
        Assert.Contains("exit 1", workflow);
    }
}
