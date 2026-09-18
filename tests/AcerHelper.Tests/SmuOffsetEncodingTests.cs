using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The SMU Curve-Optimizer offset encodings — the single highest-value silent-failure target in the tree.
///
/// Three helpers, two DIFFERENT widths, and the mailbox accepts either without complaint while meaning
/// something else entirely:
///   * <c>Encode</c>     — the CPU (MP1) 20-bit field: -5 is 0xFFFFB.
///   * <c>GpuMargin</c>  — the iGPU (RSMU) 16-bit two's-complement margin: -5 is 0xFFFB.
///   * <c>CoreArg</c>    — the per-core CPU argument: CCD in [31:28], slot in [23:20], margin masked to
///                         [15:0] — so a 20-bit Encode value passed through it SILENTLY loses bits 16..19.
///
/// A wrong bit pattern here is written to a hardware register and acknowledged with REP_MSG_OK. The only
/// observable symptom is an undervolt that does not hold, or one that is twice as deep as the user asked
/// for. These tests are the only thing that can catch a change in the arithmetic.
///
/// Every expected literal below is one of:
///   - a value the source comments state explicitly (0xFFFFB / 0xFFFB),
///   - a value at the offered bound (MinCounts = -40, GpuMinCounts = -50), or
///   - 0x100000 - |n| (CPU) / the low 16 bits of that (GPU), computed by hand.
/// </summary>
public class SmuOffsetEncodingTests
{
    private const uint CpuWideMask20 = 0xFFFFFu;   // the CPU field is 20 bits
    private const uint GpuWideMask16 = 0xFFFFu;    // the GPU field is 16 bits

    // The offered ranges, from the source: RyzenCurveOptimizer.MinCounts / GpuMinCounts and Range.
    private const int CpuMin = -40;
    private const int GpuMin = -50;

    // ================= Encode — the CPU (MP1) 20-bit field =================

    [Fact]
    public void Encode_Zero_IsAPlainZero_NotTheSignBit()
    {
        // The source is explicit: 0 must NOT be sent as 0x100000, because bit 20 is the core selector in
        // the per-core form of the message. Getting this wrong is a known source of rejected arguments.
        Assert.Equal(0u, RyzenCurveOptimizer.Encode(0));
        Assert.NotEqual(0x100000u, RyzenCurveOptimizer.Encode(0));
    }

    /// <summary>THE HARDENING READING WON, and this case is now a spec rather than an open question.
    ///
    /// What the code does (<c>RyzenCurveOptimizer.Encode</c>):
    ///
    ///     =&gt; (counts &gt;= 0 ? 0u : 0x100000u - (uint)(-counts)) &amp; 0xFFFFFu;
    ///
    /// The non-negative branch is the literal <c>0u</c>, so EVERY non-negative input collapses to the stock
    /// word: Encode(1), Encode(2), Encode(5) and Encode(100) are all 0. No positive offset can reach the mailbox
    /// as its own value — a request to RAISE voltage becomes "leave stock", silently, which is the intended
    /// answer: the app supports undervolt only and a positive offset buys nothing here while being a thermal and
    /// stability risk (the sentence <c>ICurveOptimizer.Range</c> carries). This used to be recorded as OBSERVED,
    /// with two readings left open, and the counter-evidence for the second one was the sibling <c>GpuMargin</c>
    /// genuinely returning 5 for 5. That counter-evidence is gone: <c>GpuMargin</c> now collapses non-negative
    /// inputs too, so the two helpers agree by rule rather than by accident, and the reading is resolved in
    /// docs/open-decisions.md's item 1.
    ///
    /// Still unreachable on today's call paths — <c>Set</c>, <c>SetDomains</c>, <c>Range</c>, <c>CoViewModel</c>'s
    /// constructor clamp and the clamp in <c>LaptopService.SetCo</c> all clamp to [MinCounts, 0] — but the guard
    /// is now what makes that a property of the encoding instead of a count of the clamps that exist, so a later
    /// widening of the range cannot silently turn into a voltage increase.</summary>
    [Theory]
    [InlineData(1, 0u)]
    [InlineData(2, 0u)]
    [InlineData(5, 0u)]
    [InlineData(100, 0u)]
    public void Encode_NonNegative_CollapsesToStock(int counts, uint observed) =>
        Assert.Equal(observed, RyzenCurveOptimizer.Encode(counts));

