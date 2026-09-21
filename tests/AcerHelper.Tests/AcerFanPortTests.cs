using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The Acer fan-control port: the mode encoding, the order of the writes, the capability, and the one thing it
/// deliberately does NOT do (write anything on dispose).
///
/// It is covered here rather than left to the hardware because it was put in an UN-SUFFIXED file for exactly this
/// reason: the test project targets <c>net10.0-windows</c> while the app excludes <c>**/*.Linux.cs</c> from that
/// TFM, so policy left in <c>AcerDevice.Linux.cs</c> is unreachable by the suite at all. The I/O is what stayed
/// behind there (finding the hwmon chip, resolving the nodes, probing write permission) and it arrives as paths
/// plus one write delegate — the shape <c>DelegatePorts.cs</c> already uses.
///
/// WHY THE MODE MAP GETS ITS OWN TEST, named after the trap: the app's <see cref="FanMode"/> values (1/2/3) and
/// the kernel's pwm_enable values (2/0/1) are different sets that happen to be the same small integers, so the
/// identity mapping — the obvious simplification — would turn Auto into TURBO: full fan speed, permanently, at
/// the moment the user asked for the quietest behaviour the machine has. Nothing else in this file matters as
/// much as that one table.
/// </summary>
public class AcerFanPortTests
{
    // The nodes are opaque strings to the port — it never parses them — so the tests use readable placeholders
    // rather than fake paths: what is asserted is WHICH node got WHICH value and in WHAT order.
    private const string CpuEnable = "cpu-enable";
    private const string CpuDuty   = "cpu-duty";
    private const string GpuEnable = "gpu-enable";
    private const string GpuDuty   = "gpu-duty";

    private static AcerFanPort Port(Writes writes) => new(new AcerFanNodes(CpuEnable, CpuDuty, GpuEnable, GpuDuty), writes.Write);

    /// <summary>Records every (path, value) the port writes, and decides which of them fail — the port's whole I/O
    /// surface, so a test can assert the sequence and not merely the set.</summary>
    private sealed class Writes(Func<string, bool>? fail = null)
    {
        public List<(string Path, string Value)> Log { get; } = [];
        public string[] Paths => [.. Log.Select(w => w.Path)];
        public string[] Values => [.. Log.Select(w => w.Value)];

        public (bool ok, string? error) Write(string path, string value)
        {
            Log.Add((path, value));
            return fail?.Invoke(path) == true ? (false, $"refused: {path}") : (true, null);
        }
    }

    // ---- the mode map, and the trap it exists for ----

    /// <summary>The map itself, all three modes. The kernel's pwm_enable is TURBO 0 / CUSTOM 1 / AUTO 2 — and it
    /// calls 0 TURBO, which is why <see cref="FanMode.Max"/> is the one that writes "0".</summary>
    [Theory]
    [InlineData(FanMode.Auto,   "2")]
    [InlineData(FanMode.Max,    "0")]
    [InlineData(FanMode.Custom, "1")]
    public void TheKernelSpellsTheThreeModesDifferently(FanMode mode, string expected)
    {
        Assert.True(AcerFanEnable.TryFor(mode, out var enable));
        Assert.Equal(expected, enable);
    }

    /// <summary>THE TRAP, pinned as a property rather than as three more expected strings: for NO mode does the
    /// value written equal the app's own number for it. The identity map (<c>((byte)mode).ToString()</c>) is the
    /// plausible-looking simplification — it even reads "1 means manual" to anyone coming from a generic PWM
    /// channel — and it would mean Auto = turbo, i.e. the two modes a user switches between most (Auto and Max)
    /// would send the fan to full speed.</summary>
    [Theory]
    [InlineData(FanMode.Auto)]
    [InlineData(FanMode.Max)]
    [InlineData(FanMode.Custom)]
    public void AnIdentityMapWouldMeanAutoIsTurbo(FanMode mode)
    {
        Assert.True(AcerFanEnable.TryFor(mode, out var enable));
        Assert.NotEqual(((byte)mode).ToString(), enable);
    }

    /// <summary>A mode the enum does not name writes nothing and is refused — the second door, for a value that
    /// reached the port as a cast. (The first is <c>FanModes.FromStored</c>, which refuses it before it becomes a
    /// mode.)</summary>
    [Fact]
    public void AnUndefinedModeWritesNothing()
    {
        var writes = new Writes();
        var port = Port(writes);

        Assert.False(port.SetMode((FanMode)7));

        Assert.Empty(writes.Log);
        Assert.NotNull(port.LastError);
    }

    // ---- what each mode writes ----

