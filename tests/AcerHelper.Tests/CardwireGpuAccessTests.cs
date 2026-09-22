using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE APP MAY NOT ASK A THIRD-PARTY DAEMON FOR ANYTHING IT WAS NOT GIVEN LEAVE TO ASK FOR, and this file holds
/// that to machine checks. What is being tested is not a port — it is the app's OWN request to see the machine's
/// discrete GPU, which cardwire is hiding from programs it has not allowed (docs/cardwire-gpu-access.md). Three
/// things make it worth a file of its own rather than a port-shaped test:
///
/// <list type="number">
/// <item>THE GATE. The capability must be invisible — no row, no prompt, no call — everywhere its premise does
/// not hold: another OS, no daemon on the bus, a machine with no discrete GPU, and above all a machine whose GPU
/// is ALREADY VISIBLE. That last one is the easy mistake: on a cardwire in Hybrid mode the daemon is running and
/// there is nothing to ask for, and an app that asked anyway would be writing a permission into a daemon it has
/// no business touching.</item>
/// <item>THE ONE CALL, exactly: the daemon, object and interface, the method, the signature, the policy word
/// (<c>"Allow_dGPU"</c> — the strings are case-sensitive), the pid, and the gpu id. A wrong word here is not a
/// refusal, it is a DIFFERENT permission, and a wrong signature is a call that never leaves the bus.</item>
/// <item>WHAT IS REMEMBERED. The consent is a preference in settings.json; the GRANT is not, and cannot be — it
/// belongs to one running process and cardwire has no revoke — so what this file pins is that a refused request
/// is never written down as granted, and that every start asks again.</item>
/// </list>
///
/// THE WORLD IS WRITTEN DOWN, not arranged: a port is built over four facts and a busctl recorder, so every
/// branch is reachable on a machine with no cardwire, no NVIDIA GPU and no /proc — including this one, where the
/// suite compiles the Windows TFM and the real host's port says "not Linux" (which is itself the gate test
/// below).
/// </summary>
public class CardwireGpuAccessTests
{
    // ---- the world, written down -------------------------------------------------------------------------

    /// <summary>The state the request exists for: Linux, cardwire on the system bus, a discrete NVIDIA GPU on the
    /// machine, and that GPU hidden from this process.</summary>
    private static CardwireGpuAccessFacts Hidden => new(IsLinux: true, CardwirePresent: true, DgpuPresent: true, DgpuVisible: false);

    /// <summary>A port over <paramref name="facts"/>, recording every argument list it is handed. The answer is
    /// the daemon's: exit 0 by default, i.e. the grant was accepted.</summary>
    private static (CardwireGpuAccessPort Port, List<string[]> Calls) Port(
        CardwireGpuAccessFacts facts, (int code, string output) answer = default, uint pid = 4242)
    {
        var calls = new List<string[]>();
        var port = new CardwireGpuAccessPort(() => facts, args => { calls.Add(args); return answer; }, () => pid);
        return (port, calls);
    }

    /// <summary>A fixture whose machine has cardwire, the GPU hidden, and a recorder — the arrangement every
    /// service-level test starts from.</summary>
    private static (LaptopServiceFixture Fixture, List<string[]> Calls) Gated(
        Settings? settings = null, (int code, string output) answer = default)
    {
        var (port, calls) = Port(Hidden, answer);
        return (new LaptopServiceFixture(settings, cardwireGpuAccess: port), calls);
    }

    // ---- the gate ----------------------------------------------------------------------------------------

