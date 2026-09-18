using System.Buffers.Binary;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The frame decode of the Windows LampArray transport — <c>LampArrayTransport.DecodeFrame</c>, the one piece
/// of <c>WaitFrame</c> that decides what a WAIT_FRAME payload means.
///
/// Worth a file of its own because getting it wrong is not a wrong picture, it is the whole app going away:
/// the frame comes back on the bridge's worker thread, and <c>LampArrayBridge.WorkerLoop</c> has no catch, so
/// an exception here terminates the process. The shape that did exactly that is pinned below — the payload
/// advertises one number of lamps, the published layout says another, and the two were read from the
/// transport's mutable lamp count at different moments while a <c>Stop()</c> was tearing the device down.
///
/// The fix is that the published count reaches this method as an ARGUMENT: it bounds the decode and sizes the
/// array, so the clamp and the array cannot disagree no matter when the teardown happens. That argument is
/// also what makes the decode reachable at all without hardware — reaching the wait itself needs a real
/// control device, the driver staged, and elevation, none of which this suite has.
///
/// NOT covered here, deliberately: <c>WaitFrame</c> itself. Its body is a blocking <c>DeviceIoControl</c> on
/// the frame handle, so a test could only drive it against a live virtual LampArray, which would need this
/// machine's driver AND would publish a real lighting device. That leaves one hole, and it was measured rather
/// than assumed: replacing the argument below with a fresh read of the transport's lamp count field —
/// <c>DecodeFrame(buf, _lampCount)</c>, i.e. the original two-read defect put back at the call site — leaves
/// this file GREEN. The structural fix is that the count reaches the decode as an argument, so the clamp and
/// the array cannot disagree; that the argument is the snapshot taken before the wait, rather than a second
/// read taken after it, is held by review and by the remark on <c>WaitFrame</c>, not by a test. Closing it
/// would need a seam in the transport (an injectable handle, an injectable <c>DeviceIoControl</c>), and a
/// hardware transport that grows test-only seams to test its own state machine is a worse trade than one
/// documented hole — the same judgement LampArrayLifecycleTests records for the bridge. Same gap, and same
/// reasoning, as EneHidEncodingTests' note about <c>Send</c>.
///
/// <c>DecodeFrame</c> was extracted from <c>WaitFrame</c> and made <c>internal</c> for this file — the
/// <c>RyzenCurveOptimizer.Encode</c>/<c>CoreArg</c>/<c>GpuMargin</c> precedent (SmuOffsetEncodingTests.cs).
/// </summary>
public class LampArrayTransportFrameTests
{
    // The wire contract, spelled here on purpose rather than imported: these are values from
    // driver/AcerHelperLampArray/public.h (sizeof(AHLA_FRAME), the Sequence/AutonomousMode/LampCount offsets,
    // and the 4-byte AHLA_COLOR stride), and a test that read them off the type under test could not notice
    // the type moving them.
    private const int MaxLamps = 64;
    private const int FrameSize = 12 + MaxLamps * 4;
    private const int ColorsOffset = 12;
    private const int ColorStride = 4;

