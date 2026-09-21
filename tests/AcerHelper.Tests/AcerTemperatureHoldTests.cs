using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The hold that decides what a temperature row carries, and the memory that makes the EC's intermittent channel
/// answerable at all.
///
/// WHY A HOLDER AND NOT A PURE FUNCTION: the EC's channel is not "0 or a value" as a stable property. Sampled
/// eight times 4 s apart it gave 41, 40, 41, 40, 40, 40, 0, 0, and earlier in the same session it read 0 for
/// twelve consecutive samples (≥60 s) before coming back. A rule that switched to the generic source on each zero
/// would make the Monitor alternate between the EC's ~40 °C and the substitute's ~35 °C — a flickering number,
/// worse than either stable answer — so a real reading is REMEMBERED and a zero holds it.
///
/// The rows below are therefore SEQUENCES on one hold, because the behaviour that matters only exists across
/// calls: what a single call returns is the easy half.
///
/// It lives in an un-suffixed file for the reason the rest of this backend's policy does: the test project cannot
/// compile AcerDevice.Linux.cs at all (net10.0-windows vs **/*.Linux.cs), so a rule written at the call site could
/// not be driven by any test. The I/O stays there; only the decision arrives here.
/// </summary>
public class AcerTemperatureHoldTests
{
    /// <summary>The measured shape, in one sequence: real, real, zero, zero, real — and the row must show the EC's
    /// own values with no dip to the substitute in between.</summary>
    [Fact]
    public void ARealReadingIsUsed_AndAZeroHoldsIt()
    {
        var hold = new AcerTemperatureHold();

        Assert.Equal(41, hold.Reading(fromEc: 41, fromGeneric: 35));
        Assert.Equal(40, hold.Reading(fromEc: 40, fromGeneric: 35));
        Assert.Equal(40, hold.Reading(fromEc: 0, fromGeneric: 35));    // hold, not 35
        Assert.Equal(40, hold.Reading(fromEc: 0, fromGeneric: 35));    // …however long the stretch runs
        Assert.Equal(39, hold.Reading(fromEc: 39, fromGeneric: 35));   // a real reading replaces the held one
        Assert.Equal(39, hold.Reading(fromEc: 0, fromGeneric: 35));
    }

    /// <summary>Before the channel has EVER answered there is nothing to hold, so the generic source is the only
    /// information there is and the row carries it.</summary>
    [Fact]
    public void AZeroWithNothingRemembered_FallsBackToTheGenericSource()
    {
        var hold = new AcerTemperatureHold();

        Assert.Equal(35, hold.Reading(fromEc: 0, fromGeneric: 35));
    }

    /// <summary>A missing EC node is the same "no value" as a zero: it falls back before anything is remembered,
    /// and holds afterwards. Two meanings for "the EC has no reading" would be a rule with an exception nobody
    /// could predict.</summary>
    [Fact]
    public void AMissingEcNodeIsTreatedLikeAZero()
    {
        var fallback = new AcerTemperatureHold();
        Assert.Equal(35, fallback.Reading(fromEc: -1, fromGeneric: 35));

        var holding = new AcerTemperatureHold();
        Assert.Equal(40, holding.Reading(fromEc: 40, fromGeneric: 35));
        Assert.Equal(40, holding.Reading(fromEc: -1, fromGeneric: 35));
    }

    /// <summary>Nothing anywhere is -1 — the contract's "unavailable", which the UI renders as "—" and hides.
    /// A zero from the generic source is not a temperature either, so it does not become one by being the last
    /// candidate standing.</summary>
    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(-1, 0)]
    public void NothingAnywhereIsUnavailable(int fromEc, int fromGeneric)
    {
        var hold = new AcerTemperatureHold();

        Assert.Equal(-1, hold.Reading(fromEc, fromGeneric));
    }

    /// <summary>The generic source never displaces a real EC reading — including the one already held, which is
    /// the point of holding it: the EC is the right sensor for this machine.</summary>
    [Fact]
    public void TheGenericSourceNeverDisplacesTheEc()
    {
        var hold = new AcerTemperatureHold();

        Assert.Equal(40, hold.Reading(fromEc: 40, fromGeneric: 90));   // a higher generic value does not win
        Assert.Equal(40, hold.Reading(fromEc: 0, fromGeneric: 90));    // nor does it replace the held one
    }

    /// <summary>One hold per channel, asserted as the property the wiring depends on: a hold that never saw its
    /// own channel must NOT inherit another channel's memory. (Sharing one instance between the CPU and GPU rows
    /// would let the CPU's real reading stand in for a GPU channel that has never answered at all.)</summary>
    [Fact]
    public void TheMemoryIsTheChannelsOwn()
    {
        var cpu = new AcerTemperatureHold();
        var gpu = new AcerTemperatureHold();
        Assert.Equal(60, cpu.Reading(fromEc: 60, fromGeneric: 35));

        Assert.Equal(35, gpu.Reading(fromEc: 0, fromGeneric: 35));     // the GPU row does not borrow the CPU's 60
        Assert.Equal(60, cpu.Reading(fromEc: 0, fromGeneric: 35));     // and the CPU row still holds its own
    }
}
