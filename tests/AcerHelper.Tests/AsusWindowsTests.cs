using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Asus;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The Windows ASUS backend, driven over a recorded ATK control delegate — no `\\.\ATKACPI`, no WMI, no ASUS
/// hardware. The pure protocol (buffers + decodes), the value mappings, the capability probes, the consent /
/// single-flight gates and the queued-MUX behaviour are all pinned here.
///
/// The fake answers the way the firmware does: a `DSTS` reply for a present device sets `ASUS_WMI_DSTS_PRESENCE_BIT`
/// (bit 16), an absent one answers the unsupported-method value `0xFFFFFFFE`, and a `DEVS` reply is 1 on success.
/// </summary>
public class AsusWindowsTests
{
    private sealed class FakeAtk
    {
        public HashSet<uint> Present { get; } = [];
        public Dictionary<uint, int> Values { get; } = [];
        public Dictionary<uint, byte[]> Buffers { get; } = [];
        public List<byte[]> Calls { get; } = [];
        public int SetResult { get; set; } = 1;

        public AsusAtkDevice Device => new(Control);

        public byte[] Control(byte[] input)
        {
            Calls.Add(input);
            var method = ReadU32(input, 0);
            var device = ReadU32(input, 8);

            if (method == AsusAtk.MethodDsts)
            {
                if (Buffers.TryGetValue(device, out var buffer)) return Pad(buffer);
                if (Present.Contains(device)) return Reply((uint)(Values.TryGetValue(device, out var v) ? v : 0) | AsusAtk.PresenceBit);
                return Reply(0xFFFFFFFE);
            }

            if (Present.Contains(device) && input.Length >= 16)
            {
                Values[device] = unchecked((int)ReadU32(input, 12));
                if (input.Length > 16) Buffers[device] = input[12..];
            }
            return Reply((uint)SetResult);
        }

        private static byte[] Reply(uint value) { var b = new byte[16]; BitConverter.GetBytes(value).CopyTo(b, 0); return b; }
        private static byte[] Pad(byte[] source) { var b = new byte[16]; Array.Copy(source, b, Math.Min(source.Length, 16)); return b; }
        private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    private static AsusFanCurve Curve(AsusFan fan = AsusFan.Cpu) => new(fan,
        [new(30, 10), new(40, 20), new(50, 30), new(60, 40), new(70, 50), new(80, 60), new(90, 70), new(100, 80)],
        false);

    // ---- the protocol ----

    [Fact]
    public void TheCallBuffersAreTheDocumentedShape()
    {
        Assert.Equal(
            [0x44, 0x45, 0x56, 0x53, 8, 0, 0, 0, 0x16, 0x00, 0x09, 0x00, 1, 0, 0, 0],
            AsusAtk.DevsCall(AsusAtk.GpuMux, 1));   // "DEVS", args length 8, device 0x00090016, value 1

        Assert.Equal(
            [0x44, 0x53, 0x54, 0x53, 8, 0, 0, 0, 0x75, 0x00, 0x12, 0x00, 0, 0, 0, 0],
            AsusAtk.DstsCall(AsusAtk.ThermalPolicy));   // "DSTS", args length 8, device 0x00120075, status 0
    }

    [Fact]
    public void ThePresenceBitIsStrippedAndAnAbsentDeviceIsNegative()
    {
        var present = new byte[16];
        BitConverter.GetBytes(AsusAtk.PresenceBit | 2u).CopyTo(present, 0);
        Assert.Equal(2, AsusAtk.DecodeGet(present));

        var absent = new byte[16];
        BitConverter.GetBytes(0xFFFFFFFEu).CopyTo(absent, 0);
        Assert.True(AsusAtk.DecodeGet(absent) < 0);
    }

    [Fact]
    public void ASetReplyIsOneOnSuccess()
    {
        var ok = new byte[16]; BitConverter.GetBytes(1u).CopyTo(ok, 0);
        Assert.Equal(1, AsusAtk.DecodeSet(ok));
    }

    // ---- profiles ----

    [Fact]
    public void TheProfilesMapSilentBalancedAndTurboToTheAppVocabulary()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.ThermalPolicy);
        fake.Values[AsusAtk.ThermalPolicy] = AsusAtk.ModeTurbo;

