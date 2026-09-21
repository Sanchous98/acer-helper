using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Infrastructure;

namespace AcerHelper.Tests;

/// <summary>
/// THE GRANT THAT HAS TO SURVIVE EVERY BOOT, held to something a machine can check. Three claims live here and
/// they fail in three different ways, none of which is visible at runtime:
///
/// 1. THE OFFER'S NEW HALF — the app re-offers the install when the PERMISSION is missing and not only when a
///    file differs. The rule is <c>SmuMailboxAccess.GrantMissing</c>, pure and I/O-free, so every branch is
///    executed here — including the one that must never be got wrong: a machine where the nodes do not exist at
///    all (a Dell, or an Acer without ryzen_smu) must NOT be offered an install for a driver it does not have.
/// 2. THE GRANT ITSELF — a bounded wait in the udev rule (the bind event does not promise the attribute files
///    are there yet) and the systemd unit that re-applies it after the module is up on every boot. Both are
///    source text: no test in this suite can install a unit or run udev, so the claims are made the way
///    <see cref="HardwareAccessWiringTests"/> makes them — by reading the tree from the compiler's path.
/// 3. THE ONE NODE LIST — <c>SmuMailboxAccess.Nodes</c>, the udev rule and the unit must name the same four
///    attributes. The list is READ from the installer's own constant rather than restated in an
///    <c>[InlineData]</c>, so a fifth attribute added in one place and forgotten in another reddens this file
///    instead of drifting; the two packaging files are the copies that cannot be shared with C#.
///
/// WHAT IS NOT CLAIMED: that any of it works on a real boot. A boot is the one thing this suite cannot stage —
/// see the report this change came with, and docs/curve-optimizer-linux.md.
/// </summary>
public class SmuMailboxAccessTests
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

    /// <summary>A file's EXECUTABLE lines: comment lines dropped, so prose that names a command cannot satisfy
    /// (or break) a claim about the code. The repository's own convention for source-text guards.</summary>
    private static string Code(string relativePath)
        => string.Join("\n", Source(relativePath)
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

    // ---- 1. the policy: every branch, executed ----

    /// <summary>THE FOUR STATES A COVERED NODE CAN BE IN, one character per node in
    /// <c>SmuMailboxAccess.Nodes</c> order: <c>g</c> = present and writable by this user (the grant is in place),
    /// <c>x</c> = present and NOT writable (the grant is missing), <c>-</c> = not on this machine at all.
    ///
    /// The rows are the whole rule: OFFER is "some covered node exists and is not writable", and the second row
    /// is the state the app was actually found in (root-only attributes, every file in /etc byte-identical to the
    /// bundled one), the fourth and fifth are the trap (absence must never be read as a lost grant — a Dell, or an
    /// Acer without the driver, would otherwise be told to install permissions for a module it does not have), and
    /// the rest put a missing node beside a real one in both directions.
    ///
    /// MUTATION-VERIFIED, one at a time, and each reddens a different part of the table: the predicate cut to
    /// <c>!nodeWritable(path)</c> alone reddens the two ABSENT rows (<c>----</c>, <c>g-g-</c>); returning
    /// <c>true</c> at the first node reddens the rows whose lost node is not the first (<c>gxxx</c>,
    /// <c>g--x</c>); returning <c>true</c> unconditionally reddens the GRANTED row <c>gggg</c> as well.</summary>
    [Theory]
    [InlineData("gggg", false)]   // everything granted: silent, which is what keeps the banner away
    [InlineData("xxxg", true)]    // the measured failure: the attributes are there and root-only
    [InlineData("gxxx", true)]    // ...and any one of them being lost is enough
    [InlineData("----", false)]   // no driver here at all: NOT an offer (the trap)
    [InlineData("g-g-", false)]   // absent nodes are ignored; the rest is still granted
    [InlineData("x---", true)]    // mixed, one lost one present, two absent: an offer
    [InlineData("g--x", true)]    // mixed the other way round: an offer
    public void TheOfferFollowsTheGrantAndNeverAnAbsentNode(string states, bool offer)
    {
        var paths = SmuMailboxAccess.NodePaths.ToArray();
        Assert.Equal(paths.Length, states.Length);   // one character per covered node, no more and no fewer

        var present = new HashSet<string>(StringComparer.Ordinal);
        var writable = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < states.Length; i++)
        {
            if (states[i] == '-') continue;
            present.Add(paths[i]);
            if (states[i] == 'g') writable.Add(paths[i]);
        }

        Assert.Equal(offer, SmuMailboxAccess.GrantMissing(present.Contains, writable.Contains));
    }

    /// <summary>THE ORDERING INSIDE THE RULE, and it is what makes the trap above structural rather than a
    /// property of whatever <c>nodeWritable</c> happens to answer: a node that does not exist is never asked
    /// about. This matters because the delegate a real caller passes is a permission PROBE, and a probe on a
    /// missing file reports "cannot write" — the answer that would turn every Dell into an offer.
    ///
    /// Mutation that reddens it: swap the condition to <c>!nodeWritable(path) &amp;&amp; nodeExists(path)</c> (the
    /// probe is then asked about absent nodes, and the assertion on <paramref name="asked"/> fails).</summary>
    [Fact]
    public void AnAbsentNodeIsNeverEvenAskedAboutItsWritability()
    {
        var asked = new List<string>();

        var offer = SmuMailboxAccess.GrantMissing(
            nodeExists: _ => false,
            nodeWritable: path => { asked.Add(path); return false; });

        Assert.False(offer);
        Assert.Empty(asked);
    }

    /// <summary>The list is the driver's whole writable surface, and the app's own port gates on <c>smn</c> alone
    /// — so the list must contain it and must stay names, not paths: the two packaging files write the directory
    /// once and the names inside their loop, and a list that carried absolute paths would not match what the
    /// guards below look for.
    ///
    /// Mutation that reddens it, run: drop <c>smn</c> from the constant (this row, <see cref="TheOfferFollowsTheGrantAndNeverAnAbsentNode"/>
    /// and the two naming rows below all red — it is the one edit that breaks the list and every copy of it at
    /// once).</summary>
    [Fact]
    public void TheCoveredNodesAreTheDriversWritableAttributesByName()
    {
        Assert.Contains("smn", SmuMailboxAccess.Nodes);
        Assert.All(SmuMailboxAccess.Nodes, node => Assert.DoesNotContain("/", node, StringComparison.Ordinal));
        Assert.All(SmuMailboxAccess.NodePaths,
                   path => Assert.StartsWith(SmuMailboxAccess.DriverDir + "/", path, StringComparison.Ordinal));
    }

    // ---- 2. the wait in the udev rule ----

    /// <summary>THE BIND RULE WAITS BEFORE IT GRANTS, and the rows are the pieces of that wait: the event it
    /// still hangs off (kept — it covers a manual module reload), the file it waits FOR (the one node the app
    /// gates on), the step size and the bound that make the wait finite (~5 s: 50 × 0.1 s), and the udev escaping
    /// the file's own note demands (<c>$$i</c>/<c>$$((…))</c>; a bare <c>$i</c> is an unknown substitution and
    /// udev rejects the whole rule).
    ///
    /// WHY THE WAIT IS THERE AT ALL: the bind event does not promise the driver's attribute files exist yet, and
    /// every chgrp below fails into <c>2>/dev/null</c> — silently, which is how a rule that looks installed can
    /// grant nothing. It is NOT the boot grant (the module comes from the initramfs here, so no bind reaches a
    /// running udev) — that is the unit, guarded below.
    ///
    /// MUTATION-VERIFIED, one at a time: deleting the wait reddens the four wait rows together (the file it waits
    /// for, the step size, the bound and the counter's escaping); writing <c>$i</c> for <c>$$i</c> reddens the
    /// bound row ALONE; and changing <c>ACTION=="bind"</c> to <c>"add|change"</c> reddens the event row alone —
    /// that last one is a decision to state rather than a tidy-up, which the rule's own comment argues.</summary>
    [Theory]
    [InlineData("ACTION==\"bind\", SUBSYSTEM==\"pci\", DRIVER==\"ryzen_smu\"")]   // the event, kept
    [InlineData("[ ! -e /sys/kernel/ryzen_smu_drv/smn ]")]                        // what it waits for
    [InlineData("sleep 0.1")]                                                    // in small steps
    [InlineData("[ $$i -lt 50 ]")]                                               // bounded: 50 × 0.1 s ≈ 5 s
    [InlineData("$$((i+1))")]                                                    // escaped for udev, as the note requires
    [InlineData("for f in smu_args mp1_smu_cmd rsmu_cmd smn")]                   // and then the grant, unchanged
    [InlineData("chgrp wheel /sys/kernel/ryzen_smu_drv/$$f")]                    // to the group, by absolute path
    [InlineData("must not be rewritten as a substitution")]                      // the %p note this rule still rests on
    public void TheBindRuleWaitsForTheAttributesAndThenGrantsThem(string required)
        => Assert.Contains(required, Source("packaging/60-acer-helper.rules"), StringComparison.Ordinal);

    // ---- 3. the unit: the deterministic boot grant ----

    /// <summary>THE UNIT'S SHAPE, one row per property that is load-bearing, and every one of them is a way the
    /// unit silently stops being the boot grant rather than a style: <c>After=systemd-modules-load.service</c> is
    /// the ordering that makes it run after the module (the entire reason the udev rule cannot do this job);
    /// <c>ConditionPathExists</c> is what keeps it a no-op on a machine without the driver instead of a failed
    /// unit at every boot; <c>Type=oneshot</c> with <c>RemainAfterExit=yes</c> is what makes a finished run count
    /// as active (a unit that goes inactive would be re-run by nothing and reported as failed); and
    /// <c>WantedBy=multi-user.target</c> is what <c>systemctl enable</c> writes the symlink for — without it the
    /// unit installs and never runs again after the install itself.
    ///
    /// MUTATION-VERIFIED, one at a time, and each reddened its own row and nothing else: <c>After=</c> deleted,
    /// <c>ConditionPathExists=</c> commented out, <c>Type=oneshot</c> changed to <c>Type=exec</c>,
    /// <c>RemainAfterExit=no</c>, and <c>WantedBy=graphical.target</c> in place of <c>multi-user.target</c>. The
    /// two SECTION rows are not separately mutated: they are the same Contains claim over the same file, and a
    /// deleted <c>[Install]</c> takes the <c>WantedBy=</c> that was checked into the same failure.</summary>
    [Theory]
    [InlineData("[Unit]")]
    [InlineData("After=systemd-modules-load.service")]
    [InlineData("ConditionPathExists=/sys/kernel/ryzen_smu_drv")]
    [InlineData("Type=oneshot")]
    [InlineData("RemainAfterExit=yes")]
    [InlineData("[Install]")]
    [InlineData("WantedBy=multi-user.target")]
    public void TheUnitRunsAfterTheModuleOnEveryBoot(string required)
        => Assert.Contains(required, Source("packaging/acer-helper-smu-perms.service"), StringComparison.Ordinal);

    /// <summary>WHAT THE UNIT DOES, and the escaping is a row rather than a detail: systemd expands <c>$VAR</c>
    /// in <c>ExecStart</c> from the service's own environment, and an UNDEFINED name becomes EMPTY — a bare
    /// <c>$f</c> would silently chgrp the driver's DIRECTORY instead of the four files, which is a grant that
    /// succeeds, changes nothing and looks installed. <c>$$f</c> is systemd's escape for a literal <c>$</c>.
    ///
    /// MUTATION-VERIFIED, one at a time: writing <c>$f</c> for <c>$$f</c> reddens the dollar assertion (and only
    /// it — the chgrp/chmod/loop assertions still pass, which is exactly why that assertion exists: the broken
    /// form LOOKS right); dropping <c>smn</c> from the unit's loop reddens the loop assertion. The
    /// <c>chgrp</c>/<c>chmod</c> rows are the same Contains claim over the same two lines and were not mutated
    /// separately — deleting either command deletes the line the loop assertion also reads.</summary>
    [Fact]
    public void TheUnitGrantsTheGroupWriteOnEveryCoveredAttribute()
    {
        var unit = Code("packaging/acer-helper-smu-perms.service");

        Assert.Contains("chgrp wheel", unit, StringComparison.Ordinal);
        Assert.Contains("chmod g+w", unit, StringComparison.Ordinal);
        Assert.Contains("for f in " + string.Join(" ", SmuMailboxAccess.Nodes), unit, StringComparison.Ordinal);

        // Every "$…f" in the unit's executable text is the DOUBLED form: a single one is the empty-environment
        // expansion above. Read by pattern rather than by searching for the literal, because "$$f" contains "$f".
        var dollarForms = Regex.Matches(unit, @"\$+f").Select(m => m.Value).Distinct().ToArray();
        Assert.Equal(["$$f"], dollarForms);
    }

    /// <summary>ONE LIST, THREE COPIES, AND THE SUITE IS WHAT KEEPS THEM EQUAL. The four names are the driver's
    /// whole writable surface, they are granted by a udev rule and by a unit, and the C# constant is the copy the
    /// suite can read — so the naming is asserted FROM the constant, node by node, rather than from a list
    /// restated here (a fifth attribute added to the constant and granted nowhere reddens this row).
    ///
    /// THE COMMENTS ARE DROPPED FIRST, and that is not tidiness: both packaging files explain the grant in prose
    /// and name every attribute while doing it, so a check over the whole file passes while the command that
    /// grants one of them is gone — the exact "looks installed, grants nothing" failure this whole change is
    /// about. Measured, not assumed: the un-dropped version of this row stayed GREEN when the unit's loop lost
    /// <c>smn</c>, because the header comment still names it.
    ///
    /// WHAT THIS ROW IS AND IS NOT. It is a PRESENCE claim over the executable text — a node that vanished from a
    /// file altogether (or a fifth node added to the constant and to no file) reddens it, and dropping a node
    /// from the UNIT's loop reddens it too, because that loop is the only thing in the unit that names one. It is
    /// not a claim about the udev rule's LOOP: that rule's RUN also names <c>smn</c> in the wait clause, so
    /// removing the node from the loop alone leaves it green here. That mutation's symptom is the rule's own
    /// grant row — <c>TheBindRuleWaitsForTheAttributesAndThenGrantsThem</c>, which pins the loop as one whole
    /// clause — and the two rows are deliberately different claims rather than one twice.
    ///
    /// MUTATION-VERIFIED, one at a time: removing a node from the UNIT's loop reddens this row (and the unit's
    /// loop assertion above); removing a node from the UDEV RULE's loop does not — see the paragraph above for
    /// where that one reddens instead; and dropping a node from <c>SmuMailboxAccess.Nodes</c> reddens this row
    /// from the other direction, because the constant and the two files are then two different lists.</summary>
    [Fact]
    public void TheCoveredNodesAreNamedByBothPackagingFiles()
    {
        var rules = Code("packaging/60-acer-helper.rules");
        var unit = Code("packaging/acer-helper-smu-perms.service");

        Assert.Equal(4, SmuMailboxAccess.Nodes.Length);
        Assert.Contains(SmuMailboxAccess.DriverDir, rules, StringComparison.Ordinal);
        Assert.Contains(SmuMailboxAccess.DriverDir, unit, StringComparison.Ordinal);

        foreach (var node in SmuMailboxAccess.Nodes)
        {
            Assert.Contains(node, rules, StringComparison.Ordinal);
            Assert.Contains(node, unit, StringComparison.Ordinal);
        }
    }


    // ---- 4. the installer: one mechanism, both cases ----

    /// <summary>THE INSTALLER ENABLES THE UNIT INSTEAD OF CHMODDING THE NODES ITSELF, and this is the row that
    /// decides whether the grant survives a boot at all: the inline <c>chgrp</c>/<c>chmod</c> the script used to
    /// carry could only ever fix the machine it ran on, while <c>systemctl enable</c> + <c>systemctl restart</c>
    /// covers the immediate case (restart runs the unit whether or not it is active) AND the boot case (the enable
    /// symlink). <c>enable --now</c> would cover the FIRST install only: the unit is <c>Type=oneshot</c> with
    /// <c>RemainAfterExit=yes</c>, so it counts as active once it has run, and a grant lost again in the same boot
    /// — a driver reload recreating the attributes root-only is how that happened once — would be re-offered by
    /// the app and not re-applied by the install. Both halves are pinned by position inside the executable text —
    /// comments are dropped first, so the paragraphs explaining the change cannot satisfy it: the unit is loaded
    /// and started after the tmpfiles pass and BEFORE the module reload, which stays last.
    ///
    /// The unit's NAME is a constant in that file, so the row pins the constant and the command separately — and
    /// pins that the inline chmod is gone rather than merely joined, because a leftover chgrp would put the grant
    /// back in a script that no longer owns it.
    ///
    /// MUTATION-VERIFIED, one at a time: the inline <c>chgrp … /sys/kernel/ryzen_smu_drv/smn</c> put back into
    /// the chain reddens the "inline grant is gone" assertion, and moving the step to after the module reload
    /// (appending it where the reload's <c>script += </c> is) reddens the position assertion. The installed-path
    /// row is the same Contains claim over the same table and was not mutated separately.</summary>
    [Fact]
    public void TheInstallScriptEnablesTheUnitInsteadOfGrantingTheNodesItself()
    {
        var script = Code("Infrastructure/HardwareAccess.cs");

        Assert.Contains("systemctl daemon-reload", script, StringComparison.Ordinal);
        Assert.Contains("systemctl enable ", script, StringComparison.Ordinal);
        Assert.Contains("systemctl restart ", script, StringComparison.Ordinal);
        Assert.Contains("const string SmuPermissionsUnit = \"acer-helper-smu-perms.service\"", script, StringComparison.Ordinal);

        // The unit has to be where systemd looks, and it has to be the SAME name the const enables.
        Assert.Contains(
            "\"/etc/systemd/system/acer-helper-smu-perms.service\"",
            script, StringComparison.Ordinal);

        // The inline grant is GONE, not merely beside the new step.
        Assert.DoesNotContain("chgrp ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod g+w", script, StringComparison.Ordinal);

        // ...and the ordering the script's own remarks promise, asserted by position. The unit is located by its
        // USE SITE in the chain, not by the command text: the command also appears in the step's own definition
        // near the top of the file, and a position taken from there would say nothing about the order.
        var tmpfiles = script.IndexOf("systemd-tmpfiles --create", StringComparison.Ordinal);
        var unit = script.IndexOf("+ SmuPermissionsStep", StringComparison.Ordinal);
        var reload = script.IndexOf("modprobe -r acer_wmi", StringComparison.Ordinal);

        Assert.True(tmpfiles >= 0 && unit > tmpfiles && reload > unit,
                    "the SMU unit must be started after the tmpfiles pass and before the module reload, which stays last");
    }

    /// <summary>AND THE UNIT REACHES BOTH PLACES THE INSTALL CAN BE RUN FROM: the installer's bundled table (so
    /// "Grant hardware access" knows the file exists and installs it) and the AppImage's own layout, which
    /// copies the packaging files into the AppDir — where <c>HardwareAccess</c> reads them from at runtime. The
    /// second one fails louder than it looks: <c>RulesNeeded()</c> returns false when a bundled file is MISSING
    /// from AppContext.BaseDirectory, so an AppImage without the unit loses the whole offer, not just this file.
    ///
    /// MUTATION-VERIFIED, one at a time: dropping the installer's table entry reddens this row (with
    /// <c>HardwareAccessWiringTests.EveryPackagingPermissionFileIsInTheInstallersBundledList</c>, which is the
    /// drift guard on the same omission), and dropping the <c>packaging/acer-helper-smu-perms.service</c> argument
    /// from the AppImage <c>cp</c> reddens it alone.</summary>
    [Fact]
    public void TheUnitIsBundledAndReachesTheAppImage()
    {
        Assert.Contains("acer-helper-smu-perms.service", HardwareAccess.ApplicableBundledNames(isAcer: true));
        Assert.Contains("packaging/acer-helper-smu-perms.service",
                        Source(".github/workflows/build.yml"), StringComparison.Ordinal);
    }
}
