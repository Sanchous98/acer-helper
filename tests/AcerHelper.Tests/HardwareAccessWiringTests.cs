using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE FILES THAT MAKE THE CONTROLS APPEAR ARE ONE FEATURE SPREAD OVER SEVERAL PLACES, and every seam
/// between those places fails SILENTLY. The udev rule and the tmpfiles entry grant write access to nodes the app
/// already drives; the systemd unit re-applies the SMU grant at every boot (the one grant udev cannot deliver —
/// see <see cref="SmuMailboxAccess"/>) and the modprobe.d options line is what makes acer-wmi offer the
/// Predator/Nitro features on a machine its quirk table does not know; the csproj carries all of them into the
/// publish output, which is where <c>HardwareAccess</c> reads them from. Lose any one of them and nothing errors: the offer disappears
/// (<c>RulesNeeded()</c> finds nothing to compare), or it is offered and installs a set that cannot work, or an
/// AppImage is built without the files and the feature is simply absent. So the claims here are about WIRING,
/// checked the way <see cref="AppArgsWiringTests"/> checks the startup switch — by reading the tree from the
/// compiler's path — because the test project's TFM excludes <c>*.Linux.cs</c> and nothing in this suite can
/// execute the installer.
///
/// WHICH FILES A MACHINE GETS IS PART OF THE WIRING, and it is asserted in BOTH directions because both mistakes
/// are real: an Acer that is not offered the options line has a driver whose features stay switched off (the
/// whole point of the file), and a Dell or generic machine that is gated out of the offer loses the vendor-
/// neutral permissions it has today (keyboard backlight, battery thresholds, BIOS attributes). The split is read
/// from the installer's own table through <c>ApplicableBundledNames</c> — a pure function, so it can be pinned
/// without a DMI to fake — and the Acer-only file is identified by CONTENT (the bundled file that names
/// <c>acer_wmi</c>), so a rename cannot slip past these rows.
///
/// THE LIST OF BUNDLED NAMES IS READ FROM <c>HardwareAccess</c>, never restated here: what the tests compare is
/// the installer's own list against the files on disk and against the csproj, so they cannot pass by agreeing
/// with a copy of the list. That is also what makes added files safe — a fourth bundled file is covered by these
/// guards the moment it is listed, with no count to bump.
/// </summary>
public class HardwareAccessWiringTests
{
    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory (the test
    /// host runs with its working directory set to the output folder, where a relative path finds nothing).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A source file of this repository, read from the compiler's path. The existence assertion matters:
    /// a moved or renamed file must fail loudly here rather than let a "does not contain" claim pass on an empty
    /// string.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The names <c>HardwareAccess</c> actually installs, taken out of its <c>Files</c> table. The block
    /// is isolated first (from the table's declaration to its closing bracket) so that a string literal belonging
    /// to some other member cannot be mistaken for a bundled name.</summary>
    private static string[] BundledNames()
    {
        var source = Source("Infrastructure/HardwareAccess.cs");
        var start = source.IndexOf("[] Files =", StringComparison.Ordinal);
        Assert.True(start >= 0, "HardwareAccess no longer declares a 'Files' table — this guard has no list to read");
        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        Assert.True(end > start, "the 'Files' table has no closing bracket — this guard cannot read it");

        var names = Regex.Matches(source[start..end], "\\(\\s*\"([^\"]+)\"")
                         .Select(m => m.Groups[1].Value)
                         .ToArray();
        Assert.True(names.Length > 0, "the 'Files' table holds no entries — every guard below would pass vacuously");
        return names;
    }

    /// <summary>The bundled names must be exactly the packaging files whose only purpose is the installer. The
    /// real risk is the direction that has no other symptom: a permission/options file ADDED to
    /// <c>packaging/</c> and never listed in <c>HardwareAccess</c> is a file that <c>RulesNeeded()</c> does not
    /// know exists — the install then "succeeds" without it and the access it was written to grant silently does
    /// not exist. The selector is the extension (.rules, .conf, .service) rather than a hard-coded list of names:
    /// packaging/ also holds AppImage and MSI layout files (AppRun, the .desktop, the .png, the .wxs), which are
    /// nobody's bundled input, and only these three extensions mean "installed into /etc".
    ///
    /// <c>.service</c> is one of them because of the SMU permission grant's own history: a grant that has to be
    /// re-applied at every boot cannot be a udev rule on this machine (the driver comes from the initramfs), so
    /// the deterministic half is a systemd unit, and the unit reaches /etc through exactly this installer.
    ///
    /// MUTATION-VERIFIED: with the unit in the installer's table, deleting that entry reddens this row alone
    /// among the packaging-file guards — it is the direction with no other symptom, which is why the extension
    /// had to be added here in the same change as the file.</summary>
    [Fact]
    public void EveryPackagingPermissionFileIsInTheInstallersBundledList()
    {
        var onDisk = Directory.EnumerateFiles(Path.Combine(Root(), "packaging"))
            .Select(Path.GetFileName)
            .Where(n => n!.EndsWith(".rules", StringComparison.Ordinal)
                        || n.EndsWith(".conf", StringComparison.Ordinal)
                        || n.EndsWith(".service", StringComparison.Ordinal))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(onDisk, BundledNames().OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>Every name the installer reads must be a file that is actually there — the other direction of the
    /// drift above, and the one whose failure is a broken publish (Content of a missing file) rather than a
    /// missing feature.</summary>
    [Fact]
    public void EveryBundledNameExistsUnderPackaging()
    {
        foreach (var name in BundledNames())
            Assert.True(File.Exists(Path.Combine(Root(), "packaging", name)),
                        $"HardwareAccess bundles '{name}', which packaging/ does not contain");
    }

    /// <summary>THE SPLIT, BOTH DIRECTIONS, read from the installer's own table. On a non-Acer the offer must
    /// still EXIST and must NOT include the acer_wmi options line; on an Acer all three files are in it. The
    /// Acer-only entry is identified by what the file says (the bundled file that names <c>acer_wmi</c>) rather
    /// than by a name restated here, and the expected non-Acer set is "everything else" rather than a hand-
    /// written pair — so adding a fourth bundled file does not redden this row for the wrong reason, and marking
    /// it Acer-only (or failing to) is what does.
    ///
    /// Mutations that redden it, one each: drop the Acer gate from the table's modprobe entry (the options line
    /// then reaches a non-Acer), and flip it onto the udev rule or the tmpfiles entry (a Dell loses the offer).</summary>
    [Fact]
    public void TheModuleOptionsFileIsTheOnlyAcerOnlyEntry_AndTheOfferSurvivesWithoutIt()
    {
        var all = BundledNames();

        // The acer_wmi options file, found by its own content: exactly one bundled file carries an options line
        // for that module. The match is on the LINE ("options acer_wmi"), not on the module name — the udev rules
        // name acer_wmi in prose about the hwmon rule, and a name-only match would finger them instead.
        var moduleFile = all.Where(n => Source($"packaging/{n}").Contains("options acer_wmi", StringComparison.Ordinal)).ToArray();
        Assert.Single(moduleFile);

        // An Acer gets everything, unchanged from before the gate existed.
        Assert.Equal(all.OrderBy(n => n, StringComparer.Ordinal),
                     HardwareAccess.ApplicableBundledNames(isAcer: true).OrderBy(n => n, StringComparer.Ordinal));

        // A machine that is not an Acer keeps the offer — the vendor-neutral files — and not the options line.
        var nonAcer = HardwareAccess.ApplicableBundledNames(isAcer: false).ToArray();
        Assert.NotEmpty(nonAcer);
        Assert.DoesNotContain(moduleFile[0], nonAcer);
        Assert.Equal(all.Except(moduleFile).OrderBy(n => n, StringComparer.Ordinal),
                     nonAcer.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>THE OUTCOME IS READ FROM THE KERNEL, NOT FROM THE RELOAD'S EXIT STATUS, and that is the whole
    /// fix for a false promise the app used to make: the reload is `|| true` (so an in-use module cannot fail an
    /// install that succeeded at everything else), which means its status is unavailable BY DESIGN — while exit
    /// status 0 only says the files landed. The old code returned a bare <c>true</c> there, so an AppImage whose
    /// module could not be unloaded reported "restart to use the unlocked controls" and restarting the app
    /// changed nothing: no app restart can load a module. The check is now the module's own parameter files,
    /// which say what the loaded module is actually running with.
    ///
    /// Position, not just presence: the check has to come AFTER the script has finished (before that, nothing
    /// has been installed and the parameters cannot have changed), and the pending-reboot outcome has to come
    /// from it — so the assertions are made on the text that follows the wait. A version that merely mentions
    /// both names somewhere in the file would pass a presence check while returning "applied" unconditionally.
    ///
    /// Mutations that redden it: put <c>if (p.ExitCode == 0) return true;</c> back; or return
    /// <c>AccessInstall.Applied</c> after the wait without consulting the kernel.</summary>
    [Fact]
    public void TheInstallOutcomeComesFromTheKernelNotFromTheReloadsExitStatus()
    {
        var code = string.Join("\n", Source("Infrastructure/HardwareAccess.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("/sys/module/acer_wmi/parameters", code, StringComparison.Ordinal);
        Assert.DoesNotContain("if (p.ExitCode == 0) return true;", code, StringComparison.Ordinal);

        var wait = code.IndexOf("WaitForExit", StringComparison.Ordinal);
        Assert.True(wait >= 0, "Install() no longer waits for the install script");
        var afterWait = code[wait..];
        Assert.Contains("ParametersAreLive(", afterWait, StringComparison.Ordinal);
        Assert.Contains("PendingReboot", afterWait, StringComparison.Ordinal);
    }

    /// <summary>Both success shapes must reach the user, in words that differ — the pending-reboot case has to
    /// say "a reboot", because "restart the app" is precisely what cannot help — and the words must survive
    /// translation: the English text is the localisation KEY (gettext-style, see <c>Loc</c>), so the literal in
    /// the UI and the literal in <c>Strings.Ru.cs</c> have to be identical or the Russian build silently falls
    /// back to English. The reboot case reaches the user TWICE, on purpose: a status line for the immediate
    /// feedback, and the banner for persistence (the status slot is rewritten by the refresh timer, so an
    /// instruction living only there is gone in seconds) — hence a row for the caption and the XAML binding that
    /// puts it on screen.</summary>
    [Theory]
    [InlineData("Infrastructure/HardwareAccess.cs", "AccessInstall.PendingReboot")]     // decides it
    [InlineData("UI/AppController.cs", "AccessInstall.PendingReboot")]                 // branches on it
    [InlineData("UI/AppController.cs", PendingRebootMessage)]                          // and says so (status line)
    [InlineData("Localization/Strings.Ru.cs", PendingRebootMessage)]                   // translated verbatim
    [InlineData("UI/AppController.cs", "SetHardwareAccessRebootPending")]              // and keeps the banner up
    [InlineData("UI/ViewModels/MainViewModel.cs", RebootBannerCaption)]                // with the order in its caption
    [InlineData("Localization/Strings.Ru.cs", RebootBannerCaption)]                    // translated verbatim
    [InlineData("UI/MainWindow.axaml", "Content=\"{Binding HardwareAccessLabel}\"")]   // which the banner renders
    [InlineData("UI/AppController.cs", "_accessRebootPending")]                       // and survives a UI rebuild
    public void ThePendingRebootOutcomeIsDecidedBranchedAndTranslated(string relativePath, string required)
        => Assert.Contains(required, Source(relativePath), StringComparison.Ordinal);

    /// <summary>The English keys, spelled ONCE here so the UI and the translation table are compared against one
    /// literal rather than against a copy of each other.</summary>
    private const string PendingRebootMessage =
        "Hardware access granted — the acer-wmi driver could not be reloaded, so the new module settings take effect after a reboot.";

    private const string RebootBannerCaption =
        "Restart your computer to finish enabling the unlocked controls (click to retry).";

    /// <summary>THE BANNER'S CLICK MUST BE WIRED BY WHOEVER RAISES IT, in BOTH ways the banner is raised — and
    /// the reboot banner is raised a SECOND time on a FRESH view model, by <c>ApplyHardwareAccessBanner</c> after a
    /// language rebuild. That second raise used to come with no callback at all (the method took none), so the
    /// caption promised "(click to retry)" while the click ran nothing and said nothing, in the one state where a
    /// retry is the only recovery short of a reboot and there is no way back to the installer (the files are in
    /// /etc, so <c>RulesNeeded()</c> is false and the offer never returns).
    ///
    /// THIS ROW IS BEHAVIOURAL WHERE ITS NEIGHBOURS READ TEXT, because the view model is compiled into this TFM and
    /// the command can simply be executed: the caption is a promise, and what it promises is a call.
    ///
    /// MUTATION that reddens it: drop <c>_grantAccess = grant;</c> from
    /// <c>SetHardwareAccessRebootPending</c> — the click then reaches nothing and the recorded count stays zero.</summary>
    [Fact]
    public void TheRebootBannerCarriesTheRetryItsCaptionPromises()
    {
        var clicks = 0;
        var vm = Banner();

        vm.SetHardwareAccessRebootPending(() => clicks++);

        Assert.True(vm.NeedsHardwareAccess);          // the banner is on screen...
        vm.GrantHardwareAccessCommand.Execute(null);
        Assert.Equal(1, clicks);                     // ...and its click is the retry it advertises
    }

    /// <summary>The banner's view model over a machine with no capabilities: this row is about the click, and a
    /// section that existed here would only add places for the build to be wrong. Built the way the lighting
    /// tests build theirs (the sections themselves are their own tests).</summary>
    private static MainViewModel Banner()
    {
        var d = new FakeDevice();
        var fan = new FanSettings(false, Fan.DefaultDuties(), 70);
        return new MainViewModel(d, new UiActions(
            new ProfileActions(_ => { }, TurboToggles: false, _ => { }),
            new FanSection(new FanAxisState(FanMode.Auto, fan, fan),
                           (_, _, _) => { }, (_, _, _) => { }, _ => Task.CompletedTask),
            new GpuSection(new GpuAxisState(0, 0), (_, _) => { }),
            new CpuSection([], null, _ => { }),
            new CoSection([], [], _ => { }),
            new BatterySection(d.Battery, null, null, null),
            new OptionsSection([], [], [], TurboToggles: false, _ => { }, _ => { }, _ => { },
                               AppLanguage.System, _ => { })), lighting: null);
    }

    /// <summary>THE BANNER IS THE HOME FOR THE REBOOT ORDER, and the way it can silently stop being one is the
    /// branch clearing <c>NeedsHardwareAccess</c> — which hides the banner and leaves the install looking
    /// complete: the files are in /etc, so <c>RulesNeeded()</c> goes false and the offer never returns, and the
    /// user waits for controls that a reboot would have given them. So the branch is asserted BOTH ways: it
    /// re-labels and it does not clear; the <c>Applied</c> branch right above it does clear, because there the
    /// banner has finished its job.
    ///
    /// Mutations that redden it: put <c>NeedsHardwareAccess = false;</c> back into the pending branch; or drop
    /// the <c>SetHardwareAccessRebootPending(…)</c> call.</summary>
    [Fact]
    public void ThePendingRebootBranchKeepsTheBannerVisibleAndRelabelsIt()
    {
        var code = string.Join("\n", Source("UI/AppController.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var applied = code.IndexOf("case HardwareAccess.AccessInstall.Applied:", StringComparison.Ordinal);
        var pending = code.IndexOf("case HardwareAccess.AccessInstall.PendingReboot:", StringComparison.Ordinal);
        var end = code.IndexOf("default:", StringComparison.Ordinal);

        Assert.True(applied >= 0 && pending > applied && end > pending,
                    "the two success branches must both exist, Applied first, before the failure default");

        var appliedBranch = code[applied..pending];
        var pendingBranch = code[pending..end];

        Assert.Contains("SetHardwareAccessRebootPending(RequestHardwareAccess)", pendingBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("NeedsHardwareAccess = false", pendingBranch, StringComparison.Ordinal);
        Assert.Contains("NeedsHardwareAccess = false", appliedBranch, StringComparison.Ordinal);
        // ...and remembers the state, because the banner is re-derived on a UI rebuild (the language switch calls
        // ApplyHardwareAccessBanner again, and by then RulesNeeded() is false for the very reason this branch
        // exists) — without the flag the instruction would survive the refresh timer only to vanish on a
        // language change.
        Assert.Contains("_accessRebootPending = true", pendingBranch, StringComparison.Ordinal);

        var bannerApply = code.IndexOf("private void ApplyHardwareAccessBanner()", StringComparison.Ordinal);
        var nextMethod = code.IndexOf("private async Task CheckForUpdatesAsync", StringComparison.Ordinal);
        Assert.True(bannerApply >= 0 && nextMethod > bannerApply, "the banner re-apply method must exist where this guard looks for it");
        var bannerBody = code[bannerApply..nextMethod];
        Assert.Contains("_accessRebootPending", bannerBody, StringComparison.Ordinal);
        // BOTH raises hand the retry over, and the reboot one is the one that had none: it runs on a view model
        // built by the rebuild a moment ago, so an argument-less call here is a caption promising a click that
        // does nothing. The named method is what makes "the retry" one thing in both branches.
        Assert.Contains("SetHardwareAccessRebootPending(RequestHardwareAccess)", bannerBody, StringComparison.Ordinal);
        Assert.Contains("SetHardwareAccessNeeded(RequestHardwareAccess)", bannerBody, StringComparison.Ordinal);
    }

    /// <summary>THE PARSE THAT FEEDS THE CHECK, executed against the shipped file rather than described: the
    /// parameters the installer will demand of the kernel are exactly the pairs of the real options line. This
    /// is what keeps the honest test honest — a file whose prose mentions parameters (this one's does, in nearly
    /// every paragraph) must still yield only the two it sets, and a value changed in the file changes what the
    /// installer compares with.</summary>
    [Fact]
    public void TheInstallersExpectedParametersAreTheShippedOptionsLine()
    {
        var pairs = HardwareAccess.OptionsLineParameters(
            Path.Combine(Root(), "packaging", "acer-helper-modprobe.conf"));

        Assert.Equal([("predator_v4", "1"), ("force_caps", "7200")], pairs);
    }

    /// <summary>ANY WHITESPACE RUN BETWEEN THE TOKENS, because that is what kmod accepts: modprobe.d splits a
    /// directive on spaces and tabs alike, so a tab-separated or re-aligned file is the SAME instruction. This is
    /// not cosmetic — the parser decides whether the parameters are live, so a single-space parser reports a live
    /// module as not live for a reformatted file and hands the user a reboot they do not need. Each row is a
    /// whole file, with the decoy comment first: prose naming parameters must stay invisible to the parser (the
    /// shipped file's own prose does exactly that).</summary>
    [Theory]
    [InlineData("options acer_wmi predator_v4=1 force_caps=7200")]
    [InlineData("options\tacer_wmi\tpredator_v4=1\tforce_caps=7200")]      // tabs, as kmod accepts them
    [InlineData("options  acer_wmi   predator_v4=1    force_caps=7200")]   // a run of spaces
    [InlineData("  options\t acer_wmi  predator_v4=1\t\tforce_caps=7200  ")]
    public void TheOptionsLineParserAcceptsEveryWhitespaceKmodAccepts(string optionsLine)
    {
        var path = Path.Combine(Path.GetTempPath(), $"acer-helper-options-{Guid.NewGuid():N}.conf");
        try
        {
            File.WriteAllText(path,
                "# a comment naming force_caps=1 and predator_v4=0 must not parse as settings\n" +
                optionsLine + "\n");
            Assert.Equal([("predator_v4", "1"), ("force_caps", "7200")], HardwareAccess.OptionsLineParameters(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The kernel spells parameters differently from modprobe — a bool parameter's file reads Y/N while
    /// the options line says 1/0, and <c>force_caps</c> reports its own default as -1 — so the comparison has to
    /// be by meaning. Get this wrong and every install on an Acer reports "applies after a reboot" forever (the
    /// live value never "equals" the wanted one), which is the safe direction to fail but still a lie.</summary>
    [Theory]
    [InlineData("Y", "1", true)]        // the bool parameter, as the kernel reports it
    [InlineData("N", "0", true)]
    [InlineData("7200", "7200", true)]  // the int parameter, equal
    [InlineData("-1", "7200", false)]   // its unset default: not live
    [InlineData("0", "7200", false)]    // the falsified value must not pass for the shipped one
    [InlineData("Y", "0", false)]
    public void TheParameterComparisonUnderstandsTheKernelsSpelling(string live, string wanted, bool same)
        => Assert.Equal(same, HardwareAccess.SameValue(live, wanted));

    /// <summary>The offer and the install must make the SAME decision, so both read the one split
    /// (<c>Applicable(IsAcer())</c>) instead of each carrying its own Acer test — two copies of it would let a
    /// machine be offered a set the install then silently narrows, or the reverse. Renaming that helper or
    /// caching its result in a field reddens this row; that is a decision to state, not a mistake to repair.</summary>
    [Fact]
    public void BothEntryPointsRouteThroughTheOneSplit()
    {
        var source = Source("Infrastructure/HardwareAccess.cs");
        var calls = Regex.Matches(source, Regex.Escape("Applicable(IsAcer())")).Count;
        Assert.True(calls >= 2,
                    $"RulesNeeded() and Install() must both go through the shared split — found {calls} call(s)");
    }

    /// <summary>Each bundled file must reach the publish output, which is where <c>HardwareAccess</c> reads it
    /// from (<c>AppContext.BaseDirectory</c>) — and must do so ONLY for the portable TFM. Parsed as XML rather
    /// than grepped, and asserted per name rather than by counting Content elements: a count is what a fourth
    /// file would break, and the claim is about the mapping, not the total.
    ///
    /// Three things are pinned per item, each load-bearing: the <c>Link</c> is the BARE name (the three land flat
    /// next to the binary, beside the AppImage's own files — a surviving "packaging/" prefix would put them
    /// where nothing looks), <c>CopyToPublishDirectory</c> is PreserveNewest (without it the item exists but a
    /// publish carries nothing — the Dockerfile path, a local publish, the RPM), and the item sits under a
    /// condition that NEGATES a Windows test (the Windows publish feeds the WiX harvest, which would otherwise
    /// pull udev/tmpfiles/modprobe files into the MSI).
    ///
    /// Mutation that reddens it, one row at a time: drop the Condition, drop the "!" from it, change a Link to
    /// include the folder, or drop CopyToPublishDirectory.</summary>
    [Fact]
    public void TheProjectBundlesEachInstallerFileForThePortableTargetFrameworkOnly()
    {
        var doc = XDocument.Load(Path.Combine(Root(), "AcerHelper.csproj"));
        var content = doc.Descendants("Content")
            .Where(e => ((string?)e.Attribute("Include"))?.Contains("packaging", StringComparison.Ordinal) == true)
            .Select(e => (Element: e, Link: (string?)e.Attribute("Link"),
                          Group: (string?)e.Parent?.Attribute("Condition")))
            .ToArray();

        // No packaging item may live outside a non-Windows group: the MSI harvests publish\** wholesale.
        foreach (var (element, link, group) in content)
            Assert.True(NegatesWindows(group),
                        $"a Content item for '{link}' is not gated to non-Windows builds (condition: {group ?? "<none>"})");

        foreach (var name in BundledNames())
        {
            var item = content.SingleOrDefault(c => c.Link == name);
            Assert.True(item.Element != null,
                        $"AcerHelper.csproj has no non-Windows Content item linked as '{name}' — a publish carries no such file, so RulesNeeded() never offers anything");
            Assert.Equal("PreserveNewest", (string?)item.Element!.Attribute("CopyToPublishDirectory"));
            Assert.Equal("PreserveNewest", (string?)item.Element!.Attribute("CopyToOutputDirectory"));
            Assert.Contains($"packaging{Path.DirectorySeparatorChar}{name}",
                            ((string?)item.Element!.Attribute("Include"))!.Replace('\\', Path.DirectorySeparatorChar),
                            StringComparison.Ordinal);
        }
    }

    /// <summary>A condition that applies to the PORTABLE build: a negation that mentions windows. This is the
    /// repository's own spelling (<c>!$(TargetFramework.Contains('windows'))</c>) and the shape, not the exact
    /// string, is what is pinned — a rewrite that still means "not Windows" stays green.</summary>
    private static bool NegatesWindows(string? condition)
        => condition != null
           && condition.TrimStart().StartsWith("!", StringComparison.Ordinal)
           && condition.Contains("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>The one-line options file, with BOTH values this machine was measured on, on that one line.
    /// predator_v4 is what makes acer-wmi offer the Predator/Nitro set at all here (the model is not in the
    /// driver's DMI quirk table) and force_caps is what adds fan control to it, so a config that installs
    /// without either is worse than no config: the prompt succeeds and the feature stays absent. "One options
    /// line" is asserted because several lines for one module merge ambiguously across kmod versions — a second
    /// line could silently disarm this one, which is also why a new parameter must be appended to THIS line
    /// rather than added beside it.</summary>
    [Fact]
    public void TheModprobeOptionsFileCarriesTheMeasuredParametersOnASingleOptionsLine()
    {
        var lines = Source("packaging/acer-helper-modprobe.conf")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        // The SAME token rule the installer's parser uses (any whitespace run, keyword as a token), so this
        // format guard and the live/not-live decision can never disagree about what an options line is: a
        // tab-separated line satisfies both, and a stray second directive is caught by both.
        var options = lines
            .Where(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [var head, ..] && head == "options")
            .ToArray();
        Assert.Single(options);
        Assert.Contains("acer_wmi", options[0], StringComparison.Ordinal);
        Assert.Contains("predator_v4=1", options[0], StringComparison.Ordinal);
        Assert.Contains("force_caps=7200", options[0], StringComparison.Ordinal);
    }

    /// <summary>THE FALSIFIED VALUE IS ABSENT, asserted as an absence because 7206 is the mistake the sources
    /// invite: it is the number that also sets the interface descriptor's WIRELESS | BLUETOOTH bits, and the
    /// first version of this file shipped it on exactly that reasoning. On hardware it registers two phantom
    /// rfkill devices whose status probe fails (the rows below record that), so nothing on the machine would
    /// stop it from coming back — only this assert and the comment do.</summary>
    [Fact]
    public void TheModprobeOptionsFileDoesNotShipTheValueThatRegistersPhantomRfkill()
        => Assert.DoesNotContain("force_caps=7206", Source("packaging/acer-helper-modprobe.conf"), StringComparison.Ordinal);

    /// <summary>WHY 7206 IS WRONG IS WRITTEN DOWN, in the same place as the value, so that a later reader cannot
    /// "correct" the number back from the old derivation: the comment must name the two phantom devices, the
    /// three 0xe2 status failures they log per load, and the descriptor bits that made the wrong number look
    /// right — plus the lesson that follows (a bit reaching the mask need not survive into the registered
    /// device, so the value has to be confirmed by an inventory diff rather than by arithmetic). Without these
    /// rows the file would say "7200" with no trace of why the plausible 7206 is worse.</summary>
    [Theory]
    [InlineData("acer-wireless")]                  // the phantom rfkill devices 7206 registers
    [InlineData("acer-bluetooth")]
    [InlineData("phantom")]
    [InlineData("0xe2")]                           // their failed device-status probe, once per module load
    [InlineData("Get Current Device Status failed")]
    [InlineData("wmid_v2_interface.capability")]   // where the wrong bits were read from
    [InlineData("ACER_CAP_WIRELESS")]
    [InlineData("ACER_CAP_BLUETOOTH")]
    [InlineData("INVENTORY DIFF")]                 // and how the value must be confirmed instead
    public void TheModprobeOptionsFileRecordsWhyThePhantomValueIsWrong(string evidence)
        => Assert.Contains(evidence, Source("packaging/acer-helper-modprobe.conf"), StringComparison.OrdinalIgnoreCase);

    /// <summary>THE DERIVATION IS WRITTEN DOWN, so a kernel bump or a different model forces a REVIEW instead of
    /// a silent copy: the comment must name the bits the shipped mask is made of — including the one this file
    /// exists to add — and the sum in both bases, so the arithmetic is checkable. Without these rows the file
    /// would still say WHERE the value came from (kernel, DMI, commit — the rows above) while leaving HOW it was
    /// derived unrecorded, and the next person would re-derive it from scratch or, far worse, "correct" it.</summary>
    [Theory]
    [InlineData("ACER_CAP_SET_FUNCTION_MODE")]    // present because this machine has ACER_WMID3
    [InlineData("ACER_CAP_PLATFORM_PROFILE")]
    [InlineData("ACER_CAP_HWMON")]
    [InlineData("ACER_CAP_PWM")]                  // the bit force_caps is here to add (no DMI entry grants it)
    [InlineData("0x1C20 = 7200")]                 // the sum's result in both bases, so the arithmetic is checkable
    [InlineData("whole-mask replacement")]        // and WHY the value cannot be reasoned about as an OR
    public void TheModprobeOptionsFileRecordsHowTheMaskWasDerived(string provenance)
        => Assert.Contains(provenance, Source("packaging/acer-helper-modprobe.conf"), StringComparison.OrdinalIgnoreCase);

    /// <summary>The measured consequences, recorded in the file rather than only in a report: what appeared (the
    /// pwm names as the kernel actually spells them — the duty in pwmN, no pwmN_input), that nothing was lost,
    /// and that a write really moves the fans. These are the rows a future kernel bump has to re-measure; the
    /// udev rule's glob depends on the first one.</summary>
    [Theory]
    [InlineData("pwm1_enable")]     // the measured file names, not the assumed pwmN_input
    [InlineData("pwm2_enable")]
    [InlineData("5085")]            // CPU fan rpm at MAX, from 1920
    [InlineData("4510")]            // GPU fan rpm at MAX, from 619
    [InlineData("byte-for-byte identical")]   // nothing lost in the before/after inventory diff
    public void TheModprobeOptionsFileRecordsWhatWasMeasured(string measurement)
        => Assert.Contains(measurement, Source("packaging/acer-helper-modprobe.conf"), StringComparison.Ordinal);

    /// <summary>The provenance the options line has to carry, row by row: the kernel build the value was measured
    /// on, the DMI it was measured for, and the upstream commit that introduced the parameter. Pinning them is
    /// deliberate — they are the file's answer to "may I copy this line onto another machine?", and their
    /// absence is how a value with a rule attached to it becomes an unexplained constant.
    ///
    /// THE KERNEL ROW IS EXPECTED TO REDDEN ON A KERNEL BUMP. That is the file's own rule ("re-derive, do not
    /// copy") made mechanical: the comment and the value are to be re-derived together, and the test refuses to
    /// let the old provenance outlive the bump in silence.</summary>
    [Theory]
    [InlineData("7.2.4-ogc3.1.fc44.x86_64")]   // the kernel build this value was measured on
    [InlineData("AN18-61")]                    // the DMI it was measured for (Acer Nitro AN18-61)
    [InlineData("f9124f2a")]                   // upstream: "platform/x86: acer-wmi: Add predator_v4 module parameter"
    public void TheModprobeOptionsFileRecordsWhereItsValueCameFrom(string provenance)
        => Assert.Contains(provenance, Source("packaging/acer-helper-modprobe.conf"), StringComparison.Ordinal);

    /// <summary>The hwmon rule the fan write depends on, and the property that makes it work: udev sets MODE/GROUP
    /// on device NODES only, so the pwm attributes need a RUN, and the chip must be matched by DEVPATH rather
    /// than by its "name" attribute (which is read-only and therefore useless for a write). The glob is pinned
    /// because the file suffixes depend on the driver build: a rule naming pwm1_input would silently cover one
    /// channel of one build.</summary>
    [Fact]
    public void TheRulesFileGrantsWheelAccessToTheAcerHwmonPwmAttributes()
    {
        var rules = Source("packaging/60-acer-helper.rules");
        Assert.Contains("DEVPATH==\"*/acer-wmi/hwmon/hwmon*\"", rules, StringComparison.Ordinal);
        Assert.Contains("/sys%p/pwm*", rules, StringComparison.Ordinal);
        Assert.Contains("chgrp wheel", rules, StringComparison.Ordinal);
        Assert.Contains("chmod g+w", rules, StringComparison.Ordinal);
    }

    /// <summary>The Linuwu-Sense block is GONE, and this is a guard on that decision rather than on a format: the
    /// Linux backend is module-free by construction (the app no longer binds nitro_sense/predator_sense at all,
    /// and the owner removed the tier), so a rule that chmods those nodes would grant access to nothing while
    /// implying a dependency that no longer exists. A return is a decision to state — this test is where it gets
    /// stated.</summary>
    [Theory]
    [InlineData("nitro_sense")]
    [InlineData("predator_sense")]
    public void TheRulesFileNoLongerReachesForTheLinuwuSenseNodes(string removed)
        => Assert.DoesNotContain(removed, Source("packaging/60-acer-helper.rules"), StringComparison.Ordinal);

    /// <summary>What the deleted block must NOT have taken with it: the nodes the app still uses on this machine.
    /// The platform-profile rule is what makes the profile switch work at all (the vendor handler's class node is
    /// root-only without it) and the hidraw uaccess rules are what make RGB and the EC channel open — systemd
    /// grants uaccess to hidraw for a few hwdb classes only and upstream declined a blanket rule, so these two
    /// lines are the difference between those controllers existing and silently reporting "not present".</summary>
    [Theory]
    [InlineData("/sys%p/profile")]                    // the vendor platform-profile handler's own node
    [InlineData("/sys/firmware/acpi/platform_profile")] // its legacy alias, which tmpfiles misses at boot
    [InlineData("*:0CF2:5130.*")]                     // ENE RGB (keyboard + lightbar) hidraw
    [InlineData("*:1025:174B.*")]                     // the EC power-envelope hidraw
    public void TheRulesFileStillGrantsWhatTheAppUses(string kept)
        => Assert.Contains(kept, Source("packaging/60-acer-helper.rules"), StringComparison.Ordinal);

    /// <summary>THE INSTALL ORDER, which is invisible in a script that is only ever executed on a machine with
    /// polkit — and wrong in a way that looks like success: the rule must be in the ruleset BEFORE the module
    /// reload creates the pwm nodes it matches, and the reload must be LAST so the parameters take effect in the
    /// session rather than at the next boot. Asserted by position within the executable lines (comment lines are
    /// dropped first, so prose that mentions the same commands cannot satisfy or break the ordering).
    ///
    /// The parentheses around the reload are pinned too, and that is not cosmetic: the script is one
    /// <c>&amp;&amp;</c> chain ending in <c>|| true</c>, so an unparenthesised <c>|| true</c> binds to the WHOLE
    /// chain — a failed `install` earlier on would be swallowed and the caller would report success for a
    /// half-installed set. Parenthesised, only the reload may fail, which is the intended degradation: a module
    /// in use (rfkill/wifi hold a reference) means "applies after a reboot".
    ///
    /// And the reload is CONDITIONAL on the options file being part of this install, which is pinned here
    /// because it is one decision rather than two: the reload exists for that file alone, so a machine that never
    /// gets the file is not asked to reload a stranger's driver (on a non-Acer `modprobe -r acer_wmi` would only
    /// report "Module acer_wmi not found" from a root prompt). The effect on the chain is kept deliberate: with
    /// no reload the script ends at the tmpfiles pass, whose status is then the script's own, so an earlier
    /// failure still propagates.</summary>
    [Fact]
    public void TheInstallScriptLoadsTheRuleBeforeItReloadsTheModule()
    {
        var script = string.Join("\n", Source("Infrastructure/HardwareAccess.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var reloadRules = script.IndexOf("udevadm control --reload-rules", StringComparison.Ordinal);
        var trigger = script.IndexOf("--subsystem-match=hwmon", StringComparison.Ordinal);
        var tmpfiles = script.IndexOf("systemd-tmpfiles --create", StringComparison.Ordinal);
        var reloadModule = script.IndexOf("modprobe -r acer_wmi", StringComparison.Ordinal);

        Assert.True(reloadRules >= 0, "the install script no longer reloads the udev rules");
        Assert.True(trigger >= 0, "the install script no longer triggers the hwmon subsystem — the new rule would only apply at the next boot");
        Assert.True(tmpfiles >= 0, "the install script no longer applies the tmpfiles entry");
        Assert.True(reloadModule >= 0, "the install script no longer reloads acer_wmi — the parameters would only apply at the next boot");

        Assert.True(reloadRules < trigger, "the rules must be reloaded before the trigger replays devices through them");
        Assert.True(trigger < tmpfiles, "the trigger must run before the tmpfiles pass (existing order, kept)");
        Assert.True(tmpfiles < reloadModule, "the module reload must be LAST — it is what creates the pwm nodes the rule has to match");
        Assert.Contains("(/usr/sbin/modprobe -r acer_wmi && /usr/sbin/modprobe acer_wmi) || true", script, StringComparison.Ordinal);
        Assert.Contains("if (applicable.Any(f => f.AcerOnly))", script, StringComparison.Ordinal);
    }

    /// <summary>The rules-less case, executed rather than read: with no bundled file next to the binary the offer
    /// must be OFF, because there is nothing to install. This is a live run of <c>RulesNeeded()</c> — the only
    /// behavioural check in this file, and one whose premise is asserted first: the test layout carries no
    /// bundled packaging file, which is itself the csproj gate working (the test project's TFM is
    /// net10.0-windows, so the Linux-only Content items are not copied into it). The premise assertion is what
    /// keeps this from passing on the DMI gate instead of on the missing bundle.</summary>
    [Fact]
    public void TheOfferIsAbsentWhenNothingIsBundled()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "60-acer-helper.rules")),
                     "this test's premise is that the test output carries no bundled packaging file");
        Assert.False(HardwareAccess.RulesNeeded());
    }
}
