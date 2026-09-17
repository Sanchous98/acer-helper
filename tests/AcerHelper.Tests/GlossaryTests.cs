using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;

namespace AcerHelper.Tests;

/// <summary>
/// The glossary (<c>docs/context-map.md</c>) is only worth having while its words still name things that
/// exist. A dictionary that drifts is worse than none: it reads as authoritative while describing types that
/// were renamed or deleted, which is precisely how a shared vocabulary rots.
///
/// So the doc's first table has a contract — its first column is a TYPE NAME, in backticks — and this test
/// holds it: every name there must resolve to a type in the shipped assembly. Renaming a type now means
/// updating the glossary, which is the point; the doc becomes a place a rename has to be reasoned about rather
/// than a file nobody reads.
///
/// SCOPE, stated because it is easy to over-read: this checks RESOLVABILITY, not meaning. A row whose
/// description has gone stale still passes. What it forbids is the silent kind of rot — a name that no longer
/// exists — and the tables of words WITHOUT a type (the five "profile" channels, the triggers) are deliberately
/// left un-backticked so they are not swept up in it.
/// </summary>
public class GlossaryTests
{
    private static string DocPath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "docs", "context-map.md");

    /// <summary>A table row whose FIRST cell is a single backticked identifier: the shape the contract
    /// describes. Rows whose first cell is prose (the "words without a type" tables) are skipped by construction
    /// rather than by a list of exceptions.</summary>
    private static readonly Regex TypeRow = new(@"^\|\s*`([A-Za-z_][A-Za-z0-9_]*)`\s*\|", RegexOptions.Multiline);

    private static string[] NamesInTheGlossary()
    {
        var path = DocPath();
        Assert.True(File.Exists(path), $"the glossary is missing at {path}");
        return TypeRow.Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToArray();
    }

    /// <summary>Every type the shipped assembly declares, by simple name — enums, interfaces and static classes
    /// included, because the glossary legitimately names all three.</summary>
    private static HashSet<string> ShippedTypeNames()
        => typeof(Settings).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>One name that does not resolve is one word of the project's shared vocabulary pointing at
    /// nothing.</summary>
    [Fact]
    public void EveryTypeTheGlossaryNamesStillExists()
    {
        var shipped = ShippedTypeNames();

        var unknown = NamesInTheGlossary().Where(name => !shipped.Contains(name)).Distinct().ToArray();

        Assert.True(unknown.Length == 0,
            "docs/context-map.md names types that no longer exist in the assembly — update the glossary (or "
            + "the code) so the two agree:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>Non-vacuity: if the contract's markup ever changes shape (a different quote character, a table
    /// with no leading pipe), the regex above would match nothing and the test would pass while checking
    /// nothing at all. The glossary is expected to name a substantial part of the model, so a handful of rows is
    /// a broken parse rather than a short document.</summary>
    [Fact]
    public void TheGlossaryNamesEnoughTypesToBeWorthChecking()
    {
        var names = NamesInTheGlossary();

        Assert.True(names.Length >= 15,
            $"only {names.Length} type rows were parsed from docs/context-map.md — the table's markup probably "
            + "changed shape, and the resolvability check above is now vacuous");
    }

    /// <summary>The three types this work introduced are in the glossary, so the vocabulary for the axes and the
    /// offset has one home rather than living in the commit that added them.</summary>
    [Theory]
    [InlineData("ModeKey")]
    [InlineData("ModeAxis")]
    [InlineData("ModeAxisTable")]
    [InlineData("EmptyAxisPolicy")]
    [InlineData("ReassertOwner")]
    [InlineData("OffsetCounts")]
    public void TheNewlyIntroducedTypesAreInTheGlossary(string name)
        => Assert.Contains(name, NamesInTheGlossary());
}