    /// <summary>Auto writes the two behaviour registers and NOTHING else: no duty write, so the speed the EC holds
    /// (the firmware's own curve, or our last custom write) stays where it was. Windows' behaviour write carries
    /// no speed either.</summary>
    [Fact]
    public void AutoWritesOnlyTheEnable()
    {
        var writes = new Writes();
        var port = Port(writes);

        Assert.True(port.SetMode(FanMode.Auto));

        Assert.Equal([CpuEnable, GpuEnable], writes.Paths);
        Assert.Equal(["2", "2"], writes.Values);
    }

    /// <summary>Max is the same shape with the turbo value — and BOTH channels, because this driver's
    /// pwmN_enable is per-channel: writing only pwm1_enable would turbo the CPU fan and leave the GPU fan in the
    /// behaviour it happened to be in, which is not what Max means on Windows.</summary>
    [Fact]
    public void MaxWritesOnlyTheEnable_ForBothFans()
    {
        var writes = new Writes();
        var port = Port(writes);

        Assert.True(port.SetMode(FanMode.Max));

        Assert.Equal([CpuEnable, GpuEnable], writes.Paths);
        Assert.Equal(["0", "0"], writes.Values);
    }

    /// <summary>The custom sequence, driven the way the application drives it: LaptopService.Fans.ApplyFan puts
    /// the fans into custom behaviour FIRST and pushes the speeds second (the EC ignores a manual speed until the
    /// behaviour is custom), so the wire order for a custom curve is enable -> CPU duty -> GPU duty.</summary>
    [Fact]
    public void CustomWritesTheEnableThenCpuThenGpu()
    {
        var writes = new Writes();
        var port = Port(writes);

        Assert.True(port.SetMode(FanMode.Custom));
        Assert.True(port.SetCustomSpeeds(cpuPercent: 40, gpuPercent: 80));

        Assert.Equal([CpuEnable, GpuEnable, CpuDuty, GpuDuty], writes.Paths);
        Assert.Equal(["1", "1", "102", "204"], writes.Values);   // 40% and 80% on the node's 0..255 scale
    }

    /// <summary>The split is deliberate and this is where it is written down: the behaviour write belongs to
    /// <see cref="IFanControl.SetMode"/>, so <see cref="IFanControl.SetCustomSpeeds"/> on its own issues no enable
    /// write. The caller must have switched to custom first — one caller exists (LaptopService.Fans.ApplyFan) and
    /// it does — and teaching the port to infer it would mean it holding state the hardware already holds.</summary>
    [Fact]
    public void TheSpeedsAloneWriteNoEnable()
    {
        var writes = new Writes();
        var port = Port(writes);

        Assert.True(port.SetCustomSpeeds(cpuPercent: 100, gpuPercent: 0));

        Assert.Equal([CpuDuty, GpuDuty], writes.Paths);
    }

    // ---- failure handling: Windows' shape, and what a failure must NOT do ----

    /// <summary>A refused CPU duty does not write the GPU one — Windows' shape exactly
    /// (<c>AcerDevice.Windows.SetFanSpeeds</c>: <c>a.ok ? GmSet(...gpu...) : a</c>). The pair is ONE curve for two
    /// fans, so half of it is worse than none: the GPU would run a duty the CPU fan knows nothing about.</summary>
    [Fact]
    public void AFailedCpuDutyWritesNoGpuDuty()
    {
        var writes = new Writes(path => path == CpuDuty);
        var port = Port(writes);

        Assert.False(port.SetCustomSpeeds(cpuPercent: 50, gpuPercent: 60));

        Assert.Equal([CpuDuty], writes.Paths);
        Assert.Contains(CpuDuty, port.LastError);
    }

    /// <summary>A failed enable writes no DUTY — the mode is what the duty's meaning depends on, so a duty
    /// written while the behaviour register refused would be a speed the fans may not even be in the mode to
    /// honour. The OTHER channel's enable is still attempted: the two are separate fans' registers, and leaving
    /// the second fan latched in the previous behaviour because the first fan's write failed is the worse
    /// outcome — the failure is reported either way.</summary>
    [Fact]
    public void AFailedEnableWritesNoDuty()
    {
        var writes = new Writes(path => path == CpuEnable);
        var port = Port(writes);

        Assert.False(port.SetMode(FanMode.Max));

        Assert.Equal([CpuEnable, GpuEnable], writes.Paths);
        Assert.DoesNotContain(writes.Paths, p => p is CpuDuty or GpuDuty);
        Assert.Contains(CpuEnable, port.LastError);
    }

