using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE STARTUP SWITCH IS NAMED, NOT SPELLED — at every end that has to agree about it.
///
/// <c>AppArgs</c> states its own reason to exist: the launcher, the autostart registration and the arg parsing
/// are "centralised so [they] can't drift apart — a mismatch between what gets registered and what gets parsed
/// silently breaks autostart (which is exactly what bit us before)". Centralising the string makes the two ends
/// agree TODAY by construction, but agreement by construction is not a guard: the failure mode this type exists
/// for is one end quietly spelling the switch itself, which is how the bug happened, and a hand-written literal
/// at either end is invisible to every other test in this suite — the registration is a scheduled task or a
/// .desktop file and the parse lives in <c>App.axaml.cs</c>, none of which this suite can execute.
///
/// SO THE CLAIM IS ABOUT SOURCE WIRING, not about behaviour, and it is checked the way
/// <see cref="ArchitectureMapTests"/> checks the layer map: by reading the tree from the compiler's path. The
/// claim is narrow on purpose — each end must REFER to the constant. It does not pin the constant's VALUE (a
/// renamed switch is a decision, and pinning the literal would make the rename red for no reason, so the
/// required pattern below spells the SYMBOL and never its value), and it does not assert that the registration
/// and the parse produce the same string: they cannot differ while both read one constant, and such a test would
/// compare the constant with itself.
///
/// THE PATTERN IS AS PRECISE AS THAT END NEEDS, and that is measured rather than assumed.
/// <c>Autostart.Windows.cs</c> names the constant in the task XML, so a check that the FILE names it stays green
/// while the registration itself has gone literal; the mutation "replace the task XML's
/// <c>{AppArgs.Startup}</c> with <c>--startup</c>" was run and passed such a check, which is why that row pins
/// the argument ELEMENT instead. The staleness check that used to sit in <c>EnsureCurrent</c> moved to
/// <c>AutostartPolicy</c> (the pure decision, Application layer), so that file is now the fourth end named here.
/// The other three files name it once each, so naming the constant is exact for them.
///
/// MUTATION-VERIFIED, one end at a time, and each is the historical bug:
/// <c>Autostart.Windows.cs</c>'s <c>&lt;Arguments&gt;</c> hard-coded, <c>Autostart.Linux.cs</c>'s Exec line
/// hard-coded, and <c>App.axaml.cs</c>'s <c>Contains</c> hard-coded each redden exactly their own row of
/// <see cref="EveryEndThatMustAgreeAboutTheStartupSwitch_WiresTheOneConstant"/> — three runs, one row red each.
/// Note what the third one means: the PARSE end is reachable here even though no test can execute it.
///
/// WHAT A REWRITE WOULD DO. An end that stopped naming the constant — an alias, a <c>using static</c> — reddens
/// this file even though it would still work. That is intended and is the same trade <c>ArchitectureMapTests</c>
/// makes: a failure here is a decision to state, and the repair is one word at the call site.
/// </summary>
public class AppArgsWiringTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Where the constant is declared. Asserted so the rows below cannot pass by agreeing on a name that
    /// no longer exists — deleting <c>AppArgs</c> would break the ends too, but the failure would then read as a
    /// wiring mistake at three unrelated call sites. The DECLARATION is spelled here rather than the constant's
    /// VALUE: what the switch is called on the command line is a decision, and pinning it would make a rename red
    /// for no reason.
    ///
    /// Mutation that reddens it: move this declaration out of <c>Application/AppArgs.cs</c>, or rename the
    /// constant (and the three ends with it) — the rename is the change this anchor is here to make visible.</summary>
    [Fact]
    public void TheStartupSwitchIsDeclaredInApplicationAppArgs()
    {
        Assert.Contains("const string Startup", Source("Application/AppArgs.cs"), StringComparison.Ordinal);
    }

    /// <summary>Every end that has to agree about the switch, with the most precise source pattern that end
    /// needs (see the class remarks for why two of the three are file-level). The registration is TWO files
    /// because the mechanism is per-OS (<c>Autostart.*.cs</c>, selected by file-name suffix) and BOTH register
    /// the same switch — the Linux one is compiled out of this test project's TFM, which is exactly why it is
    /// read here rather than executed: a guard that only saw the Windows half would let the two registrations
    /// drift apart, and that divergence would ship.
    ///
    /// A row fails two ways, and both are wanted: the pattern is absent (the end went literal, or moved), or the
    /// FILE is absent (<see cref="Source"/> throws rather than asserting on an empty string — the vacuity this
    /// guard would otherwise hide behind, and the reason <c>ArchitectureMapTests</c> has
    /// <c>EveryLayerFolderExistsAndHoldsSources</c>).</summary>
    [Theory]
    [InlineData("Infrastructure/Vendors/Generic/Autostart.Windows.cs",
                "<Arguments>{AppArgs.Startup}</Arguments>")]   // registers: the scheduled task's argument element
    [InlineData("Application/AutostartPolicy.cs",
                "AppArgs.Startup")]                           // heals: whether the task already carries the switch
    [InlineData("Infrastructure/Vendors/Generic/Autostart.Linux.cs",
                "AppArgs.Startup")]                           // registers: the .desktop file's Exec line
    [InlineData("UI/App.axaml.cs",
                "AppArgs.Startup")]                           // parses: decides startMinimized
    public void EveryEndThatMustAgreeAboutTheStartupSwitch_WiresTheOneConstant(string relativePath, string required)
    {
        Assert.Contains(required, Source(relativePath), StringComparison.Ordinal);
    }

    /// <summary>A source file of this repository, read from the compiler's path.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }
}