    /// <summary>Both helpers answer the same for every non-negative input — the two rails' encodings differ in
    /// WIDTH and in sign representation, never in whether a request to raise voltage is representable. The name
    /// this test used to carry ("...the only non-negative value the two helpers agree on") was true while
    /// <c>GpuMargin</c> passed positives through, and is asserted here in its stronger, current form.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void BothHelpers_CollapseEveryNonNegativeInputToStock(int counts)
    {
        Assert.Equal(0u, RyzenCurveOptimizer.Encode(counts));
        Assert.Equal(0u, RyzenCurveOptimizer.GpuMargin(counts));
    }

    [Theory]
    [InlineData(-1, 0xFFFFFu)]      // 0x100000 - 1
    [InlineData(-2, 0xFFFFEu)]
    [InlineData(-5, 0xFFFFBu)]      // <- stated verbatim in the source comment
    [InlineData(-10, 0xFFFF6u)]
    [InlineData(-30, 0xFFFE2u)]
    [InlineData(-40, 0xFFFD8u)]     // MinCounts, the offered floor
    [InlineData(-50, 0xFFFCEu)]     // GpuMinCounts, the deepest the iGPU slider offers
    public void Encode_NegativeOffsets_Are0x100000MinusMagnitude(int counts, uint expected) =>
        Assert.Equal(expected, RyzenCurveOptimizer.Encode(counts));

    [Fact]
    public void Encode_Minus5_Is0xFFFFB_NotTheGpus0xFFFB()
    {
        // The whole point of the two separate helpers.
        Assert.Equal(0xFFFFBu, RyzenCurveOptimizer.Encode(-5));
        Assert.NotEqual(0xFFFBu, RyzenCurveOptimizer.Encode(-5));
        Assert.NotEqual(RyzenCurveOptimizer.GpuMargin(-5), RyzenCurveOptimizer.Encode(-5));
        Assert.Equal(0xFFFBu, RyzenCurveOptimizer.GpuMargin(-5));
    }

    [Fact]
    public void Encode_NeverSetsABitAboveTheTwentieth()
    {
        for (var counts = CpuMin; counts <= 0; counts++)
            Assert.Equal(0u, RyzenCurveOptimizer.Encode(counts) & ~CpuWideMask20);
    }

    [Fact]
    public void Encode_IsInjectiveAcrossTheWholeOfferedRange()
    {
        // Two different slider positions must never produce the same register word, or one of them is
        // silently ignored. Swept over the union of the ranges the two rails can produce (GpuMin..0 covers
        // CpuMin..0 as well), which is every value the UI can hand to Encode.
        var seen = new HashSet<uint>();
        for (var counts = GpuMin; counts <= 0; counts++)
            Assert.True(seen.Add(RyzenCurveOptimizer.Encode(counts)), $"duplicate encoding at {counts}");

        Assert.Equal(-GpuMin + 1, seen.Count);
    }

    /// <summary>Width boundary, outside anything the UI can offer: at exactly -2^20 the 20-bit field wraps
    /// onto zero, so "stock" and the most extreme representable offset become the same register word. The
    /// clamp at MinCounts = -40 makes this unreachable today; the test documents what the mask does, so a
    /// future widening of the range cannot cross this line unnoticed.</summary>
    [Fact]
    public void Encode_AtTheWidthBoundary_WrapsOntoZero_DocumentedCollision()
    {
        Assert.Equal(0u, RyzenCurveOptimizer.Encode(-1048576));                          // -2^20
        Assert.Equal(RyzenCurveOptimizer.Encode(0), RyzenCurveOptimizer.Encode(-1048576)); // ...collides with stock
        Assert.Equal(1u, RyzenCurveOptimizer.Encode(-1048575));                          // -2^20 + 1
        Assert.Equal(0xFFFFFu, RyzenCurveOptimizer.Encode(-1));                          // the deepest representable
    }

