using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AcerHelper.Tests;

/// <summary>
/// WHAT THE DOMAIN IS ALLOWED TO CARRY — the second half of the fence
/// <see cref="ArchitectureMapTests"/> is, and the one its <c>using</c>-based rules cannot build.
///
/// That file checks the EDGES between the layers: a Domain file may not import Infrastructure, Application may
/// not import Infrastructure, and so on. Every one of those rules stays green through the failure this file is
/// about, because the violation was never an import — it was a TYPE. <c>ProfileKind</c> and the two colours
/// travelled on <c>PerformanceProfile</c>, a Domain record, so the Domain named and stored them while reading
/// neither, and no import was involved: the enum and the colours were Domain declarations beside it.
///
/// THE DECISION THESE RULES PIN (owner, 2026-09-22) is that the Domain carries "только значения, важные для
/// логики" — only values the logic depends on. A value the Domain merely CARRIES, so that some layer above can
/// look at it, is a value the Domain should not know about; it belongs where it is produced and used. So
/// <c>ProfileKind</c> moved to Infrastructure (Infrastructure/Vendors/Generic/ProfileKind.cs) with a lookup the
/// offering port implements (<c>IProfileTraits</c>), and <c>Accent</c>/<c>FlashColor</c> left the profile record
/// for the vendor tables that had written them all along. <c>AccentColor</c> deliberately STAYED a Domain type:
/// it is the payload of a lighting write, so the lighting chain really does reason in it.
///
/// A FAILURE HERE IS A DECISION, NOT A BUG. Returning any of this is a decision to argue rather than a line to
/// restore, and the yardstick to argue it against is the owner's: does the Domain's own logic branch on the
/// value? If the answer is no, it belongs in the layer that produced it. The rule is deliberately a
/// SOURCE-TEXT check, so a re-added field reddens it wherever it is put back — including in a file that does
/// not exist yet — and its mutations are named in each test.
/// </summary>
public class DomainNeutralityTests
{
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

    /// <summary>Every C# file under the Domain. The folder is asserted to exist and to hold sources for the same
    /// reason <see cref="Source"/> asserts a file: a rule over a renamed or deleted directory would otherwise
    /// pass by having nothing to read.</summary>
    private static string[] DomainSources()
    {
        var root = Path.Combine(Root(), "Domain");
        Assert.True(Directory.Exists(root), "the tree no longer has Domain/ — this guard is looking at nothing");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        return files;
    }

    /// <summary>
    /// THE CLASS: A DOMAIN TYPE THAT CARRIES A VALUE THE DOMAIN NEVER READS.
    ///
    /// <c>ProfileKind</c> stood in <c>Domain/Models.cs</c> and travelled as a field of <c>PerformanceProfile</c>.
    /// Nothing under <c>Domain/</c> or <c>Application/</c> read a member of it — every branch on it was
    /// Infrastructure's (the Turbo switch in <c>LaptopService.Profiles</c>, the EC usage-mode byte in
    /// <c>AcerEcHidController.ModeFor</c>, the vendor tables) or the UI's — so the Domain was shipping a
    /// CLASSIFICATION to two layers that both already knew how to classify their own profiles. It lives in
    /// Infrastructure now, and the port that offers the profiles answers for them (<c>IProfileTraits</c>),
    /// which is the only place the answer can be complete: the port that lists the modes is the port that has
    /// their table.
    ///
    /// MUTATION that reddens it: put the enum back, or re-add the field — <c>ProfileKind Kind</c> on
    /// <c>PerformanceProfile</c> in <c>Domain/Models.cs</c>, or a <c>using</c>-free <c>ProfileKind</c> reference
    /// anywhere under <c>Domain/</c>. Observed red with:
    /// <c>public sealed record PerformanceProfile(string Id, string DisplayName, ProfileKind Kind);</c>
    /// added to Domain/Models.cs (plus the enum declaration it needs, which reddens the same row).
    /// </summary>
    [Fact]
    public void TheDomainDoesNotNameProfileKind()
    {
        var offenders = DomainSources()
            .Where(file => File.ReadAllText(file).Contains("ProfileKind", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Root(), file))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "ProfileKind is named under Domain/ again — the classification of a performance profile is the "
            + "BACKEND's (Infrastructure/Vendors/Generic/ProfileKind.cs, reached through IProfileTraits), because "
            + "no Domain or Application code branches on it. The owner's yardstick: \"только значения, важные для "
            + "логики\" — a value the Domain only CARRIES is a value it should not know, and returning this one is "
            + "a decision to argue rather than a line to restore:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE SAME CLASS, on the record itself: <c>PerformanceProfile</c> must carry the opaque <c>Id</c> a backend
    /// understands and the display name handed in from outside, and NOTHING ELSE. <c>Accent</c> and
    /// <c>FlashColor</c> were the other half of the same violation — two colours no conditional anywhere in the
    /// tree branched on, read only where they were painted (the tray icon, the profile segment, the lightbar
    /// palette). They live in the vendor tables that wrote them, and the UI reads them from the backend through
    /// the same lookup.
    ///
    /// The declaration is matched rather than the whole file ON PURPOSE: <c>AccentColor</c> is a Domain type and
    /// stays one — it is the RGB payload of a lighting write (<c>Domain/Rgb.cs</c>) — so the forbidden thing is
    /// the colour ON THE PROFILE RECORD, not the colour in the layer. The parameter list is what is searched, so
    /// the doc comment above the record (which names them to explain the removal) is not an offender.
    ///
    /// MUTATION that reddens it: <c>public sealed record PerformanceProfile(string Id, string DisplayName,
    /// AccentColor? Accent = null, AccentColor? FlashColor = null);</c> in <c>Domain/Models.cs</c>. Observed red
    /// with exactly that change; the row is also reddened by any other member that reintroduces a colour.
    /// </summary>
    [Fact]
    public void TheProfileRecordCarriesNoColour()
    {
        var declaration = Regex.Match(Source("Domain/Models.cs"), @"record\s+PerformanceProfile\s*\((?<params>[^)]*)\)");

        Assert.True(declaration.Success,
            "Domain/Models.cs no longer declares 'record PerformanceProfile(...)' — this guard is looking at nothing, "
            + "so a record that reappeared elsewhere would go unchecked");

        var parameters = declaration.Groups["params"].Value;
        var offenders = new[] { "Accent", "FlashColor", "Color" }
            .Where(token => parameters.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "PerformanceProfile carries a colour again (" + string.Join(", ", offenders) + ") — the colours a "
            + "profile is painted with belong to the vendor table that wrote them, reached through IProfileTraits; "
            + "the Domain's record keeps the opaque Id and the display name. The owner's yardstick: \"только "
            + "значения, важные для логики\" — nothing in the tree branches on these, they are only carried and "
            + "painted, so returning them is a decision to argue rather than a line to restore:\n  "
            + $"record PerformanceProfile({parameters})");
    }
}