    /// <summary>THE OFFER REQUIRES ALL FOUR FACTS, and each of the four shut cases gets its own row because they
    /// are four different mistakes with the same symptom: an app that asks another OS's daemon, one that asks a
    /// daemon that is not there, one that asks for a GPU the machine does not have, and one that asks for a
    /// permission it already has.</summary>
    [Theory]
    [InlineData(true, true, true, false, true, "the state the request exists for")]
    [InlineData(false, true, true, false, false, "not Linux: cardwire is a Linux-side daemon")]
    [InlineData(true, false, true, false, false, "the service is not on the system bus")]
    [InlineData(true, true, false, false, false, "the machine has no discrete GPU")]
    [InlineData(true, true, true, true, false, "the GPU is already visible to us")]
    public void TheOfferExistsOnlyWhereThereIsSomethingToAskFor(
        bool isLinux, bool cardwirePresent, bool dgpuPresent, bool dgpuVisible, bool offered, string why)
    {
        var facts = new CardwireGpuAccessFacts(isLinux, cardwirePresent, dgpuPresent, dgpuVisible);

        Assert.Equal(offered, CardwireGpuAccess.ShouldOffer(facts));
        // A shut gate ALWAYS has a reason and an open one never does — the row's refusal and the startup report
        // are built from it, so a silent shut gate would be a refusal with nothing to say.
        Assert.Equal(offered, CardwireGpuAccess.ShutReason(facts) == null);
        _ = why;
    }

    /// <summary>The gate is ONE rule, and the three readers of it are three readings of the same answer —
    /// the row's offer, the run's duty, and the request's own check. A second expression anywhere here is how a
    /// gate ends up enforced in the Options drawer but not in the call.</summary>
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void TheGateIsOneRuleReadThreeWays(bool isLinux, bool cardwirePresent, bool dgpuPresent, bool dgpuVisible)
    {
        var facts = new CardwireGpuAccessFacts(isLinux, cardwirePresent, dgpuPresent, dgpuVisible);
        var offered = CardwireGpuAccess.ShouldOffer(facts);

        Assert.Equal(offered, CardwireGpuAccess.DutyFor(facts, enabled: true) == CardwireGpuAccessDuty.Ask);
        Assert.Equal(offered, CardwireGpuAccess.RowBelongs(facts, enabled: false));
        Assert.True(CardwireGpuAccess.RowBelongs(facts, enabled: true));   // a choice already made stays reachable
    }

    /// <summary>THE ONE DUTY THAT SPEAKS: the user asked and the daemon is gone. Every other shut gate is silence
    /// — and the machine whose GPU is already visible is the important one to keep quiet, because that is the
    /// ordinary state of a cardwire in Hybrid mode and a message about it would be noise on every start.</summary>
    [Fact]
    public void OnlyTheMissingDaemonIsWorthAWord()
    {
        Assert.Equal(CardwireGpuAccessDuty.ReportMissingService,
                     CardwireGpuAccess.DutyFor(Hidden with { CardwirePresent = false }, enabled: true));
        Assert.Equal(CardwireGpuAccessDuty.None,
                     CardwireGpuAccess.DutyFor(Hidden with { DgpuVisible = true }, enabled: true));
        Assert.Equal(CardwireGpuAccessDuty.None,
                     CardwireGpuAccess.DutyFor(Hidden with { DgpuPresent = false }, enabled: true));
        Assert.Equal(CardwireGpuAccessDuty.None,
                     CardwireGpuAccess.DutyFor(Hidden with { IsLinux = false }, enabled: true));
        // ...and the choice being off silences every one of them, whatever the machine looks like.
        Assert.Equal(CardwireGpuAccessDuty.None, CardwireGpuAccess.DutyFor(Hidden, enabled: false));
        Assert.Equal(CardwireGpuAccessDuty.None,
                     CardwireGpuAccess.DutyFor(Hidden with { CardwirePresent = false }, enabled: false));
    }

    // ---- the one call ------------------------------------------------------------------------------------