        var port = AsusWindowsProfiles.TryCreate(fake.Device, vivo: false);

        Assert.NotNull(port);
        Assert.Equal(["quiet", "balanced", "performance"], port!.All.Select(p => p.Id));
        Assert.Equal("performance", port.Current()!.Id);
        Assert.Equal(ProfileKind.Performance, port.Traits(port.All[2]).Kind);
        Assert.Equal(ProfileKind.Quiet, port.Traits(port.All[0]).Kind);
    }

    [Fact]
    public void SettingAProfileWritesItsModeValue()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.ThermalPolicy);
        var port = AsusWindowsProfiles.TryCreate(fake.Device, vivo: false)!;

        Assert.True(port.Set(port.All.Single(p => p.Id == "quiet")));
        Assert.Equal(AsusAtk.ModeSilent, fake.Values[AsusAtk.ThermalPolicy]);
    }

    [Fact]
    public void TheVivoDeviceIsPreferredWhenTheModelIsKnownVivo()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.ThermalPolicyVivo);

        Assert.NotNull(AsusWindowsProfiles.TryCreate(fake.Device, vivo: true));   // picks Vivo
        Assert.NotNull(AsusWindowsProfiles.TryCreate(fake.Device, vivo: false));  // falls back to Vivo
        Assert.Null(AsusWindowsProfiles.TryCreate(new FakeAtk().Device, vivo: false));
    }

    [Fact]
    public void AnUnnamedModeReadsAsNull()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.ThermalPolicy);
        fake.Values[AsusAtk.ThermalPolicy] = 3;   // FullSpeed, not one of the three

        Assert.Null(AsusWindowsProfiles.TryCreate(fake.Device, false)!.Current());
    }

    // ---- charge limit + panel overdrive ----

    [Fact]
    public void TheChargeLimitUsesTheSharedOnOffMapping()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.BatteryRsoc);
        fake.Values[AsusAtk.BatteryRsoc] = 80;

        var toggle = AsusWindowsChargeLimit.TryCreate(fake.Device);

        Assert.NotNull(toggle);
        Assert.True(toggle!.Read());
        Assert.True(toggle.Write(false).ok);
        Assert.Equal(100, fake.Values[AsusAtk.BatteryRsoc]);
        Assert.True(toggle.Write(true).ok);
        Assert.Equal(80, fake.Values[AsusAtk.BatteryRsoc]);
    }

    [Fact]
    public void WithoutTheRsocDeviceThereIsNoChargeLimit()
        => Assert.Null(AsusWindowsChargeLimit.TryCreate(new FakeAtk().Device));

    [Fact]
    public void PanelOverdriveIsOfferedOnlyWhenReportedSupported()
    {
        var unsupported = new FakeAtk();
        Assert.Null(AsusWindowsPanelOverdrive.TryCreate(unsupported.Device));

        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.PanelOverdriveSupported);
        fake.Values[AsusAtk.PanelOverdriveSupported] = 1;
        fake.Present.Add(AsusAtk.PanelOverdrive);

        var port = AsusWindowsPanelOverdrive.TryCreate(fake.Device);
        Assert.NotNull(port);
        Assert.False(port!.Get());
        Assert.True(port.Set(true));
        Assert.Equal(1, fake.Values[AsusAtk.PanelOverdrive]);
    }

    // ---- fan curves ----

    [Fact]
    public void TheCurveBufferIsTempsThenPercents()
    {
        var buffer = AsusWindowsFanCurveCodec.ToBuffer(Curve());

        Assert.Equal(16, buffer.Length);
        Assert.Equal([30, 40, 50, 60, 70, 80, 90, 100], buffer[..8].Select(b => (int)b));
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80], buffer[8..].Select(b => (int)b));

        var parsed = AsusWindowsFanCurveCodec.FromBuffer(AsusFan.Cpu, buffer, enabled: true)!;
        Assert.Equal(buffer[..8].Select(b => (int)b), parsed.Points.Select(p => (int)p.TempC));
        Assert.Equal(buffer[8..].Select(b => (int)b), parsed.Points.Select(p => (int)p.Percent));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void TheCurveDeviceModeIsSwappedLikeGHelper(int performanceMode, int deviceMode)
        => Assert.Equal(deviceMode, AsusWindowsFanCurveCodec.DeviceMode(performanceMode));

    [Fact]
    public void TheCurveDeviceIsPerFan()
    {
        Assert.Equal(AsusAtk.CpuFanCurve, AsusWindowsFanCurveCodec.DeviceFor(AsusFan.Cpu));
        Assert.Equal(AsusAtk.GpuFanCurve, AsusWindowsFanCurveCodec.DeviceFor(AsusFan.Gpu));
        Assert.Equal(AsusAtk.MidFanCurve, AsusWindowsFanCurveCodec.DeviceFor(AsusFan.Mid));
        Assert.Equal(0u, AsusWindowsFanCurveCodec.DeviceFor(AsusFan.Unknown));
    }

    [Fact]
    public void TheCurveWriterValidatesConsentsAndWrites()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.CpuFanCurve);
        var writer = new AsusWindowsFanCurvesWriter(fake.Device, _ => true);

        Assert.True(writer.SetCurve(AsusFan.Cpu, Curve()).Ok);
        Assert.Equal(AsusWindowsFanCurveCodec.ToBuffer(Curve()), fake.Buffers[AsusAtk.CpuFanCurve]);
    }

    [Fact]
    public void ADeclinedConsentWritesNoCurve()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.CpuFanCurve);
        var writer = new AsusWindowsFanCurvesWriter(fake.Device, _ => false);

        var outcome = writer.SetCurve(AsusFan.Cpu, Curve());

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.Cancelled, outcome.Message);
        Assert.False(fake.Buffers.ContainsKey(AsusAtk.CpuFanCurve));
    }

    [Fact]
    public void AnUnsupportedCurveDeviceIsRefused()
    {
        var writer = new AsusWindowsFanCurvesWriter(new FakeAtk().Device, _ => true);
        Assert.Equal(AsusFanCurveMessages.Unsupported, writer.SetCurve(AsusFan.Cpu, Curve()).Message);
    }

    [Fact]
    public void AnInvalidCurveIsRefusedBeforeTheBus()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.CpuFanCurve);
        var writer = new AsusWindowsFanCurvesWriter(fake.Device, _ => true);

        var bad = new AsusFanCurve(AsusFan.Cpu, [new(30, 10), new(40, 20)], false);
        Assert.Equal(AsusFanCurveMessages.EightPoints, writer.SetCurve(AsusFan.Cpu, bad).Message);
    }

    // ---- GPU MUX (shared IGpuMux) ----

    [Fact]
    public void TheMuxReadsAndQueuesWithoutApplyingImmediately()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.GpuMux);
        fake.Values[AsusAtk.GpuMux] = 1;   // Optimus now

        var mux = AsusWindowsGpuMux.Create(fake.Device, vivo: false);

        Assert.True(mux.Supported);
        var before = mux.Read();
        Assert.Equal("1", before.Current!.Id);
        Assert.Null(before.Pending);

        var change = mux.Request("0");   // ask for discrete

        Assert.True(change.Ok);
        Assert.True(change.Queued);                    // queued: the panel re-routes on the next boot
        Assert.Equal(0, fake.Values[AsusAtk.GpuMux]);  // the firmware value was written...
        var after = mux.Read();
        Assert.Equal("0", after.Current!.Id);          // ...and now reads back as the current mode,
        Assert.Null(after.Pending);                    // so it is normalized to no pending change
        Assert.False(after.RebootRequired);
    }

    /// <summary>Requesting the mode the firmware value already reports is a NO-OP: no write, no pending and no
    /// restart — the fix for the "same value still shows the after-restart result" report.</summary>
    [Fact]
    public void RequestingTheCurrentWindowsMuxModeIsANoOp()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.GpuMux);
        fake.Values[AsusAtk.GpuMux] = 1;   // Optimus now
        var mux = AsusWindowsGpuMux.Create(fake.Device, vivo: false);

        var change = mux.Request("1");     // ask for the mode it is already in

        Assert.True(change.Ok);
        Assert.True(change.NoOp);
        Assert.False(change.Queued);
        Assert.Equal(1, fake.Values[AsusAtk.GpuMux]);  // the firmware value was NOT rewritten
        var state = mux.Read();
        Assert.Null(state.Pending);
        Assert.False(state.RebootRequired);
    }

    /// <summary>A firmware value outside the 0/1 enum is UNKNOWN, so the request proceeds as a real change
    /// rather than being guessed to be a no-op.</summary>
    [Fact]
    public void AnUnreadableWindowsMuxCurrentModeIsNotTreatedAsANoOp()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.GpuMux);
        fake.Values[AsusAtk.GpuMux] = 2;   // not a known mode -> unknown current
        var mux = AsusWindowsGpuMux.Create(fake.Device, vivo: false);

        var change = mux.Request("0");

        Assert.True(change.Queued);
        Assert.False(change.NoOp);
        Assert.Equal(0, fake.Values[AsusAtk.GpuMux]);
    }

    [Fact]
    public void AnUnknownMuxModeIsRefused()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.GpuMux);
        var mux = AsusWindowsGpuMux.Create(fake.Device, false);

        Assert.Equal(GpuMuxMessages.UnknownMode, mux.Request("9").Error);
    }

    [Fact]
    public void AnUnsupportedMuxRefusesCleanly()
    {
        var mux = AsusWindowsGpuMux.Create(new FakeAtk().Device, false);

        Assert.False(mux.Supported);
        Assert.Empty(mux.Modes);
        Assert.Equal(GpuMuxMessages.Unsupported, mux.Request("0").Error);
    }

    // ---- TUF ATK RGB ----

    [Fact]
    public void TheTufRgbDeviceIsProbed()
    {
        Assert.Null(AsusAtkRgbController.TryCreate(new FakeAtk().Device));

        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.TufRgbMode);
        var controller = AsusAtkRgbController.TryCreate(fake.Device);
        Assert.NotNull(controller);

        var zone = Assert.Single(controller!.Zones);
        Assert.Equal("Keyboard", zone.Name);
        Assert.NotEmpty(zone.Effects);
    }

    [Fact]
    public void ApplyingATufRgbEffectSendsTheSixBytePacket()
    {
        var fake = new FakeAtk();
        fake.Present.Add(AsusAtk.TufRgbMode);
        var zone = AsusAtkRgbController.TryCreate(fake.Device)!.Zones.Single();
        var effect = zone.Effects.First(e => e.Name == "Static");

        Assert.True(zone.ApplyEffect(effect, brightness: 100, speed: 100, direction: 1, color: new AccentColor(10, 20, 30)));

        var packet = fake.Buffers[AsusAtk.TufRgbMode];
        Assert.Equal([0xb4, 0, 10, 20, 30, 2], packet);   // [0xb4][mode][r][g][b][speed=2 (high)]
    }

    // ---- quirks ----

    [Theory]
    [InlineData("ROG Strix G16", "ASUS ROG/TUF", false)]
    [InlineData("ASUS TUF Gaming F15", "ASUS ROG/TUF", false)]
    [InlineData("VivoBook 15 X515", "ASUS VivoBook/ZenBook", true)]
    [InlineData("Zenbook 14", "ASUS VivoBook/ZenBook", true)]
    [InlineData(null, "ASUS", false)]
    [InlineData("Unknown Board", "ASUS", false)]
    public void TheQuirksTablePicksTheModel(string? product, string name, bool vivo)
    {
        var model = AsusModels.Detect(product);

        Assert.Equal(name, model.Name);
        Assert.Equal(vivo, model.Vivo);
    }

    // ---- the wiring (read as text: constructing the real device would open \\.\ATKACPI) ----

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The Windows backend attaches each ATK port, and — the safety half — references NOTHING from the
    /// Linux/asusd path, so the two transports can never write the same machine.</summary>
    [Fact]
    public void TheWindowsBackendWiresTheAtkPortsAndNothingFromAsusd()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Infrastructure", "Vendors", "Asus", "AsusDevice.Windows.cs"));

        foreach (var wired in new[]
                 {
                     "AsusAtkChannel.TryOpen", "AsusWindowsProfiles.TryCreate", "AsusWindowsChargeLimit.TryCreate",
                     "AsusWindowsPanelOverdrive.TryCreate", "AsusWindowsGpuMux.Create", "AsusAtkRgbController.TryCreate",
                 })
            Assert.Contains(wired, source, StringComparison.Ordinal);

        Assert.DoesNotContain("Busctl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Asusd", source, StringComparison.Ordinal);
    }
}
