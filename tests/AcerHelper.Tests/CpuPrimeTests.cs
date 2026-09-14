using AcerHelper.Domain;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// Wave 6, step 3 — the CPU-power row is the one member of <c>UiActions</c> whose construction used to cost a
/// hardware read (<c>AppController.BuildUi</c> called <c>_svc.CurrentCpuPower()</c> on the UI thread). It is now
/// built from a placeholder and filled in by the first background pass.
///
/// Everything of step 3 that is not this class lives in <c>AppController</c> — the prime itself, the
/// <c>_cpuPrimed</c> flag and its reset on a language rebuild — and has no test: <c>AppController</c> needs a
/// desktop lifetime and a live refresh loop. Those parts are marked <c>[M]</c> in the commit that carries them.
///
/// What IS checkable here is the claim the placeholder rests on, and it is worth checking because the whole
/// deferral is only safe if both halves hold:
/// <list type="number">
/// <item><c>null</c> really does land on Balanced — if it silently landed on the first entry (Best power
/// efficiency, which is <c>_modes[0]</c>), every startup would flash the wrong mode and the "placeholder is
/// what a failed read gives" rule would be false for this row;</item>
/// <item>neither the placeholder's arrival nor the prime's can reach <c>SetCpuPower</c> — a prime that wrote
/// would be the app forcing an OS power mode on a machine whose user never asked, which
/// <c>ApplyModeCpuPower</c> deliberately avoids on an unconfigured profile.</item>
/// </list>
/// </summary>
public class CpuPrimeTests
{
    // Display order and ids as the real port declares them (OverlayCpuPower.Windows.cs): efficiency, balanced,
    // performance — with Balanced being the all-zero GUID, which is the whole reason IndexOf falls back to it.
    private const string Efficiency = "961cc777-2547-4f9d-8174-7d86181b8a7a";
    private const string Balanced = "00000000-0000-0000-0000-000000000000";
    private const string Performance = "ded574b5-45a0-4f42-8737-46345c09c238";

    private static readonly ChoiceOption[] Modes =
    [
        new(Efficiency, "Best power efficiency"),
        new(Balanced, "Balanced"),
        new(Performance, "Best performance"),
    ];

    /// <summary>The placeholder, on the path that matters: a null id is the live-overlay read failing, and it
    /// must show Balanced — NOT index 0, which is what <c>Math.Clamp</c>-style fallbacks in the option rows would
    /// have produced and what an unknown id in a modes list without Balanced would give.</summary>
    [Fact]
    public void ThePlaceholder_LandsOnBalanced_NotOnTheFirstEntry()
    {
        var picks = new List<string>();

        var vm = new CpuViewModel(Modes, initialId: null, picks.Add);

        Assert.Equal(1, vm.SelectedIndex);
        Assert.Equal("Balanced", vm.ModeNames[vm.SelectedIndex]);
        Assert.Empty(picks);
    }

    /// <summary>The prime's landing point is <c>Load</c>, and it must reflect without applying — on any id,
    /// including the placeholder's own null. This is the property that lets the deferred read be a read: the
    /// value arrives from hardware that is ALREADY in that state, so writing it back would be an EC/powrprof
    /// transaction nobody asked for.</summary>
    [Theory]
    [InlineData(Efficiency, 0)]
    [InlineData(Balanced, 1)]
    [InlineData(Performance, 2)]
    [InlineData(null, 1)]
    [InlineData("an-overlay-from-a-newer-windows", 1)]   // unknown id -> Balanced, same as the placeholder
    public void ThePrime_ReflectsTheId_AndNeverWrites(string? id, int expectedIndex)
    {
        var picks = new List<string>();
        var vm = new CpuViewModel(Modes, Efficiency, picks.Add);
        picks.Clear();

        vm.Load(id);

        Assert.Equal(expectedIndex, vm.SelectedIndex);
        Assert.Empty(picks);
    }

    /// <summary>A device whose overlay API answers with an id outside the three known modes: the port folds it to
    /// Balanced before it ever reaches here (OverlayCpuPower.Current), and this pins what happens if it did not —
    /// the row still shows a known mode rather than an unmapped index.</summary>
    [Fact]
    public void AnUnknownId_ShowsBalanced_RatherThanAnOutOfRangeSelection()
    {
        var vm = new CpuViewModel(Modes, "not-a-guid", _ => { });

        Assert.Equal(1, vm.SelectedIndex);
    }

    /// <summary>The control for the guard above: a user pick must still reach the delegate, so none of the
    /// "never writes" assertions in this file is passing because the hook is dead.</summary>
    [Fact]
    public void AUserPick_StillReachesTheDelegate()
    {
        var picks = new List<string>();
        var vm = new CpuViewModel(Modes, null, picks.Add);

        vm.SelectedIndex = 2;

        Assert.Equal([Performance], picks);
    }
}