    /// <summary>THE EXACT CALL, spelled out rather than assembled from the constants — a test that reads its own
    /// expectations out of the code under test cannot see a renamed interface or a lowercased policy word, and
    /// both of those are calls that would silently succeed at nothing (the daemon's policy strings are
    /// case-sensitive; <c>"Default"</c> is a documented no-op, so a typo is a no-op, not an error).</summary>
    [Fact]
    public void TheRequestIsThisExactCall()
    {
        Assert.Equal(
            ["call",
             "org.opengamingcollective.cardwire",
             "/org/opengamingcollective/cardwire",
             "org.opengamingcollective.cardwire.SmartPolicy",
             "RequestProcessAccess",
             "usu",
             "4242",
             "Allow_dGPU",
             "0"],
            CardwireGpuAccess.RequestArguments(4242));

        // The signature is passed EXPLICITLY because busctl's inference refuses this call ("Too many parameters
        // for signature", measured on the owner's machine): the bare form is the failure this element prevents.
        Assert.Equal("usu", CardwireGpuAccess.RequestArguments(1)[5]);

        // The SYSTEM bus is not in this list because Busctl.Call prefixes --system itself (its one call site
        // shape, PowerProfiles.Linux.cs). Asserted here so a future caller reaching for the bus directly has to
        // notice: without it the call goes to the session bus, where nobody owns the name.
        Assert.DoesNotContain("--system", CardwireGpuAccess.RequestArguments(1));
    }

    /// <summary>The pid in the argument list is the one the port was asked about — the grant is inserted FOR a
    /// pid, so a wrong one grants a stranger (or nothing at all).</summary>
    [Fact]
    public void ARequestCarriesThePidItWasGiven()
    {
        var (port, calls) = Port(Hidden, pid: 987654);

        Assert.True(port.Request().ok);

        var args = Assert.Single(calls);
        Assert.Equal("987654", args[^3]);
        Assert.Equal("Allow_dGPU", args[^2]);
    }