    /// <summary>LastError is the failed write's own reason, and a later success clears it — it is read straight
    /// after a false return, so a stale reason must not survive one.</summary>
    [Fact]
    public void LastErrorIsTheFailedWritesReason_AndIsClearedBySuccess()
    {
        var failing = true;
        var writes = new Writes(path => failing && path == GpuDuty);
        var port = Port(writes);

        Assert.False(port.SetCustomSpeeds(cpuPercent: 10, gpuPercent: 20));
        Assert.Contains(GpuDuty, port.LastError);

        failing = false;
        Assert.True(port.SetCustomSpeeds(cpuPercent: 10, gpuPercent: 20));
        Assert.Null(port.LastError);
    }

    // ---- capability, and the dispose that writes nothing ----

    /// <summary>Windows' capability, exactly: both fans, Max and Custom. A narrower one would hide rows the
    /// hardware can drive.</summary>
    [Fact]
    public void TheCapabilityIsWindows()
    {
        var capability = Port(new Writes()).Capability;

        Assert.True(capability.HasMax);
        Assert.True(capability.HasCustom);
        Assert.True(capability.HasGpuFan);
    }

    /// <summary>DISPOSE WRITES NOTHING, and this test exists to make the one difference from
    /// <c>HwmonFanControl</c> a decision rather than an omission: that class restores AUTO on exit because a
    /// generic PWM channel is not a latch, while this path latches the EC's behaviour register and Windows leaves
    /// the latch exactly as the user set it. Whoever adds a restore (the alternative is recorded in the port's own
    /// remarks) has to delete this test to do it.</summary>
    [Fact]
    public void DisposeWritesNothing()
    {
        var writes = new Writes();
        var port = Port(writes);
        port.SetMode(FanMode.Max);
        writes.Log.Clear();   // the arrange step above is not what is being asserted

        port.Dispose();

        Assert.Empty(writes.Log);
    }

    // ---- the two encodings that are not the mode map ----

    /// <summary>The duty node's scale is 0..255 for 0..100%, not percents (the driver interpolates its value onto
    /// the 0..100 speed the EC takes). Windows writes the percentage straight into the WMI argument, so a port
    /// that passed the percentage through here would drive every fan at ~40% of what the user asked for — the kind
    /// of error that looks like "the curve is a bit off" rather than like a bug.</summary>
    [Theory]
    [InlineData(0, "0")]
    [InlineData(50, "127")]
    [InlineData(100, "255")]
    [InlineData(200, "255")]   // clamped, not wrapped: the domain already clamps, this is the second door
    public void TheDutyNodeTakesZeroToTwoHundredFiftyFive(byte percent, string expected)
        => Assert.Equal(expected, AcerFanDuty.Node(percent));

    /// <summary>The channel table, complete: CPU first, the kernel's channel numbers, the labels Windows shows
    /// and the WMI fan ids of the same two physical fans. It is shared with the sensors override, so this is what
    /// keeps the sensor list and the fan writes talking about the same fan.</summary>
    [Fact]
    public void TheChannelTableIsTheKernels()
    {
        Assert.Equal(2, AcerFanChannels.All.Count);

        Assert.Equal((1, "CPU", (byte)0x01), (AcerFanChannels.All[0].Number, AcerFanChannels.All[0].Label, AcerFanChannels.All[0].WmiId));
        Assert.Equal((2, "GPU", (byte)0x04), (AcerFanChannels.All[1].Number, AcerFanChannels.All[1].Label, AcerFanChannels.All[1].WmiId));

        Assert.Equal(AcerFanChannels.Cpu, AcerFanChannels.All[0]);   // CPU first, in that order
        Assert.Equal(AcerFanChannels.Gpu, AcerFanChannels.All[1]);
    }

    /// <summary>Which hwmon chip the Acer path binds to: the one NAMED acer whose device resolves under acer-wmi's
    /// platform device. Both halves are asserted because either alone is wrong — a name is a string a driver
    /// chooses, and a path prefix without the name would accept any chip on that device.</summary>
    [Theory]
    [InlineData("acer", "/sys/devices/platform/acer-wmi", true)]                              // the device itself
    [InlineData("acer", "/sys/devices/platform/acer-wmi/hwmon/hwmon10", true)]                 // or below it
    [InlineData("acpi", "/sys/devices/platform/acer-wmi/hwmon/hwmon10", false)]                // right device, not our chip
    [InlineData("acer", "/sys/devices/platform/AMDI0103:00/hwmon/hwmon5", false)]              // the AMD handler's chip
    [InlineData("acer", "/sys/devices/platform/acer-wmi-other/hwmon/hwmon4", false)]           // a prefix that only LOOKS like one
    [InlineData("acer", null, false)]                                                          // no resolvable device path
    [InlineData(null, "/sys/devices/platform/acer-wmi", false)]
    public void TheChipIsAcerOnTheAcerWmiDevice(string? name, string? devicePath, bool expected)
        => Assert.Equal(expected, AcerHwmonChip.Matches(name, devicePath));
}
