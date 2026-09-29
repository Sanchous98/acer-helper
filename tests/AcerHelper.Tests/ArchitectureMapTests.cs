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

    /// <summary>THE OS SPLIT IS A FILE-NAME CONVENTION AND IT WORKS AT ANY DEPTH — which is why this rule changed
    /// on 2026-09-22. It used to read "must live under Infrastructure/", on the reasoning that the split was a
    /// backend concern; that stopped covering the tree when <c>UI/WindowFlags</c> needed a platform half, and the
    /// type cannot move down to satisfy the rule — it takes an <c>Avalonia.Controls.Window</c>, and Avalonia is
    /// forbidden below UI (see the rule above). The failure the old wording named — "compiled away in one target
    /// and not the other" — is what the split is FOR, and <c>AcerHelper.csproj</c>'s <c>&lt;Compile Remove&gt;</c>
    /// globs name no directory, so the suffix selects at any depth.
    ///
    /// What is still wrong, and what this checks: a platform file in a layer where a platform half means nothing —
    /// <c>Domain/</c>, <c>Application/</c>, <c>Bootstrap/</c>, <c>Localization/</c>. Today: the 45 Infrastructure
    /// halves, and the two under <c>UI/WindowFlags</c>.
    ///
    /// A COUNTERPART IS NOT REQUIRED, and a first version of this change got that wrong: demanding a pair for
    /// every half, and a shared file of the same stem, listed 39 legitimate files. Most halves are the only
    /// implementation of their capability on that OS — <c>WmiSession.Windows.cs</c> has no Linux twin because WMI
    /// is Windows', <c>Hwmon.Linux.cs</c> has no Windows twin because hwmon is Linux' — so the requirement would
    /// forbid the established shape rather than catch a defect.</summary>
    [Fact]
    public void EveryOsSplitFileLivesInALayerWhereAPlatformHalfMeansSomething()
    {
        string[] allowed = ["Infrastructure", "UI"];
        var offenders = Layers.Where(l => !allowed.Contains(l))
                              .SelectMany(SourcesIn)
                              .Where(p => p.EndsWith(".Windows.cs") || p.EndsWith(".Linux.cs"))
                              .Select(Relative)
                              .ToArray();

        Assert.True(offenders.Length == 0,
            "these platform files are selected by file-name suffix and must live under Infrastructure/ or UI/:\n  "
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
    /// The case that keeps the rule concrete is the lighting door. <c>LaptopService</c> owns the settings graph
    /// and the per-mode light zones; <c>Application/LightZone.cs</c> declares <c>ILightZoneMode</c> beside the
    /// two use cases that read and write through it (<c>ReadLightZone</c>, <c>ApplyLightZone</c>), and the state
    /// crosses as <c>LightZoneState</c> — a Domain type. The direct route — let those use cases name the stored
    /// container they write into — would have made Application depend on the layer it orchestrates. Nothing
    /// under <c>Application/</c> names it: the implementation lives where naming Infrastructure is the job
    /// (<c>Infrastructure/Composition/LaptopService.Lighting.cs</c>).
    ///
    /// WHAT IT FORCED, and this is the part worth reading before moving anything else. When the service layer
    /// and the persisted container moved to Infrastructure, this rule is what decided the SHAPE of that move:
    /// the re-apply operation could not stay whole, because its result was built out of the container's types
    /// and Application may not name them, so only the PLAN stayed (<c>Application/ReapplyPlan.cs</c>, stated in
    /// Domain vocabulary) and the executor went with the service. What came back afterwards is the operation
    /// ITSELF, and only because the result changed shape first: the outcome now speaks Domain vocabulary
    /// (<c>FanAxisState</c>, <c>GpuAxisState</c>) and the implementing layer translates at the accessor that
    /// already reads the preset. Application is EIGHTEEN files today — the four this docstring used to name, plus
    /// the family of APPLIED EDITS that landed afterwards on the same terms (FanAxis, GpuOffsets, CpuPowerOverlay,
    /// Undervolt, DeclaredSetting, each one contract plus its use cases), the family of ACTION use cases that
    /// came off the service's public surface next (ProfileSwitch, Preferences, HardwareToggles, ProfilePower,
    /// ModeApply, AppActions), and the family of QUERY use cases that came off it after those (Queries.cs — the
    /// profile/fan/GPU/CO/CPU-power/sensor/battery readers, each a contract plus the use case that owns it); the
    /// automatic-undervolt wrapper
    /// (CpuLoadTest, UndervoltSweep) was among them until it left for Infrastructure on 2026-09-27 (see the
    /// paragraph below), which is why the count is what it is and not larger — and the rule is the reason for
    /// every boundary in each of them, not an accident of the moves.
    ///
    /// WHAT IT STILL FORBIDS, so nobody mistakes the above for a relaxation: a use case that needs the STORED
    /// CONTAINER (the per-mode presets and the graph they live in) or a vendor port by name still cannot live
    /// here, and that is most of what the service does. The re-apply got in because it needs a schedule, the
    /// axes that schedule names, and a contract — nothing else. The applied edits got in the same way and not by
    /// an exception: an axis's state crosses as the Domain value it already had
    /// (<c>FanAxisState</c>, <c>GpuAxisState</c>), so the container is never named and the rule the use case
    /// states — what an edit must not clobber, what is remembered before it is written — is stated without it.
    /// What could NOT get in that way is measured in the retired analysis — the measurements it carried are kept
    /// in docs/open-decisions.md's note on its retirement: the profile switch (a
    /// lock held across the port call). It got in afterwards on the same terms as the rest, once the lock was
    /// split into the contract's two halves (<c>IProfileTarget.CanApply</c>/<c>Apply</c>) and the announcement the
    /// switch must not skip became a second contract (<c>IProfileAnnouncer</c>, implemented by the UI's
    /// <c>LightingCoordinator</c>): <c>Application/ProfileSwitch.cs</c> names neither the graph nor the lighting
    /// coordinator, so the rule below held throughout. The per-mode lighting was named there too, and is NOT any
    /// more: it was
    /// stopped by a contract-shaped reason (the door handed the UI a live reference it edited in place, so
    /// hiding the graph would have broken it by construction), the owner overruled that reason, and the axis
    /// landed on the same terms as the others — one Domain type for one axis (<c>LightZoneState</c>), one
    /// contract (<c>ILightZoneMode</c>), two use cases (<c>Application/LightZone.cs</c>: <c>ReadLightZone</c>
    /// and <c>ApplyLightZone</c>).
    ///
    /// Today Application imports Domain and, for the language preference, Localization — the same permission this
    /// file's sibling grants and the reason it is in the allowed list below: the settings model's
    /// <c>AppLanguage</c> went with the container, but a use case that records the chosen language names it, so
    /// the permission is exercised again (<c>Application/Preferences.cs</c>). Localization is deliberately NOT
    /// removed from the list below either way: it was allowed before for a real design reason, and the rule fences
    /// Application from the layers ABOVE it, which is what the sentence after this one is about. The UI may still
    /// reach Infrastructure (it does, by design; see the file's own docstring); this rule fences Application, which
    /// has no such need.
    ///
    /// WHAT LEFT APPLICATION 2026-09-27, AND WHY THE RULE GOT SMALLER RATHER THAN LOOSER. The owner ruled that
    /// the automatic undervolt is in substance a WRAPPER over the manual voltage change
    /// (<c>Application/Undervolt.cs</c>) and carries no independent domain logic — it is UI + Infrastructure
    /// only. So the whole feature moved out of Application into <c>Infrastructure/Vendors/Generic/</c>:
    /// <c>CpuLoadTest.cs</c> (the load runner) and <c>UndervoltSweep.cs</c>, plus <c>Domain/CpuStress.cs</c> (the
    /// kernel) into the same folder. (The compute-error canary and the startup watchdog — <c>UndervoltCanary.cs</c>,
    /// <c>UndervoltWatchdog.cs</c> — were removed outright on 2026-09-27; see docs/auto-undervolt.md.) The
    /// manual undervolt's own contract pair (<c>Application/Undervolt.cs</c>: <c>IUndervoltTarget</c>/
    /// <c>ApplyUndervolt</c>) — a genuine use case over the domain port — STAYS, which is exactly the point:
    /// Application shed the wrapper, not the operation. The rule below is unchanged and no exception was added;
    /// the move only makes the enumerated set smaller.
    ///
    /// MUTATION-VERIFIED, so the rule is not vacuous: <c>using AcerHelper.Infrastructure.Composition;</c> added to
    /// <c>Application/LightZone.cs</c> reddens this test and no other in the file.</summary>
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

    /// <summary>NO <c>static Run(…)</c> WITH A CONTRACT PARAMETER UNDER <c>Application/</c>. A use case in this
    /// layer OWNS its dependency through its CONSTRUCTOR (<c>sealed class XxxUseCase(IDep dep)</c> with a
    /// <c>Run(…)</c>) — the shape every applied edit, action and query was moved to, and the shape the three
    /// last offenders were converted to (<c>ReapplySettings</c>, <c>ReadLightZone</c>, <c>ApplyLightZone</c>). The
    /// forbidden form is the STATIC-with-target <c>public static Run(…, IDep dep)</c>: it puts the dependency
    /// back in the caller's hands, which is exactly the hand-wiring the constructor-ownership seam exists to
    /// remove (a caller that can pass a contract in can also build the use case over the wrong one).
    ///
    /// WHAT COUNTS AS AN INTERFACE PARAMETER is a parameter whose type name begins <c>I</c> + an uppercase letter
    /// (<c>ILightZoneMode</c>, <c>IReapplyTarget</c>, <c>IFanAxisTarget</c>, …) — the tree's interface naming, and
    /// the only thing the parameter list needs to be scanned for. A use case whose Run takes DOMAIN values
    /// (<c>ReapplyTrigger</c>, <c>LightZoneState</c>) is untouched by this rule and stays green.
    ///
    /// THE PURE STATICS ARE DELIBERATELY NOT EXCEPTIONS: <c>ReapplyPlan</c>, <c>AutostartPolicy</c> and
    /// <c>AppArgs</c> are static classes with no interface parameters (or no <c>Run</c> at all), so they never
    /// match — no allow-list entry is needed, and adding one would be the loophole this test exists to close.
    ///
    /// MUTATION THAT REDDENS IT: adding <c>public static LightZoneState Run(string zone, ILightZoneMode mode)</c>
    /// to any file under <c>Application/</c> — the parameter list names an interface and the file is listed.</summary>
    [Fact]
    public void NoApplicationUseCaseIsAStaticRunTakingItsContractAsAParameter()
    {
        // A `public static <return> Run(<params>)` declaration; the parameter list may span lines.
        var declaration = new Regex(
            @"public\s+static\s+[\w<>?\[\],\.\s]+?\sRun\s*\((?<params>[^)]*)\)", RegexOptions.Multiline);

        var offenders = SourcesIn("Application")
            .SelectMany(p =>
            {
                var text = File.ReadAllText(p);
                return declaration.Matches(text)
                    .Where(m => Regex.IsMatch(m.Groups["params"].Value, @"\bI[A-Z]\w*"))
                    .Select(_ => Relative(p));
            })
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Application use cases must own their dependencies through a constructor, not a static Run(dep):\n  "
            + string.Join("\n  ", offenders));
    }
}