    private static byte[] Payload(uint sequence, uint payloadLampCount, bool autonomous = false)
    {
        var b = new byte[FrameSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), autonomous ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), payloadLampCount);
        return b;
    }

    private static void SetLamp(byte[] payload, int lamp, byte r, byte g, byte b, byte intensity)
    {
        var o = ColorsOffset + lamp * ColorStride;
        payload[o] = r; payload[o + 1] = g; payload[o + 2] = b; payload[o + 3] = intensity;
    }

    // A colour that cannot be mistaken for a default (all-zero) entry, so "this lamp was decoded" and "this
    // lamp was left alone" are distinguishable at every index.
    private static LampColor Mark(int lamp) => new((byte)(lamp + 1), (byte)(0x40 + lamp), (byte)(0x80 + lamp), 100);

    /// <summary>The array a frame hands the bridge is exactly as long as the layout that was published — the
    /// bridge indexes those colours BY LAMP against that layout (<c>LampArrayBridge.Paint</c> walks
    /// <c>layout.Targets</c> and reads <c>colors[i]</c>), so a short array there is an overrun one layer up,
    /// in a method this file cannot reach. 0 is included because that is what a torn-down transport reports,
    /// and 64 is the contract's ceiling (AHLA_MAX_LAMPS).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    public void TheArrayIsExactlyAsLongAsThePublishedLampCount(int lampCount)
    {
        var frame = LampArrayTransport.DecodeFrame(Payload(1, MaxLamps), lampCount);

        Assert.Equal(lampCount, frame.Colors.Length);
    }

    /// <summary>THE CRASH SHAPE, and the reason the published count is a parameter rather than a field read.
    /// The driver fills its frame from the layout it holds, so a payload advertising more lamps than the
    /// layout published is ordinary — the surplus is not decoded, and the array keeps the length the layout
    /// promised. Before the count was pinned, the array could be allocated against a count a <c>Stop()</c> had
    /// already zeroed while the clamp still held the old one, and this exact input walked off the end of it.</summary>
    [Fact]
    public void APayloadClaimingMoreLampsThanWerePublishedIsClampedAndCannotOverrunTheArray()
    {
        const int published = 8;
        var payload = Payload(7, MaxLamps);
        for (var i = 0; i < MaxLamps; i++) SetLamp(payload, i, Mark(i).R, Mark(i).G, Mark(i).B, Mark(i).Intensity);

        var frame = LampArrayTransport.DecodeFrame(payload, published);

        Assert.Equal(published, frame.Colors.Length);
        for (var i = 0; i < published; i++) Assert.Equal(Mark(i), frame.Colors[i]);
    }

    /// <summary>A published count of zero against a payload that advertises the whole array: the state a
    /// teardown mid-wait leaves, and the one that used to throw.</summary>
    [Fact]
    public void AZeroPublishedCountDecodesToAnEmptyFrameWithoutThrowing()
    {
        var payload = Payload(9, MaxLamps);
        SetLamp(payload, 0, 255, 255, 255, 100);

        var frame = LampArrayTransport.DecodeFrame(payload, 0);

        Assert.Empty(frame.Colors);
    }

    /// <summary>A payload that filled fewer lamps than the layout published — a partial batch, which the
    /// driver never hands up but the decode must still not invent colours for. The untouched entries keep the
    /// default, whose intensity 0 reads as off (<c>LampColor.Rgb</c>).</summary>
    [Fact]
    public void ALampThePayloadDidNotFillStaysOff()
    {
        const int published = 8;
        var payload = Payload(3, 3);
        for (var i = 0; i < 3; i++) SetLamp(payload, i, Mark(i).R, Mark(i).G, Mark(i).B, Mark(i).Intensity);

        var frame = LampArrayTransport.DecodeFrame(payload, published);

        Assert.Equal(published, frame.Colors.Length);
        for (var i = 0; i < 3; i++) Assert.Equal(Mark(i), frame.Colors[i]);
        for (var i = 3; i < published; i++) Assert.Equal(default, frame.Colors[i]);
    }

    /// <summary>The header fields are read from their wire offsets — <c>Sequence</c> first, then
    /// <c>AutonomousMode</c>, both 4-byte little-endian, ahead of the colours at offset 12.</summary>
    [Fact]
    public void SequenceAndAutonomousModeComeOffTheirWireOffsets()
    {
        var handedBack = LampArrayTransport.DecodeFrame(Payload(0xDEADBEEF, 4, autonomous: true), 4);
        Assert.Equal(0xDEADBEEFu, handedBack.Sequence);
        Assert.True(handedBack.AutonomousMode);

        var taken = LampArrayTransport.DecodeFrame(Payload(1, 4), 4);
        Assert.Equal(1u, taken.Sequence);
        Assert.False(taken.AutonomousMode);
    }

    /// <summary>Each lamp's colour sits at its own index, not at a packed position: lamp i is four bytes at
    /// 12 + i*4. A decode that walked the colours without the stride would still pass every length assertion
    /// above, so this pins the placement.</summary>
    [Fact]
    public void EachLampIsDecodedFromItsOwnIndex()
    {
        const int published = 8;
        var payload = Payload(2, published);
        var only = 5;
        SetLamp(payload, only, Mark(only).R, Mark(only).G, Mark(only).B, Mark(only).Intensity);

        var frame = LampArrayTransport.DecodeFrame(payload, published);

        Assert.Equal(Mark(only), frame.Colors[only]);
        for (var i = 0; i < published; i++)
            if (i != only) Assert.Equal(default, frame.Colors[i]);
    }
}
