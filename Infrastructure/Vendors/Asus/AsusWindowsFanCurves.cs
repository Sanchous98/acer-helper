namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS Windows fan-curve path over the ATK device — READ + consent-gated WRITE, reusing the SAME pure model
// and validator as the Linux/asusd path (`AsusFanCurve`, `AsusFan`, `AsusFanCurveCodec.Validate`,
// `AsusFanCurveMessages`), because only the transport differs.
//
// MAPPING DECISION (same as Linux): a firmware curve is NOT an IFanControl. The app's IFanControl is mode/speed
// shaped and its own curve is a software emulation; the ASUS curve is eight temperature/pwm points per fan,
// written to the EC and interpolated by the firmware. So this is a dedicated port, and IFanControl is untouched.
//
// THE WIRE (G-Helper `app/AsusACPI.cs`, device IDs in AsusAtk.cs):
//   * the curve device per fan: CPU 0x00110024, GPU 0x00110025, MID 0x00110032;
//   * a written curve is a 16-byte buffer: 8 temperatures (0..255 °C) then 8 percentages (0..100 %);
//   * a read is `DSTS(curveDevice, fanMode)` and returns that same 16-byte buffer; the fanMode is SWAPPED
//     relative to the performance-mode value (G-Helper: 1 -> 2, 2 -> 1, else 0).
//
// WRITING IS EC-TOUCHING: consent-gated and single-flight, and never called from the refresh loop.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs): the 16-byte layout and the fanMode swap are taken from G-Helper and
// pinned by tests; the firmware has not been asked.

/// <summary>The 16-byte ATK curve buffer, and the fan/device and mode mappings.</summary>
internal static class AsusWindowsFanCurveCodec
{
    internal const int Points = 8;
    internal const int BufferLength = 16;

    internal static uint DeviceFor(AsusFan fan) => fan switch
    {
        AsusFan.Cpu => AsusAtk.CpuFanCurve,
        AsusFan.Gpu => AsusAtk.GpuFanCurve,
        AsusFan.Mid => AsusAtk.MidFanCurve,
        _           => 0,
    };

    /// <summary>The curve device's own mode value for a performance-mode value (G-Helper's swap).</summary>
    internal static int DeviceMode(int performanceMode) => performanceMode switch
    {
        1 => 2,
        2 => 1,
        _ => 0,
    };

    /// <summary>The 16-byte buffer: the eight temperatures then the eight percentages (clamped to 0..100 %).</summary>
    internal static byte[] ToBuffer(AsusFanCurve curve)
    {
        var buffer = new byte[BufferLength];
        for (var i = 0; i < Points; i++)
        {
            buffer[i] = curve.Points[i].TempC;
            buffer[Points + i] = (byte)Math.Clamp((int)curve.Points[i].Percent, 0, 100);
        }
        return buffer;
    }

    /// <summary>Parse a 16-byte curve buffer; null when the reply is too short to be a curve.</summary>
    internal static AsusFanCurve? FromBuffer(AsusFan fan, byte[] buffer, bool enabled)
    {
        if (buffer.Length < BufferLength) return null;
        var points = new List<AsusFanCurvePoint>(Points);
        for (var i = 0; i < Points; i++)
            points.Add(new AsusFanCurvePoint(buffer[i], (byte)Math.Clamp((int)buffer[Points + i], 0, 100)));
        return new AsusFanCurve(fan, points, enabled);
    }
}

/// <summary>The read half: ask the curve device for a fan's curve in a given performance mode.</summary>
internal sealed class AsusWindowsFanCurvesReader(AsusAtkDevice atk)
{
    public bool TryRead(AsusFan fan, int performanceMode, out AsusFanCurve curve)
    {
        curve = default!;
        var device = AsusWindowsFanCurveCodec.DeviceFor(fan);
        if (device == 0 || !atk.Supported(device)) return false;

        var buffer = atk.GetBuffer(device, (uint)AsusWindowsFanCurveCodec.DeviceMode(performanceMode));
        if (AsusWindowsFanCurveCodec.FromBuffer(fan, buffer, enabled: true) is not { } parsed) return false;
        curve = parsed;
        return true;
    }
}

/// <summary>The consent-gated, single-flight write half. The curve is written for the fan's CURRENT mode: the
/// ATK curve device is selected by fan only, and the profile's mode is established by the switch that precedes
/// the write (G-Helper's own order), so no mode argument travels here.</summary>
internal sealed class AsusWindowsFanCurvesWriter(AsusAtkDevice atk, AsusWriteConsent consent)
{
    private readonly object _gate = new();

    internal AsusWriteOutcome SetCurve(AsusFan fan, AsusFanCurve curve)
    {
        if (AsusFanCurveCodec.Validate(curve) is (false, { } refusal)) return AsusWriteOutcome.Refused(refusal);

        var device = AsusWindowsFanCurveCodec.DeviceFor(fan);
        if (device == 0) return AsusWriteOutcome.Refused(AsusFanCurveMessages.UnknownFan);
        if (!atk.Supported(device)) return AsusWriteOutcome.Refused(AsusFanCurveMessages.Unsupported);

        lock (_gate)
        {
            if (!consent(AsusFanCurveMessages.Warning)) return AsusWriteOutcome.Refused(AsusArmouryMessages.Cancelled);

            var result = AsusAtk.DecodeSet(atk.SetBuffer(device, AsusWindowsFanCurveCodec.ToBuffer(curve)));
            return result == 1
                ? AsusWriteOutcome.Applied()
                : AsusWriteOutcome.Refused(result < 0
                    ? "the machine did not answer the fan-curve write"
                    : $"the firmware refused the fan-curve write (status {result})");
        }
    }
}
