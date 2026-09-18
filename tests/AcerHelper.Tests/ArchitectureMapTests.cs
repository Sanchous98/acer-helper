using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AcerHelper.Tests;

/// <summary>
/// The layer map, asserted rather than described.
///
/// README states the layering — "one project, organised by layer; namespaces match the directories" — and
/// <c>LaptopService.Settings</c> documents the honest limit of that claim in its own words: this is ONE
/// assembly, so <c>internal</c> is "a SIGNPOST, not a fence", and the only boundary the COMPILER enforces is the
/// OS axis (the <c>*.Windows.cs</c>/<c>*.Linux.cs</c> suffix, selected by <c>&lt;Compile Remove&gt;</c>). This
/// file is the rest of the fence: cheap to run, and it catches the failure a single-assembly build cannot — a
/// <c>using</c> that points the wrong way, which today is a comment-level rule kept alive by review.
///
/// WHAT IT DOES NOT CLAIM. It does not claim the UI never reaches Infrastructure — it does, by design
/// (<c>AppController</c> imports <c>AcerHelper.Infrastructure.Diagnostics</c> for the gate stats). It does not
/// claim <c>Domain</c> is dependency-free of the shared kernel below it: <c>Localization</c> is ALLOWED by the
/// rule below (it sits below Domain as the shared kernel), and that permission is now unexercised rather than
/// relied on — no file under <c>Domain/</c> imports it since the settings container moved to Infrastructure,
/// which is what the type that stored <c>AppLanguage</c> was the reason for. The permission stays because
/// narrowing it would be a rule CHANGE made in the commit that happened to empty it, and a rule that is tighter
/// than the design is the next author's problem rather than this one's. And it is a rule
/// about SOURCE LAYOUT, not about behaviour: nothing here proves a type is where it belongs conceptually, only
/// that the folder, the namespace and the edges between them agree.
///
/// A FAILURE HERE IS A DECISION, NOT A BUG. If a new dependency is genuinely wanted, the rule has to change
/// first — and that is the point. Each test states its rule and its exceptions in its own docstring so the next
/// author does not have to work out whether the test or their change is the mistake.
/// </summary>
public class ArchitectureMapTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The folders that are layers in the sense README means. <c>driver/</c> and <c>packaging/</c> hold no
    /// C#, and the test project has a root namespace of its own.</summary>
    private static readonly string[] Layers =
        ["Domain", "Application", "Infrastructure", "UI", "Bootstrap", "Localization"];

    private static List<string> SourcesIn(string layer)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(Root(), layer), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
            .ToList();
    }

    private static string Relative(string path) => Path.GetRelativePath(Root(), path).Replace('\\', '/');

    /// <summary>The <c>namespace X;</c> a file declares. Matches the file-scoped form the tree uses.</summary>
    private static string NamespaceOf(string text)
        => Regex.Match(text, @"^namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline).Groups[1].Value;

    /// <summary>The namespaces a file imports. <c>using X = Y;</c> aliases deliberately do not match: the target
    /// is what would violate a rule, and an alias hides it behind an invented name.</summary>
    private static IEnumerable<string> UsingsOf(string text)
        => Regex.Matches(text, @"^using\s+(?:static\s+)?([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value);

    /// <summary>The map is only meaningful while these folders ARE the layers: a renamed or deleted folder would
    /// otherwise turn every rule below into an empty enumeration that asserts nothing and passes.</summary>
    [Fact]
    public void EveryLayerFolderExistsAndHoldsSources()
    {
        var missing = Layers
            .Where(l => !Directory.Exists(Path.Combine(Root(), l)) || SourcesIn(l).Count == 0)
            .ToArray();

        Assert.True(missing.Length == 0,
            "layer folder(s) missing or empty — every rule in this file would pass vacuously: "
            + string.Join(", ", missing));
    }

    /// <summary>README's claim, checked per layer so a failure names the layer it is about. This is the rule the
    /// others lean on: a file whose namespace does not name its folder is invisible to <c>using</c>-based rules,
    /// and it breaks the <c>AcerHelper + path</c> convention the whole tree is read by.</summary>
    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Infrastructure")]
    [InlineData("UI")]
    [InlineData("Bootstrap")]
    [InlineData("Localization")]
    public void EveryNamespaceNamesItsFolder(string layer)
    {
        var offenders = new List<string>();

        foreach (var path in SourcesIn(layer))
        {
            var expected = "AcerHelper." + Path.GetRelativePath(Root(), Path.GetDirectoryName(path)!)
                                              .Replace(Path.DirectorySeparatorChar, '.');
            var declared = NamespaceOf(File.ReadAllText(path));
            if (declared != expected) offenders.Add($"{Relative(path)}: declares '{declared}'");
        }

        Assert.True(offenders.Count == 0,
            $"files under {layer}/ whose namespace is not their folder (expected 'AcerHelper.{layer}…'):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The OS split is a BACKEND concern, and it is the one boundary the build already enforces — the
    /// suffix decides which files exist in which TFM (<c>AcerHelper.csproj</c> <c>&lt;Compile Remove&gt;</c>), so a
    /// platform file above <c>Infrastructure/</c> would be compiled away in one target and not the other, which is
    /// exactly the kind of failure nobody sees until a release. Today: 45 files, all under Infrastructure.</summary>
    [Fact]
    public void EveryOsSplitFileLivesUnderInfrastructure()
    {
        var offenders = Layers.Where(l => l != "Infrastructure")
                              .SelectMany(SourcesIn)
                              .Where(p => p.EndsWith(".Windows.cs") || p.EndsWith(".Linux.cs"))
                              .Select(Relative)
                              .ToArray();

        Assert.True(offenders.Length == 0,
            "these platform files are selected by file-name suffix and must live under Infrastructure/:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Domain points DOWN at nothing. Localization is the single permitted exception, and the
    /// permission is now unexercised: the type that made it necessary — the settings container, which stored
    /// <c>AppLanguage</c> — is Infrastructure's, so no file under <c>Domain/</c> imports anything at all today.
    /// The permission is deliberately NOT narrowed here: emptying it was a consequence of that move, not a
    /// decision about the rule, and a rule tightened in passing is a rule nobody reviewed. Everything else —
    /// Infrastructure, Application, UI — is forbidden, because a Domain type that reaches one of them stops
    /// being the neutral model the backends are written against.
    ///
    /// MUTATION-VERIFIED, so neither half is vacuous: <c>using AcerHelper.Infrastructure.Composition;</c> added
    /// to any file under <c>Domain/</c> reddens it (the container is nameable from Domain again, which is what
    /// "points up" means), and the Localization permission is what an <c>AppLanguage</c> reference would need if
    /// one were ever put back.</summary>
    [Fact]
    public void DomainPointsAtNothingButItselfAndLocalization()
    {
        string[] allowed = ["AcerHelper.Domain", "AcerHelper.Localization"];

        var offenders = SourcesIn("Domain")
            .SelectMany(p => UsingsOf(File.ReadAllText(p)).Select(u => (File: Relative(p), Using: u)))
            .Where(x => x.Using.StartsWith("AcerHelper.", StringComparison.Ordinal) && !allowed.Contains(x.Using))
            .Select(x => $"{x.File}: using {x.Using};")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Domain must not depend on a layer above it:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Application points DOWN at Domain, not UP at Infrastructure. The rule needs stating because
    /// the compiler does not state it: this is one assembly, so an Infrastructure type is perfectly nameable
    /// from Application and the build stays green either way.
    ///
    /// The case that made the rule concrete is the LampArray surface. <c>LaptopService</c> publishes a virtual
    /// lighting device whose implementation is Infrastructure's
    /// (<c>Infrastructure/Lighting/LampArrayBridge.cs</c>), and the direct route — let the service name the
    /// bridge and the transport under it — would have made Application depend on the layer it orchestrates.
    /// It names <c>IDynamicLighting</c> and <c>IDynamicLightingFactory</c>
    /// (<c>Application/DynamicLighting.cs</c>) instead, and composition supplies the implementation.
    ///
    /// WHAT IT FORCED, and this is the part worth reading before moving anything else. When the service layer
    /// and the persisted container moved to Infrastructure, this rule is what decided the SHAPE of that move:
    /// the re-apply operation could not stay whole, because its result was built out of the container's types
    /// and Application may not name them, so only the PLAN stayed (<c>Application/ReapplyPlan.cs</c>, stated in
    /// Domain vocabulary) and the executor went with the service. What came back afterwards is the operation
    /// ITSELF, and only because the result changed shape first: the outcome now speaks Domain vocabulary
    /// (<c>FanAxisState</c>, <c>GpuAxisState</c>) and the implementing layer translates at the accessor that
    /// already reads the preset. Application is TEN files today — the four this docstring used to name, plus the
    /// family of APPLIED EDITS that landed afterwards on the same terms (FanAxis, GpuOffsets, CpuPowerOverlay,
    /// Undervolt, DeclaredSetting, DynamicLightingSwitch, each one contract plus its use cases) — and the rule is
    /// the reason for every boundary in each of them, not an accident of the moves.
    ///
    /// WHAT IT STILL FORBIDS, so nobody mistakes the above for a relaxation: a use case that needs the STORED
    /// CONTAINER (the per-mode presets and the graph they live in) or a vendor port by name still cannot live
    /// here, and that is most of what the service does. The re-apply got in because it needs a schedule, the
    /// axes that schedule names, and a contract — nothing else. The applied edits got in the same way and not by
    /// an exception: an axis's state crosses as the Domain value it already had
    /// (<c>FanAxisState</c>, <c>GpuAxisState</c>), so the container is never named and the rule the use case
    /// states — what an edit must not clobber, what is remembered before it is written — is stated without it.
    /// What could NOT get in that way is measured in docs/device-and-application.md §8: the per-mode lighting
    /// (a live reference the UI edits in place) and the profile switch (a lock held across the port call).
    ///
    /// Today Application imports Domain and nothing else: Localization is still permitted for the same reason
    /// <see cref="DomainPointsAtNothingButItselfAndLocalization"/> permits it, but the <c>AppLanguage</c> the
    /// settings model stored went with the container, so nothing here uses it any more. Localization is
    /// deliberately NOT removed from the list below for that reason: it was allowed before for a real design
    /// reason, and dropping it here would be a rule change smuggled into a move. The UI may still reach
    /// Infrastructure (it does, by design; see the file's own docstring); this rule fences Application, which
    /// has no such need.
    ///
    /// MUTATION-VERIFIED, so the rule is not vacuous: <c>using AcerHelper.Infrastructure.Lighting;</c> added to
    /// <c>Application/DynamicLighting.cs</c> reddens this test and no other in the file.</summary>
    [Fact]
    public void ApplicationPointsAtDomainNotInfrastructure()
    {
        string[] allowed = ["AcerHelper.Application", "AcerHelper.Domain", "AcerHelper.Localization"];

        var offenders = SourcesIn("Application")
            .SelectMany(p => UsingsOf(File.ReadAllText(p)).Select(u => (File: Relative(p), Using: u)))
            .Where(x => x.Using.StartsWith("AcerHelper.", StringComparison.Ordinal) && !allowed.Contains(x.Using))
            .Select(x => $"{x.File}: using {x.Using};")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Application must not depend on Infrastructure (or on the UI):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>No toolkit below the UI. <c>Bootstrap</c> is exempt because it is the composition root and
    /// legitimately starts the Avalonia app; <c>UI</c> is the layer that exists to hold it. The exemption is
    /// narrow on purpose — every mention of Avalonia in Domain, Application, Infrastructure and Localization is
    /// a COMMENT today, and <c>Localization/TrExtension.cs</c> stays clear of the toolkit by duck typing
    /// (<c>public object ProvideValue(IServiceProvider)</c>), so no exception is needed for it.</summary>
    [Fact]
    public void NoLayerBelowTheUiImportsAvalonia()
    {
        string[] belowTheUi = ["Domain", "Application", "Infrastructure", "Localization"];

        var offenders = belowTheUi
            .SelectMany(layer => SourcesIn(layer)
                .SelectMany(p => UsingsOf(File.ReadAllText(p))
                    .Where(u => u.StartsWith("Avalonia", StringComparison.Ordinal))
                    .Select(u => $"{Relative(p)}: using {u};")))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these files pull the UI toolkit into a layer below the UI:\n  " + string.Join("\n  ", offenders));
    }
}
