using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using AcerHelper.UI;

namespace AcerHelper.Tests;

/// <summary>
/// THE USER IS SHOWN WHAT THE INSTALL ADDS BEFORE IT RUNS, and this file holds that claim to something a
/// machine can check. The install behind the "Grant hardware access…" banner is not one permission: it writes
/// udev rules and a tmpfiles.d entry that make specific kernel nodes group-writable, and on an Acer it also
/// writes a modprobe.d options line that changes how <c>acer_wmi</c> behaves at LOAD. That last one is a
/// consequence no "grant access" caption can be read as consenting to, so the prompt exists — and the prompt is
/// only worth having if its rows ARE the installer's own list. A second, hand-kept list of permissions is the
/// thing that drifts: it stays green while the installer gains a file the user never saw.
///
/// SO EVERY CLAIM HERE IS MADE AGAINST THE INSTALLER'S TABLE, never against a copy of it: the rows come from
/// <c>HardwareAccess.DescribedEntries</c> / <c>ApplicableBundledNames</c>, which are the same table
/// <c>Install()</c> walks, and the tests render the prompt for BOTH machine kinds (<c>isAcer: true/false</c>)
/// rather than depending on the DMI of whatever host runs them — the Acer gate changes the row set, and a
/// suite that only ever saw one side of it could not tell a gated entry from a missing one.
///
/// WHAT REDDENS, and each is a real mistake rather than a style: an entry whose description is blank or
/// untranslated; a description the prompt renders differently from the table's (an unsubstituted <c>{0}</c>, a
/// raw key); a row that appears in the prompt but is not installed, or the reverse; the group the prompt names
/// no longer being the one the packaging files grant; and — the one this design exists for — a FOURTH ENTRY
/// (the CPU undervolt's SMU nodes, docs/curve-optimizer-strix-point.md) added to the table without a
/// description. That last one is refused by the compiler, because Description is a positional member of the
/// record; the guard here is what stops the refusal from being silenced with a default value.
/// </summary>
public class HardwareAccessConsentTests
{
    /// <summary>The repository root, from the COMPILER's path (the test host's working directory is the output
    /// folder, where a relative path finds nothing) — the same convention as
    /// <see cref="HardwareAccessWiringTests"/>.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A source file of this repository, read from the compiler's path. The existence assertion is
    /// load-bearing: a moved or renamed file must fail loudly rather than let a "does not contain" claim pass on
    /// an empty string.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The prompt's rows AS THE USER SEES THEM: the bullet lines of a rendered body, prefix stripped.
    /// The prefix is not dropped silently — a line that does not carry it is a frame sentence, and the rows are
    /// identified by it so that a row rendered without its bullet cannot pass as one (see
    /// <c>ThePromptRendersExactlyTheEntriesThisMachineInstalls</c>, which pins both halves).</summary>
    private static string[] RenderedRows(string body)
        => body.Split('\n')
               .Select(l => l.TrimEnd('\r'))
               .Where(l => l.StartsWith("• ", StringComparison.Ordinal))
               .Select(l => l[2..])
               .ToArray();

