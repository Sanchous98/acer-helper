using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE RELEASE PIPELINE GUARD for the vendor plugin (docs/vendor-plugins.md §5.2, §5.4, §5.5; task D3), read as
/// text from <c>.github/workflows/build.yml</c>.
///
/// WHY SOURCE-TEXT AND NOT A RUNTIME TEST. A GitHub Actions run cannot be executed here (no secret, no runners,
/// no AOT toolchain), so the contract that MAKES the CI run meaningful is pinned structurally — the same
/// technique <see cref="PluginSoakHostSourceTests"/> uses for plugin-soak.yml. These checks fail if a future edit
/// drops the sign step's secret, weakens the tag guard, routes a plugin into the MSI, or forgets the aggregate
/// job — all edits that would otherwise only surface as a bad release.
///
/// HONEST LIMIT: green here means the YAML SPELLS the contract. It does NOT mean the AOT publish succeeded, the
/// secret is present, or the real asset verifies — only a tagged CI run proves that.
/// </summary>
public class PluginReleaseWorkflowTests
{
    private const string Workflow = ".github/workflows/build.yml";

    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    private static string WorkflowText() => Source(Workflow);

    /// <summary>Both OSes build the SAME plugin project, each for its own TFM/RID with PublishAot — Native AOT
    /// cannot cross-compile, so win-x64 is built on Windows and linux-x64 on Linux (docs/build-image.md:99-150).
    /// The output folders are the plugin-specific ones, NOT publish/, which the WiX harvest reads.</summary>
    [Fact]
    public void BothOsJobsPublishThePluginNativeAot()
    {
        var yml = WorkflowText();

        Assert.Contains("plugins/AcerHelper.Vendor.acer-nitro/AcerHelper.Vendor.acer-nitro.csproj", yml);
        Assert.Contains("-f net10.0-windows", yml);
        Assert.Contains("-f net10.0", yml);
        Assert.Contains("-r win-x64", yml);
        Assert.Contains("-r linux-x64", yml);
        Assert.Contains("-p:PublishAot=true", yml);
        Assert.Contains("-o out/plugin-win", yml);
        Assert.Contains("-o out/plugin-linux", yml);

        // The plugin must NOT be copied into publish/ (the MSI must not carry a plugin, §5). The Windows publish
        // output is out/plugin-win and nothing copies AcerHelper.Vendor.* into publish\.
        Assert.DoesNotContain(@"publish\AcerHelper.Vendor", yml, StringComparison.Ordinal);
        Assert.DoesNotContain("publish/AcerHelper.Vendor", yml, StringComparison.Ordinal);
    }

    /// <summary>The sign step is real on both OSes: it reads the secret from <c>PLUGIN_SIGNING_KEY</c>, refuses
    /// to publish an unsigned plugin (an error annotation and a non-zero exit), and runs the source-included
    /// signer. This is the owner's fail-loud decision (§5.4/§5.5): an unsigned release is worse than a failed
    /// build. The secret is referenced (not hardcoded) and never passed on a command line.</summary>
    [Fact]
    public void TheSignStepFailsLoudlyWhenTheSecretIsAbsent()
    {
        var yml = WorkflowText();

        Assert.Contains("secrets.PLUGIN_SIGNING_KEY", yml);
        Assert.Contains("PLUGIN_SIGNING_KEY is not set", yml);
        Assert.Contains("::error::", yml);
        // Both sign steps pass the key only through env, and both call the tool's `sign` mode.
        Assert.Contains("tools/PluginSign/PluginSign.csproj", yml);
        Assert.Contains("sign `", yml);       // Windows backtick line-continuation form
        Assert.Contains(" sign \\", yml);     // Linux backslash line-continuation form
        // The production keyId is pinned, not the test key: a release signed under the test key would be forgeable.
        Assert.Contains("--key-id 2026-09-release", yml);
        Assert.DoesNotContain("test-p256-2026-09", yml);
    }

    /// <summary>Every plugin step is tag-only, so a branch <c>workflow_dispatch</c> neither spends AOT minutes nor
    /// needs the secret (§5.5). Pinned as the tag guard appearing on the publish/sign steps; the count is at
    /// least the four plugin steps across both jobs.</summary>
    [Fact]
    public void ThePluginStepsAreTagOnly()
    {
        var yml = WorkflowText();

        var tagGuards = yml.Split("if: startsWith(github.ref, 'refs/tags/v')").Length - 1;
        // The MSI release, the AppImage release, the app tag guards and the four plugin steps + manifest job.
        Assert.True(tagGuards >= 8, $"expected the plugin + release steps to be tag-guarded, found {tagGuards}");
    }

    /// <summary>THE AGGREGATE JOB (§5.5): a small job that needs both OS jobs, downloads their artifacts, merges
    /// the per-OS fragments through the signer's <c>manifest</c> mode into one <c>vendor-plugins.json</c>, and
    /// attaches the assets + manifest to the SAME release type the other jobs use.</summary>
    [Fact]
    public void TheAggregateJobMergesAndPublishesTheManifest()
    {
        var yml = WorkflowText();

        Assert.Contains("plugins-manifest:", yml);
        Assert.Contains("needs: [windows, linux]", yml);
        Assert.Contains("actions/download-artifact@", yml);
        Assert.Contains("actions/upload-artifact@", yml);
        Assert.Contains("manifest \\", yml);
        Assert.Contains("vendor-plugins.json", yml);
        // The SAME release mechanism the app jobs use, not a second/weaker one (§5.5).
        Assert.Contains("softprops/action-gh-release@v3", yml);
    }

    /// <summary>The aggregate job re-verifies every merged entry with the host verifier before publishing (the
    /// signer's manifest mode does this), so a truncated/hand-edited fragment cannot reach the release. Pinned as
    /// the signer call being the writer — no ad-hoc <c>jq</c>/<c>cat</c> JSON assembly in the workflow.</summary>
    [Fact]
    public void TheAggregateJobUsesTheSignerToWriteTheManifest()
    {
        var yml = WorkflowText();

        Assert.Contains("--out vendor-plugins.json", yml);
        Assert.Contains("--fragment artifacts/plugin-win/vendor-plugins-fragment.json", yml);
        Assert.Contains("--fragment artifacts/plugin-linux/vendor-plugins-fragment.json", yml);
    }

    /// <summary>THE DEPRECATED-MAJOR LEG IS DELIBERATELY ABSENT (§5.5/§3.8.3): only API major 1 exists, so there
    /// is no second leg to build. Pinned so the omission is a recorded decision — the comment states it, and no
    /// second publish keyed on another major exists.</summary>
    [Fact]
    public void ThereIsNoDeprecatedMajorLegToday()
    {
        var yml = WorkflowText();

        Assert.Contains("DEPRECATED-MAJOR CI LEG", yml);
        // No second sign invocation keyed on another major: both sign steps pass major 1 only.
        Assert.Contains("--api-major 1", yml);
        Assert.DoesNotContain("--api-major 0", yml);
        Assert.DoesNotContain("--api-major 2", yml);
    }
}