    [Fact]
    public void Encode_SignIsCarriedByTheWrap_NotByABitFlag()
    {
        // Two's-complement style: the sign is the magnitude subtracted from the 20-bit modulus.
        for (var counts = CpuMin; counts < 0; counts++)
            Assert.Equal((uint)(0x100000 + counts) & CpuWideMask20, RyzenCurveOptimizer.Encode(counts));
    }

    // ================= GpuMargin — the iGPU (RSMU) 16-bit margin =================

    [Fact]
    public void GpuMargin_Zero_IsZero()
    {
        Assert.Equal(0u, RyzenCurveOptimizer.GpuMargin(0));
    }

    /// <summary>REWRITTEN — and this is the one case in this file whose EXPECTATION was deliberately replaced
    /// rather than corrected. It used to be <c>GpuMargin_PositiveOffsets_AreTheValueItself</c>: 1 → 1, 2 → 2,
    /// 5 → 5, 65535 → 0xFFFF. That was recorded as OBSERVED, not as a spec — the sibling <c>Encode</c> was
    /// already collapsing non-negative inputs to <c>0u</c>, and the disagreement between the two helpers was the
    /// counter-evidence that kept <c>Encode</c>'s flattening documented as an open question in
    /// docs/open-decisions.md.
    ///
    /// The guard settles it, and the policy is the one this file's own source states verbatim: "a positive
    /// offset RAISES voltage, which buys nothing on this hardware and is a thermal and stability risk" (the
    /// sentence <c>ICurveOptimizer.Range</c> carries as "Min &lt; 0, Max = 0 — undervolt only"). A margin that
    /// asks for MORE voltage must not be representable at the mailbox, so every non-negative input now collapses
    /// to stock — the same answer <c>Encode</c> gives, for the same reason.
    ///
    /// The old expectation was unreachable in production either way (Set clamps to [MinCounts, 0] at the port,
    /// and the service and the view-model clamp before that), which is exactly why it had to be a stated rule
    /// rather than a property of the clamps: the guard is what makes "unreachable" true of the ENCODING.
    ///
    /// MUTATION THAT REDDENS IT: restoring the pass-through branch
    /// (<c>(uint)((counts &lt; 0 ? 0x100000 : 0) + counts) &amp; 0xFFFF</c>), after which 65535 is 0xFFFF again.</summary>
    [Theory]
    [InlineData(1, 0u)]
    [InlineData(2, 0u)]
    [InlineData(5, 0u)]
    [InlineData(65535, 0u)]
    [InlineData(int.MaxValue, 0u)]
    public void GpuMargin_NonNegativeOffsets_AreCollapsedToStock(int counts, uint expected) =>
        Assert.Equal(expected, RyzenCurveOptimizer.GpuMargin(counts));

    [Theory]
    [InlineData(-1, 0xFFFFu)]
    [InlineData(-2, 0xFFFEu)]
    [InlineData(-5, 0xFFFBu)]       // <- stated verbatim in the source comment: the GPU's form of -5
    [InlineData(-10, 0xFFF6u)]
    [InlineData(-30, 0xFFE2u)]
    [InlineData(-50, 0xFFCEu)]      // GpuMinCounts, the offered floor
    public void GpuMargin_NegativeOffsets_Are0x100000MinusMagnitudeMasked(int counts, uint expected) =>
        Assert.Equal(expected, RyzenCurveOptimizer.GpuMargin(counts));

    [Fact]
    public void GpuMargin_NeverSetsABitAboveTheSixteenth()
    {
        for (var counts = GpuMin; counts <= 0; counts++)
            Assert.Equal(0u, RyzenCurveOptimizer.GpuMargin(counts) & ~GpuWideMask16);
    }