    /// <summary>The body's frame sentences — everything that is not a row and not blank. Used to hold the
    /// prompt's OWN words to the translation table, so a sentence edited inline cannot ship untranslated.</summary>
    private static string[] FrameLines(string body)
        => body.Split('\n')
               .Select(l => l.TrimEnd('\r'))
               .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("• ", StringComparison.Ordinal))
               .ToArray();

    /// <summary>The rows the renderer must produce, built from the table's OWN pairs rather than from
    /// <c>ConsentRows</c> — the point of the comparison in
    /// <c>ThePromptRendersExactlyTheEntriesThisMachineInstalls</c> is that two different projections of the
    /// table agree, not that a function equals itself. The group is the installer's constant, which the test
    /// below ties to the packaging files.</summary>
    private static string[] ExpectedRows(bool isAcer)
        => HardwareAccess.DescribedEntries(isAcer)
                         .Select(e => Loc.T(e.Description, HardwareAccess.AccessGroup))
                         .ToArray();

    /// <summary>ONE ROW PER INSTALLED FILE, or the prompt is lying in one of two directions: an entry with no
    /// description renders an empty row (the user sees a bullet and nothing after it), and an entry that never
    /// reaches the rows was installed without being consented to. Both are caught here because the expected set
    /// is the installer's own <c>ApplicableBundledNames</c>, in the installer's order.
    ///
    /// The distinctness row is not decoration: two entries sharing one sentence ("write access to the nodes the
    /// app drives") would collapse the difference between them, and the file whose access they now both claim
    /// would go unmentioned.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryEntryTheInstallerPlacesCarriesItsOwnNonEmptyDescription(bool isAcer)
    {
        var entries = HardwareAccess.DescribedEntries(isAcer);

        Assert.NotEmpty(entries);
        Assert.Equal(HardwareAccess.ApplicableBundledNames(isAcer), entries.Select(e => e.Bundled));
        Assert.Equal(entries.Count, entries.Select(e => e.Description).Distinct().Count());

        foreach (var (bundled, description) in entries)
            Assert.False(string.IsNullOrWhiteSpace(description),
                         $"'{bundled}' is in the installer's table with no description — its row in the consent "
                         + "prompt would be blank, and the permission would be asked for in silence");
    }

    /// <summary>THE GUARD THE DESIGN EXISTS FOR: a fourth entry cannot be added without a description. The
    /// compiler already refuses one — <c>Description</c> is a positional member of the <c>InstallerFile</c>
    /// record — but a compiler refusal is silenced by ONE character (<c>string Description = ""</c>), and that
    /// is the edit this row catches: every member must be required, and the description must still be there,
    /// last, under its own name.
    ///
    /// Read by reflection rather than by parsing the source, so it survives reformatting and cannot be satisfied
    /// by prose. The four names in order ARE the contract; changing the shape means changing this row, which is
    /// the review the next permission is supposed to get.</summary>
    [Fact]
    public void AnEntryCannotBeWrittenWithoutItsDescription()
    {
        var type = typeof(HardwareAccess).GetNestedType("InstallerFile", BindingFlags.NonPublic);
        Assert.True(type != null, "HardwareAccess no longer declares an 'InstallerFile' type — this guard has nothing to hold");

        var parameters = type!.GetConstructors().Single().GetParameters();

        Assert.Equal(["Bundled", "Installed", "AcerOnly", "Description"],
                     parameters.Select(p => p.Name));

        foreach (var p in parameters)
            Assert.False(p.IsOptional || p.HasDefaultValue,
                         $"'{p.Name}' has a default value — a new entry could then be added without it, and the "
                         + "consent prompt would render fewer rows than the install places");

        Assert.Equal(typeof(string), parameters[^1].ParameterType);
    }

    /// <summary>EVERY WORD THE PROMPT SHOWS IS A LOCALISATION KEY THAT EXISTS, checked against the RUSSIAN table
    /// — the only table other than English, and the one whose absence is silent (an unknown key falls back to
    /// English, so a missing row never throws and never shows up as anything but untranslated text).
    ///
    /// The keys are taken from the RENDERING rather than from a list kept here, so an inline sentence with no
    /// row in the table is caught too: with English active <c>Loc.T</c> returns the key itself, so whatever the
    /// prompt prints for a frame line or the heading IS the key that has to be in the table. The descriptions
    /// come from the installer's table the same way.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryWordThePromptShowsIsTranslated(bool isAcer)
    {
        var body = HardwareAccessConsent.Body(HardwareAccess.ConsentRows(isAcer));

        var keys = new[] { HardwareAccessConsent.Title(), HardwareAccessConsent.ConfirmText() }
            .Concat(FrameLines(body))
            .Concat(HardwareAccess.DescribedEntries(isAcer).Select(e => e.Description))
            .ToArray();

        Assert.True(keys.Length >= 5, $"only {keys.Length} keys were collected from the prompt — the extraction, not the table, is probably broken");

        foreach (var key in keys)
            Assert.True(Strings.Ru.ContainsKey(key),
                        "the prompt shows a sentence with no entry in Localization/Strings.Ru.cs, so the Russian "
                        + "build shows it in English:\n  " + key);

        // The confirm button deliberately reuses the DRIVER prompt's word for "agree to install this" rather
        // than inventing a second one, so the key is the one that prompt's button already had translated —
        // asserting the literal is what keeps the reuse a decision instead of a coincidence (in English, the
        // source text IS the key, which is why this reads as the word itself).
        Assert.Equal("Install", HardwareAccessConsent.ConfirmText());
        Assert.Equal(Strings.Ru["Install"], Strings.Ru[HardwareAccessConsent.ConfirmText()]);
    }

    /// <summary>THE ROWS ARE THE ENTRY LIST, IN BOTH DIRECTIONS AND IN ORDER, for a machine of each kind. The
    /// expected rows are re-derived from the table's (bundled name, description) pairs and rendered here, so what
    /// this compares is two projections of one table — not <c>ConsentRows</c> against itself. An unsubstituted
    /// <c>{0}</c> (the group the prompt promises access to), a row rendered from the bundled name instead of the
    /// description, or a row silently dropped by the Acer gate all redden it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePromptRendersExactlyTheEntriesThisMachineInstalls(bool isAcer)
    {
        var expected = ExpectedRows(isAcer);
        var rendered = RenderedRows(HardwareAccessConsent.Body(HardwareAccess.ConsentRows(isAcer)));

        Assert.Equal(expected, rendered);

        // ...and the count is the installer's own, so a row that appears in the prompt without a file behind it
        // (or a file with no row) cannot hide behind a matching pair elsewhere in the list.
        Assert.Equal(HardwareAccess.ApplicableBundledNames(isAcer).Count(), rendered.Length);

        // The group is substituted, not left showing "{0}" — a row the user reads as "write access for the {0}
        // group" is a permission granted to nobody anyone can name. And it is NAMED where the table asks for it:
        // the entries that hand access to a group say which group; the modprobe one hands the parameter to
        // nobody and names none, which is why this is a "some row" claim and not an "every row" one.
        Assert.All(rendered, row => Assert.DoesNotContain("{0}", row, StringComparison.Ordinal));
        Assert.Contains(rendered, row => row.Contains(HardwareAccess.AccessGroup, StringComparison.Ordinal));
    }

    /// <summary>The PUBLIC prompt — the one <c>FlyoutCoordinator</c> shows, which reads the machine's own DMI —
    /// must render one of the two sets above and nothing else. Without this the tests above could pin the
    /// parameterized body while the entry point the user actually reaches rendered something else entirely, and
    /// the difference would only ever show on hardware.</summary>
    [Fact]
    public void ThePromptTheUserIsActuallyShownIsOneOfThoseTwoSets()
    {
        var rendered = RenderedRows(HardwareAccessConsent.Message());

        Assert.True(rendered.SequenceEqual(ExpectedRows(true)) || rendered.SequenceEqual(ExpectedRows(false)),
                    "the prompt this machine shows is neither the Acer nor the non-Acer row set:\n  "
                    + string.Join("\n  ", rendered));
    }

    /// <summary>THE GROUP THE PROMPT NAMES IS THE GROUP THE FILES GRANT, and the two are written in different
    /// files by different edits, which is why it is worth a test: the packaging files say the group may be
    /// edited, and editing "wheel" there would leave the prompt promising write access to a group the rules no
    /// longer mention. Read out of both packaging files by their own syntax (a chgrp in the rules, the group
    /// column of a tmpfiles <c>z</c> line) rather than by searching for the constant, so this cannot pass by
    /// finding its own literal in a comment.</summary>
    [Fact]
    public void TheGroupThePromptNamesIsTheOneThePackagingFilesGrant()
    {
        var rules = Regex.Matches(Source("packaging/60-acer-helper.rules"), @"chgrp\s+([A-Za-z0-9_-]+)")
                         .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Equal([HardwareAccess.AccessGroup], rules);

        var tmpfiles = Regex.Matches(Source("packaging/acer-helper.conf"), @"^\s*z\s+\S+\s+\S+\s+\S+\s+([A-Za-z0-9_-]+)",
                                    RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Equal([HardwareAccess.AccessGroup], tmpfiles);
    }

    /// <summary>THE PRIVILEGED PATH IS UNCHANGED BUT NO LONGER DIRECT: the consent is a STEP BETWEEN the banner
    /// and the install, not a label on it. Two things are pinned by position in the executable text (comments
    /// dropped first, so prose describing the prompt cannot satisfy it):
    ///
    /// 1. the dialog is asked BEFORE <c>HardwareAccess.Install</c> is reached — an install that ran first would
    ///    already have written /etc by the time the user could say no, which is the whole failure this feature
    ///    exists to prevent; and
    /// 2. the answer is ACTED ON — a declined prompt must return without installing (and without touching the
    ///    banner state, so the offer is still there to accept later).
    ///
    /// The mechanism itself is deliberately not asserted here beyond that: still one pkexec script, same order,
    /// no new privilege path — <see cref="HardwareAccessWiringTests"/> holds that side of it.</summary>
    [Fact]
    public void TheInstallIsReachedOnlyAfterThePromptIsAnsweredYes()
    {
        var code = string.Join("\n", Source("UI/AppController.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var ask = code.IndexOf("ConfirmHardwareAccessAsync", StringComparison.Ordinal);
        var install = code.IndexOf("HardwareAccess.Install", StringComparison.Ordinal);

        Assert.True(ask >= 0, "the banner's handler no longer asks for consent — the install would run on one click");
        Assert.True(install > ask, "the privileged install is reached before the consent prompt is answered");
        Assert.Contains("if (!await _windows.ConfirmHardwareAccessAsync()) return;", code, StringComparison.Ordinal);
    }

    /// <summary>...and the prompt is the modal dialog, composed from the two pieces this file tests. A prompt
    /// whose body were built and then not shown is the same failure as no prompt at all, and it is invisible from
    /// either side alone.
    ///
    /// Scoped to the ONE method rather than to the file, and that scoping is the test: this coordinator holds
    /// three confirmations, and "<c>ShowAsync</c> appears somewhere in FlyoutCoordinator.cs" would already be
    /// true of the battery-calibration dialog next door if the consent prompt never reached a window at all.</summary>
    [Fact]
    public void ThePromptIsShownThroughTheReposOwnConfirmDialog()
    {
        var coordinator = Source("UI/FlyoutCoordinator.cs");

        var start = coordinator.IndexOf("public Task<bool> ConfirmHardwareAccessAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "FlyoutCoordinator has no ConfirmHardwareAccessAsync — nothing shows the prompt");
        var end = coordinator.IndexOf("\n    public ", start, StringComparison.Ordinal);
        Assert.True(end > start, "the consent method has no end marker — this guard cannot scope itself to it");

        var method = coordinator[start..end];

        Assert.Contains("HardwareAccessConsent.Title()", method, StringComparison.Ordinal);
        Assert.Contains("HardwareAccessConsent.Message()", method, StringComparison.Ordinal);
        Assert.Contains("HardwareAccessConsent.ConfirmText()", method, StringComparison.Ordinal);
        Assert.Contains("Views.ConfirmDialog.ShowAsync", method, StringComparison.Ordinal);
    }
}
