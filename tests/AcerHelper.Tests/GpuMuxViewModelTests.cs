using AcerHelper.Domain;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// The shared GPU-mode card. It exists so the MUX's dangerous semantics are visible rather than hidden behind a
/// toggle: the card shows the CURRENT mode in ONE stable line, marks a queued change with a trailing '*', and a
/// note next to the selector explains the mark while the value differs from the original. THERE IS NO APPLY
/// BUTTON — choosing a different mode in the selector runs the confirmation (with the restart + black-screen
/// warning) and queues it; cancelling reverts the selection and queues nothing.
/// </summary>
public class GpuMuxViewModelTests
{
    private sealed class FakeMux : IGpuMux
    {
        public string? LastError => null;
        public bool Supported { get; init; } = true;
        public IReadOnlyList<ChoiceOption> Modes { get; init; } =
            [new("0", GpuMuxMessages.DiscreteMode), new("1", GpuMuxMessages.HybridMode)];
        public GpuMuxState State { get; set; } = new(null, null, false);
        public GpuMuxState Read() => State;
        public GpuMuxChange Result { get; set; } = new(true, true, null);
        public List<string> Requests { get; } = [];

        /// <summary>Record the request, and — like every real backend — remember the queued mode so the following
        /// <c>Read()</c> (the card's <c>Refresh</c>) reports it as pending. A no-op or a failure records nothing.</summary>
        public GpuMuxChange Request(string modeId)
        {
            Requests.Add(modeId);
            if (Result is { Ok: true, Queued: true })
            {
                var pending = Modes.FirstOrDefault(m => m.Id == modeId);
                State = new(State.Current, pending, true);
            }
            return Result;
        }
    }

    private static GpuMuxViewModel Vm(FakeMux mux, bool confirm, List<string>? warnings = null,
                                      List<string>? requested = null, List<string>? reported = null)
        => new(mux,
               key => { warnings?.Add(key); return Task.FromResult(confirm); },
               id => { requested?.Add(id); return mux.Request(id); },
               text => reported?.Add(text));

    // ---- the one-line render: '*' mark, note visibility, pending==current normalization ----