    [Fact]
    public void GpuMargin_IsInjectiveAcrossTheWholeOfferedRange()
    {
        var seen = new HashSet<uint>();
        for (var counts = GpuMin; counts <= 0; counts++) Assert.True(seen.Add(RyzenCurveOptimizer.GpuMargin(counts)),
            $"duplicate encoding at {counts}");
    }

    /// <summary>Width boundary, outside the offered range: the 16-bit mask makes -65536 collide with 0.
    /// (65536 lands on 0 as well, and for a different reason — the non-negative ceiling above, not the width —
    /// which is why the two lines are asserted separately rather than as one symmetric collision.) Unreachable
    /// behind GpuMinCounts = -50 and the positive-offsets-are-not-offered rule, but this is the exact collision
    /// the mailbox cannot detect.</summary>
    [Fact]
    public void GpuMargin_AtTheWidthBoundary_WrapsOntoZero_DocumentedCollision()
    {
        Assert.Equal(0u, RyzenCurveOptimizer.GpuMargin(-65536));   // the width: 0x100000 - 65536, masked
        Assert.Equal(0u, RyzenCurveOptimizer.GpuMargin(65536));    // the ceiling: it is not negative
        Assert.Equal(RyzenCurveOptimizer.GpuMargin(0), RyzenCurveOptimizer.GpuMargin(-65536));
    }

    [Fact]
    public void GpuMargin_IsTheLowSixteenBitsOfTheCpuWideValue()
    {
        // The two helpers are written independently (deliberately — a shared one with a width parameter is
        // what the source says it did NOT want), so this cross-check is real: whatever changes inside
        // either, the GPU word must stay the CPU word truncated to 16 bits for every offered offset.
        for (var counts = GpuMin; counts <= 0; counts++)
            Assert.Equal(RyzenCurveOptimizer.Encode(counts) & GpuWideMask16, RyzenCurveOptimizer.GpuMargin(counts));
    }

    // ================= CoreArg — the per-core CPU argument =================