    /// <summary>NOTHING IS CALLED WHERE THERE IS NOTHING TO ASK FOR — the recorder is the point, not the return
    /// value: an app that called the daemon and merely reported the refusal would look identical from the
    /// outside.</summary>
    [Theory]
    [InlineData(false, true, true, false)]   // not Linux
    [InlineData(true, false, true, false)]   // no daemon on the bus
    [InlineData(true, true, false, false)]   // no discrete GPU
    [InlineData(true, true, true, true)]     // already visible
    public void NothingIsCalledWhereThereIsNothingToAskFor(bool isLinux, bool cardwirePresent, bool dgpuPresent, bool dgpuVisible)
    {
        var facts = new CardwireGpuAccessFacts(isLinux, cardwirePresent, dgpuPresent, dgpuVisible);
        var (port, calls) = Port(facts);

        var (ok, error) = port.Request();

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));   // a refusal says why, even when nothing was sent
        Assert.Empty(calls);
    }

    /// <summary>A REFUSAL KEEPS THE DAEMON'S OWN WORDS and is not retried: one call, one answer, both halves of
    /// the outcome handed back. cardwire answers "process doesn't exist" for a pid it cannot see and the bus
    /// answers for a permission it will not grant; neither is this app's to reword.</summary>
    [Fact]
    public void ARefusalKeepsTheDaemonsWords()
    {
        var (port, calls) = Port(Hidden, answer: (1, "Call failed: Access denied\n"));

        var (ok, error) = port.Request();

        Assert.False(ok);
        Assert.Equal("Call failed: Access denied", error);   // trimmed and on one line, for the status line
        Assert.Single(calls);                                // and never retried
    }

    /// <summary>A silent failure still says something: an exit code with no output is what a killed or missing
    /// busctl looks like, and "failed" with no reason is the message this guard exists to prevent.</summary>
    [Fact]
    public void ARunnerThatSaysNothingStillReportsTheCode()
    {
        var (port, _) = Port(Hidden, answer: (127, ""));

        var (ok, error) = port.Request();

        Assert.False(ok);
        Assert.Equal("busctl exited with 127", error);
    }

    // ---- what is remembered ------------------------------------------------------------------------------

    /// <summary>ON ASKS FIRST AND REMEMBERS WHAT HAPPENED: the grant is made, the one call is made, and only then
    /// does the preference reach settings.json — the same contract as applying a declared setting
    /// (<c>Application/DeclaredSetting.cs</c>), and for the same reason (a file that says a permission exists
    /// while the daemon refused is a preference the app cannot keep).</summary>
    [Fact]
    public void TurningItOnAsksFirstAndRemembersWhatHappened()
    {
        var (fixture, calls) = Gated();

        var (ok, error) = fixture.Service.SetCardwireGpuAccess(true);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(calls);
        Assert.True(fixture.Service.CardwireGpuAccessEnabled);
        Assert.True(fixture.Store.Settings.CardwireGpuAccess);
        Assert.Equal(1, fixture.Store.SaveCount);   // remembered, not only held
    }

    /// <summary>A REFUSED REQUEST IS NOT REMEMBERED — the row snaps back, and settings.json never claims an
    /// access the daemon did not grant. The daemon's words travel out unchanged for the row's own failure
    /// path.</summary>
    [Fact]
    public void ARefusedRequestIsNotRemembered()
    {
        var (fixture, calls) = Gated(answer: (1, "Call failed: Access denied"));

        var (ok, error) = fixture.Service.SetCardwireGpuAccess(true);

        Assert.False(ok);
        Assert.Equal("Call failed: Access denied", error);
        Assert.Single(calls);
        Assert.False(fixture.Service.CardwireGpuAccessEnabled);
        Assert.False(fixture.Store.Settings.CardwireGpuAccess);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>OFF FORGETS THE CHOICE AND CALLS NOTHING. It cannot revoke the grant — cardwire has no such
    /// method, and the consent prompt says so before the user agrees — so the honest behaviour is to stop asking
    /// on the next start and to leave the running grant alone, which is exactly this: a remembered choice
    /// cleared, and not one call.</summary>
    [Fact]
    public void TurningItOffForgetsTheChoiceAndCallsNothing()
    {
        var (fixture, calls) = Gated(new Settings { CardwireGpuAccess = true });

        var (ok, error) = fixture.Service.SetCardwireGpuAccess(false);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(calls);                        // there is no un-ask to make
        Assert.False(fixture.Service.CardwireGpuAccessEnabled);
        Assert.False(fixture.Store.Settings.CardwireGpuAccess);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    /// <summary>EVERY START ASKS AGAIN while the choice is on — the grant died with the previous process, so the
    /// consent is a standing instruction to ask, not a permission that is being restored.</summary>
    [Fact]
    public void EveryStartAsksAgainWhileTheChoiceIsOn()
    {
        var (fixture, calls) = Gated(new Settings { CardwireGpuAccess = true });

        var (ok, error) = fixture.Service.ApplyCardwireGpuAccess();

        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(calls);
        Assert.Equal("Allow_dGPU", calls[0][^2]);
    }

    /// <summary>A RUN THAT NEVER CONSENTED TOUCHES NOTHING: the default is off, and off means no probe is acted
    /// on, no call is made, and nothing is reported — on every machine, whatever cardwire is doing.</summary>
    [Fact]
    public void ARunThatNeverConsentedTouchesNothing()
    {
        var (fixture, calls) = Gated();   // no settings: the default, off

        var (ok, error) = fixture.Service.ApplyCardwireGpuAccess();

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(calls);
    }

    /// <summary>THE MISSING DAEMON IS REPORTED, and not called: the user's choice has stopped being honoured,
    /// which is the one shut gate worth a word — but there is nothing to send, so nothing is sent.</summary>
    [Fact]
    public void StartupReportsTheDaemonItCannotFind()
    {
        var (port, calls) = Port(Hidden with { CardwirePresent = false });
        var fixture = new LaptopServiceFixture(new Settings { CardwireGpuAccess = true }, cardwireGpuAccess: port);

        var (ok, error) = fixture.Service.ApplyCardwireGpuAccess();

        Assert.False(ok);
        Assert.Equal("cardwire is not on the system bus", error);
        Assert.Empty(calls);
    }

    /// <summary>AN ALREADY-VISIBLE GPU IS NOT A FAILURE and does not become one at startup: the choice stays
    /// remembered (it is the user's), no call is made, and nothing is said — the ordinary state of a cardwire in
    /// Hybrid mode.</summary>
    [Fact]
    public void StartupIsSilentWhereTheGpuIsAlreadyVisible()
    {
        var (port, calls) = Port(Hidden with { DgpuVisible = true });
        var fixture = new LaptopServiceFixture(new Settings { CardwireGpuAccess = true }, cardwireGpuAccess: port);

        var (ok, error) = fixture.Service.ApplyCardwireGpuAccess();

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(calls);
        Assert.True(fixture.Service.CardwireGpuAccessEnabled);   // the choice is the user's, and it stays
    }

    // ---- the Options row ---------------------------------------------------------------------------------

    /// <summary>THE ROW EXISTS EXACTLY WHERE THE GATE SAYS SO, and the absence is asserted rather than left to
    /// read: on this build — the Windows TFM, where the real host's port says "not Linux" — the drawer must not
    /// carry the row at all, and a machine with something to ask for must.</summary>
    [Fact]
    public void TheOptionsRowAppearsOnlyWhereThereIsSomethingToAskFor()
    {
        Assert.False(HasRow(new OptionsAssemblerHarness()));   // the real host, on this build: nothing to ask for

        var (gated, _) = Gated();
        Assert.True(HasRow(new OptionsAssemblerHarness(gated)));

        // ...and a machine that already sees its GPU offers nothing, with or without a daemon on the bus.
        var (visible, _) = Port(Hidden with { DgpuVisible = true });
        Assert.False(HasRow(new OptionsAssemblerHarness(
            new LaptopServiceFixture(cardwireGpuAccess: visible))));

        static bool HasRow(OptionsAssemblerHarness h)
            => AssemblerRows.Toggles(h).Any(t => t.Label == Loc.T("Use the discrete GPU"));
    }

    /// <summary>THE ROW SURVIVES ITS OWN OFFER, so a choice made while the GPU was hidden can be turned off
    /// after the machine stops hiding it — the alternative leaves a standing consent with no switch.</summary>
    [Fact]
    public void TheRowStaysReachableAfterTheOfferIsGone()
    {
        var (port, _) = Port(Hidden with { DgpuVisible = true });   // the offer is gone
        var fixture = new LaptopServiceFixture(new Settings { CardwireGpuAccess = true }, cardwireGpuAccess: port);

        Assert.Contains(Loc.T("Use the discrete GPU"), AssemblerRows.Labels(new OptionsAssemblerHarness(fixture)));
    }

    /// <summary>THE ROW CARRIES THE CONSENT PROMPT — the same delegate the AppController hands in, not a second
    /// dialog or an inline question. This is the whole of the "nothing is granted silently" rule at the row:
    /// the toggle's async veto is what shows it, and it must be present on THIS row.</summary>
    [Fact]
    public void TheRowCarriesTheConsentPrompt()
    {
        var (fixture, calls) = Gated();
        var asked = 0;
        var h = new OptionsAssemblerHarness(fixture, confirmGpuAccess: () => { asked++; return Task.FromResult(true); });

        var row = AssemblerRows.Toggle(h, "Use the discrete GPU");
        Assert.NotNull(row.ConfirmAsync);

        var vm = new ToggleRowViewModel(row, post: Eventually.Sync);
        vm.IsOn = true;

        Assert.True(Eventually.Until(() => asked == 1), "the consent prompt was never shown");
        Assert.True(Eventually.Until(() => vm.IsOn), "the confirmed switch never settled on");
        // The write runs on the row's own serial worker, so the call is a moment behind the switch — waited for
        // rather than assumed, and asserted against the recorder so a second call would redden it.
        Assert.True(Eventually.Until(() => calls.Count == 1), "the confirmed row never asked for the grant");
    }

    /// <summary>DECLINING ASKS FOR NOTHING. The switch shows ON optimistically while the dialog is up, and this
    /// is the other side of that: no call, no remembered consent, and the switch back off — the veto the owner's
    /// rule requires, held at the point where it can actually be bypassed.</summary>
    [Fact]
    public void DecliningTheConsentAsksForNothing()
    {
        var (fixture, calls) = Gated();
        var asked = 0;
        var h = new OptionsAssemblerHarness(fixture, confirmGpuAccess: () => { asked++; return Task.FromResult(false); });

        var vm = new ToggleRowViewModel(AssemblerRows.Toggle(h, "Use the discrete GPU"), post: Eventually.Sync);
        vm.IsOn = true;

        Assert.True(Eventually.Until(() => asked == 1 && !vm.IsOn), "the declined switch never reverted");
        Assert.Empty(calls);
        Assert.False(fixture.Service.CardwireGpuAccessEnabled);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    // ---- the consent text --------------------------------------------------------------------------------

    /// <summary>EVERY SENTENCE THE PROMPT SHOWS IS A LOCALISATION KEY THAT EXISTS, checked against the Russian
    /// table and taken from the RENDERING rather than from a list kept here (with English active <c>Loc.T</c>
    /// returns the key itself, so whatever the prompt prints IS the key that has to be in the table). Two more
    /// strings the feature shows — the row's label and the status line the startup failure reports through — are
    /// held to the same table, because neither is reachable from the prompt.</summary>
    [Fact]
    public void EverySentenceThisFeatureShowsIsTranslated()
    {
        var paragraphs = CardwireGpuAccessConsent.Message()
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var keys = new[] { CardwireGpuAccessConsent.Title(), CardwireGpuAccessConsent.ConfirmText(),
                           "Use the discrete GPU", "GPU access failed" }
            .Concat(paragraphs)
            .ToArray();

        Assert.Equal(3, paragraphs.Length);   // the three sentences, none silently dropped by a bad join
        Assert.True(keys.Length >= 6, $"only {keys.Length} keys were collected — the extraction is probably broken");

        foreach (var key in keys)
            Assert.True(Strings.Ru.ContainsKey(key),
                        "this feature shows a sentence with no entry in Localization/Strings.Ru.cs, so the Russian "
                        + "build shows it in English:\n  " + key);
    }

    /// <summary>THE FOUR FACTS THE PROMPT OWES, pinned by position in the executable text rather than by prose
    /// about them: who is asked and for WHICH process, that the grant dies with the app, that it CANNOT be
    /// revoked while the app runs, and that every other application stays blocked. The third is the one a user
    /// would otherwise assume a switch can do — and the one this app cannot do for them — so its absence is the
    /// failure this test exists for.</summary>
    [Theory]
    [InlineData("THIS process", "the process the grant belongs to is not named")]
    [InlineData("Nothing is written to disk", "the prompt does not say that nothing is installed")]
    [InlineData("disappears the moment Acer Helper exits", "the grant's lifetime is not stated")]
    [InlineData("cannot be revoked while the app runs", "the missing revoke is not stated")]
    [InlineData("Every other application stays blocked", "the per-client scope is not stated")]
    public void ThePromptStatesWhatCannotBeUndone(string phrase, string why)
    {
        Assert.True(CardwireGpuAccessConsent.Message().Contains(phrase, StringComparison.Ordinal), why);
    }

    /// <summary>The confirm button is NOT the install prompts' word: nothing is installed here, and the two
    /// prompts sit one drawer apart. Asserting the literal keeps that a decision rather than a copy-paste.</summary>
    [Fact]
    public void TheConfirmWordIsNotInstall()
    {
        Assert.Equal("Allow", CardwireGpuAccessConsent.ConfirmText());
        Assert.NotEqual(Loc.T("Install"), CardwireGpuAccessConsent.ConfirmText());
    }
}