    /// <summary>A value that differs from the current one is a queued change: it marks the current-mode value
    /// with a trailing '*' on the SAME line (never a second line or an inline suffix) and shows the note next to
    /// the selector.</summary>
    [Fact]
    public void TheCardMarksAQueuedChangeWithAnAsterisk()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode),
                        new ChoiceOption("0", GpuMuxMessages.DiscreteMode), true),
        };
        var vm = Vm(mux, confirm: true);

        vm.Refresh();

        Assert.Equal(Loc.T("GPU mode: {0}", Loc.T(GpuMuxMessages.HybridMode)) + "*", vm.CurrentText);
        Assert.Equal(0, vm.SelectedIndex);   // the pending mode (Discrete) is preselected
        Assert.DoesNotContain('\n', vm.CurrentText);   // the mark is on the same line, never a second row
        Assert.DoesNotContain('\r', vm.CurrentText);
        Assert.True(vm.NoteVisible);                   // the value differs from the original -> note shown
    }

    /// <summary>A pending that EQUALS the current mode is not a change the next restart would make (a stale read,
    /// or firmware that already reports the request), so the card must not mark the value and must not show the
    /// note. The mode still preselects its own entry, but no reboot-required claim is made.</summary>
    [Fact]
    public void APendingEqualToTheCurrentModeAddsNoAsterisk()
    {
        var same = new ChoiceOption("1", GpuMuxMessages.HybridMode);
        var vm = Vm(new FakeMux { State = new(same, same, RebootRequired: true) }, confirm: true);

        vm.Refresh();

        Assert.Equal(Loc.T("GPU mode: {0}", Loc.T(GpuMuxMessages.HybridMode)), vm.CurrentText);
        Assert.DoesNotContain("*", vm.CurrentText);
        Assert.Equal(1, vm.SelectedIndex);
        Assert.False(vm.NoteVisible);
    }

    /// <summary>The displayed label is deliberately compact so the marked sentence fits the card's single
    /// non-wrapping line, and the note shown next to the selector is a stable, non-empty caption.</summary>
    [Fact]
    public void TheComposedLineUsesTheCompactLabelsAndFitsOneLine()
    {
        var vm = Vm(new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode),
                        new ChoiceOption("2", GpuMuxMessages.AutoMode), true),
        }, confirm: true);

        vm.Refresh();

        Assert.Equal("Hybrid", GpuMuxMessages.HybridMode);
        Assert.Equal("Discrete", GpuMuxMessages.DiscreteMode);
        Assert.Equal(Loc.T("GPU mode: {0}", Loc.T(GpuMuxMessages.HybridMode)) + "*", vm.CurrentText);
        Assert.DoesNotContain("(Optimus)", vm.CurrentText);
        Assert.DoesNotContain("dGPU only", vm.CurrentText);
        Assert.DoesNotContain('\n', vm.CurrentText);
        // The note is available and explains the mark while the value differs from the original.
        Assert.True(vm.NoteVisible);
        Assert.Equal(Loc.T(GpuMuxMessages.AfterRebootNote), vm.Note);
        Assert.Contains("*", vm.Note);
        Assert.False(string.IsNullOrWhiteSpace(vm.Note));
    }

    [Fact]
    public void WithNothingPendingTheLineHasNoMark()
    {
        var vm = Vm(new FakeMux { State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false) }, confirm: true);

        vm.Refresh();

        Assert.Equal(Loc.T("GPU mode: {0}", Loc.T(GpuMuxMessages.HybridMode)), vm.CurrentText);
        Assert.DoesNotContain("*", vm.CurrentText);
        Assert.False(vm.NoteVisible);
    }

    // ---- no Apply button: a selector change IS the request ----

    /// <summary>There is no Apply button: changing the selector to a different mode runs the confirmation and
    /// queues it. No command is invoked — the selection change alone drives the flow.</summary>
    [Fact]
    public void SelectingADifferentModeTriggersConfirmAndQueue()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false),
        };
        var requested = new List<string>();
        var warnings = new List<string>();
        var vm = Vm(mux, confirm: true, warnings, requested);
        vm.Refresh();                              // original Hybrid, selector on index 1
        Assert.Empty(warnings);

        vm.SelectedIndex = 0;                      // Discrete — the selector change IS the request

        Assert.Equal([GpuMuxMessages.Warning], warnings);
        Assert.Equal(["0"], requested);
        Assert.EndsWith("*", vm.CurrentText);
        Assert.True(vm.NoteVisible);
    }

    /// <summary>A real change is risky, so the confirmation gates it; declining queues NOTHING and REVERTS the
    /// selector to the mode actually in effect, so the card never shows an unqueued mode.</summary>
    [Fact]
    public void CancellingTheConfirmationRevertsTheSelectionAndQueuesNothing()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false),
        };
        var requested = new List<string>();
        var reported = new List<string>();
        var vm = Vm(mux, confirm: false, requested: requested, reported: reported);
        vm.Refresh();                              // original Hybrid, selector on index 1

        vm.SelectedIndex = 0;                      // user picks Discrete, then declines the warning

        Assert.Empty(requested);
        Assert.Empty(reported);
        Assert.Equal(1, vm.SelectedIndex);         // reverted to the mode in effect (Hybrid)
        Assert.DoesNotContain("*", vm.CurrentText);
        Assert.False(vm.NoteVisible);
    }

    /// <summary>The port is the authoritative no-op check: when the ViewModel could not tell (current unknown) it
    /// conservatively confirms, the port answers NoOp, so nothing is queued and neither the mark nor the note
    /// appears.</summary>
    [Fact]
    public void APortLevelNoOpIsReportedWithoutARestart()
    {
        var mux = new FakeMux
        {
            State = new(null, null, false),                     // current unknown to the VM
            Result = new(true, false, null, NoOp: true),        // the port proves it is a no-op
        };
        var reported = new List<string>();
        var vm = Vm(mux, confirm: true, reported: reported);
        vm.Refresh();                              // current unknown, selector on index 0

        vm.SelectedIndex = 1;                      // a selection change the port proves is a no-op

        Assert.Equal([Loc.T(GpuMuxMessages.AlreadyCurrent)], reported);
        Assert.Equal(Loc.T("GPU mode: unknown"), vm.CurrentText);
        Assert.DoesNotContain("*", vm.CurrentText);
        Assert.False(vm.NoteVisible);
    }

    /// <summary>A refused write reports the vendor's words and reverts the selector: nothing was queued, so the
    /// card must not keep showing the requested mode.</summary>
    [Fact]
    public void AFailedRequestReportsTheReasonAndRevertsTheSelection()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false),
            Result = new(false, false, "refused"),
        };
        var reported = new List<string>();
        var vm = Vm(mux, confirm: true, reported: reported);
        vm.Refresh();                              // original Hybrid, selector on index 1

        vm.SelectedIndex = 0;                      // Discrete -> the write is refused

        Assert.Equal(["refused"], reported);
        Assert.Equal(1, vm.SelectedIndex);         // reverted to the mode in effect
    }

    /// <summary>The ViewModel's own <see cref="GpuMuxViewModel.Refresh"/> sets the selector to the pending mode;
    /// that programmatic change must NOT be treated as a user request (no confirmation, no write).</summary>
    [Fact]
    public void RefreshDoesNotTreatItsOwnSelectionAsARequest()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode),
                        new ChoiceOption("0", GpuMuxMessages.DiscreteMode), true),
        };
        var warnings = new List<string>();
        var requested = new List<string>();
        var vm = Vm(mux, confirm: true, warnings, requested);

        vm.Refresh();                              // sets SelectedIndex itself (to the pending mode)

        Assert.Empty(warnings);
        Assert.Empty(requested);
    }

    // ---- the note's baseline rule ----

    /// <summary>The note appears only while the queued value differs from the ORIGINAL value captured when the
    /// card was first shown; reverting to the original hides it (no mark, no note).</summary>
    [Fact]
    public void TheNoteShowsOnlyWhileTheValueDiffersFromTheOriginal()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false),   // original: Hybrid
        };
        var vm = Vm(mux, confirm: true);
        vm.Refresh();                              // captures the original (Hybrid)
        Assert.False(vm.NoteVisible);

        vm.SelectedIndex = 0;                      // Discrete — differs from the original
        Assert.True(vm.NoteVisible);
        Assert.EndsWith("*", vm.CurrentText);

        vm.SelectedIndex = 1;                      // back to the original Hybrid (the mode in effect)
        Assert.False(vm.NoteVisible);
        Assert.DoesNotContain("*", vm.CurrentText);
    }

    /// <summary>The baseline is the ORIGINAL (first-populated) value, never the latest read: once the firmware
    /// reports the requested value (the ATK/Acer read-back), the note must still show, because the value differs
    /// from the original.</summary>
    [Fact]
    public void TheBaselineIsTheOriginalNotTheLatestRead()
    {
        var mux = new FakeMux
        {
            State = new(new ChoiceOption("1", GpuMuxMessages.HybridMode), null, false),   // original: Hybrid
        };
        var vm = Vm(mux, confirm: true);
        vm.Refresh();
        vm.SelectedIndex = 0;                      // Discrete

        // A later read (drawer reopen) now reports the requested mode with no separate pending: the note must
        // remain, because the value still differs from the ORIGINAL Hybrid.
        mux.State = new(new ChoiceOption("0", GpuMuxMessages.DiscreteMode), null, false);
        vm.Refresh();

        Assert.True(vm.NoteVisible);
        Assert.EndsWith("*", vm.CurrentText);
    }

    // ---- unsupported ----

    [Fact]
    public void AnUnsupportedPortStatesTheRefusalInTheOneLine()
    {
        var vm = Vm(new FakeMux { Supported = false, Modes = [] }, confirm: true);

        Assert.False(vm.Supported);
        Assert.False(vm.HasModes);
        Assert.Contains("cannot be switched", vm.CurrentText);   // the refusal is the one stable line
    }

    [Fact]
    public void AnUnsupportedPortNeverRequestsOrConfirms()
    {
        var mux = new FakeMux { Supported = false, Modes = [] };
        var warnings = new List<string>();
        var vm = Vm(mux, confirm: true, warnings: warnings);

        vm.SelectedIndex = 1;   // no selector flow runs: the port exposes no switch

        Assert.Empty(mux.Requests);
        Assert.Empty(warnings);
    }
}