    [Fact]
    public void CoreArg_ZeroSlotZeroCcd_IsJustTheMargin()
    {
        Assert.Equal(0u, RyzenCurveOptimizer.CoreArg(0, 0, 0));
        Assert.Equal(0xFFFBu, RyzenCurveOptimizer.CoreArg(0, 0, 0xFFFFBu));
    }

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(1, 0x00100000u)]
    [InlineData(2, 0x00200000u)]
    [InlineData(6, 0x00600000u)]
    [InlineData(7, 0x00700000u)]
    public void CoreArg_SlotLandsInBits20To23(int slot, uint expected) =>
        Assert.Equal(expected, RyzenCurveOptimizer.CoreArg(0, slot, 0));

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(1, 0x10000000u)]
    public void CoreArg_CcdLandsInBits28To31(int ccd, uint expected) =>
        Assert.Equal(expected, RyzenCurveOptimizer.CoreArg(ccd, 0, 0));

    [Fact]
    public void CoreArg_CcdAndSlotDoNotOverlap()
    {
        // The two production CCDs are 0 (Zen 5) and 1 (Zen 5c), and slots run 0..7.
        for (var ccd = 0; ccd <= 1; ccd++)
            for (var slot = 0; slot < 8; slot++)
            {
                var arg = RyzenCurveOptimizer.CoreArg(ccd, slot, 0);
                Assert.Equal((uint)ccd, arg >> 28);
                Assert.Equal((uint)slot, (arg >> 20) & 0xFu);
            }
    }

    [Fact]
    public void CoreArg_AllEightSlotsOfOneCcdAreDistinct()
    {
        var seen = new HashSet<uint>();
        for (var slot = 0; slot < 8; slot++)
            Assert.True(seen.Add(RyzenCurveOptimizer.CoreArg(1, slot, 0xFFFBu)), $"slot {slot} collided");
    }

    [Fact]
    public void CoreArg_IsIndependentOfTheMarginForEverySlot()
    {
        // The margin must not bleed into the selector bits, whatever its value.
        foreach (var margin in new[] { 0u, 1u, 0xFFFBu, 0xFFFFu, 0xFFFFFu })
            for (var slot = 0; slot < 8; slot++)
            {
                var arg = RyzenCurveOptimizer.CoreArg(1, slot, margin);
                Assert.Equal(1u, arg >> 28);
                Assert.Equal((uint)slot, (arg >> 20) & 0xFu);
                Assert.Equal(margin & 0xFFFFu, arg & 0xFFFFu);
            }
    }

    /// <summary>The documented silent trap, asserted directly: <c>Set()</c> sends the 20-bit
    /// <c>Encode</c> result, but <c>SetDomains()</c> routes the same value through <c>CoreArg</c>, which
    /// truncates it to 16 bits. Bits 16..19 are all ones for every negative offset, so they ARE dropped —
    /// harmlessly, because the 16-bit word the SMU reads back out is the GPU rail's own self-consistent
    /// two's complement. This pins the truncation so it cannot become a lossy one unnoticed.</summary>
    [Fact]
    public void CoreArg_TruncatesTheMarginToSixteenBits_LosingTheCpuWideForm()
    {
        Assert.Equal(0xFFFBu, RyzenCurveOptimizer.CoreArg(0, 0, RyzenCurveOptimizer.Encode(-5)));
        Assert.NotEqual(RyzenCurveOptimizer.Encode(-5), RyzenCurveOptimizer.CoreArg(0, 0, RyzenCurveOptimizer.Encode(-5)));
        Assert.Equal(0u, RyzenCurveOptimizer.CoreArg(0, 0, 0x100000u));       // bit 20 is dropped entirely
        Assert.Equal(0xFFFFu, RyzenCurveOptimizer.CoreArg(0, 0, 0xFFFFFu));
    }

    [Fact]
    public void CoreArg_ForEveryOfferedOffset_CarriesTheSixteenBitForm_NotTheTwentyBitOne()
    {
        // Over the whole offered CPU range, the margin the per-core message actually carries is the GPU
        // rail's 16-bit two's complement — never the 20-bit word Encode produced.
        for (var counts = CpuMin; counts <= 0; counts++)
            Assert.Equal(RyzenCurveOptimizer.GpuMargin(counts),
                         RyzenCurveOptimizer.CoreArg(1, 7, RyzenCurveOptimizer.Encode(counts)) & 0xFFFFu);

        // ...and for every negative offset bits 16..19 really are lost.
        for (var counts = CpuMin; counts < 0; counts++)
            Assert.NotEqual(RyzenCurveOptimizer.Encode(counts),
                            RyzenCurveOptimizer.CoreArg(1, 7, RyzenCurveOptimizer.Encode(counts)) & 0xFFFFu);
    }

    [Fact]
    public void CoreArg_SlotWrapsModuloEight()
    {
        // SlotsPerCcd is 8 and the loop only ever passes 0..7, but the modulo is in the source and is the
        // reason a slot beyond the CCD does not leak into the CCD selector.
        Assert.Equal(RyzenCurveOptimizer.CoreArg(0, 0, 0), RyzenCurveOptimizer.CoreArg(0, 8, 0));
        Assert.Equal(RyzenCurveOptimizer.CoreArg(0, 1, 0), RyzenCurveOptimizer.CoreArg(0, 9, 0));
    }

    // ================= cross-helper invariants =================

    [Fact]
    public void TheTwoEncodingsDiffer_ForOffsetsInsideTheOfferedRange()
    {
        // -5 is the case the source calls out, and it is inside the offered range on both rails, so the
        // two words genuinely differ for a value the user can actually pick.
        Assert.Equal(0xFFFFBu, RyzenCurveOptimizer.Encode(-5));
        Assert.Equal(0xFFFBu, RyzenCurveOptimizer.GpuMargin(-5));
        Assert.NotEqual(RyzenCurveOptimizer.Encode(-5), RyzenCurveOptimizer.GpuMargin(-5));
    }
}
