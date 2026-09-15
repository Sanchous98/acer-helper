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
/// claim <c>Domain</c> is dependency-free: <c>Domain/Settings.cs</c> imports <c>Localization</c> for
/// <c>AppLanguage</c>, which is listed as an explicit exception rather than quietly tolerated. And it is a rule
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

    /// <summary>Domain points DOWN at nothing. Localization is the single exception and it is named here rather
    /// than tolerated silently: <c>Domain/Settings.cs</c> stores <c>AppLanguage</c>, so the string tables sit
    /// below Domain in the dependency order as a shared kernel. Everything else — Infrastructure, Application,
    /// UI — is forbidden, because a Domain type that reaches one of them stops being the neutral model the
    /// backends are written against.</summary>
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
