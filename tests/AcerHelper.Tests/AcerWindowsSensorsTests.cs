using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// WHAT THE WINDOWS BACKEND REPORTS FROM ITS SENSOR CHANNEL — the owner's report pinned where it happens.
///
/// The report was "GPU часто показывает 0 градусов". The channel behind it (Acer's gaming-WMI
/// <c>GetGamingSysInfo</c> — the same EC sensors Linux reads as the acer hwmon chip's temp1/temp2) answers 0
/// while it has nothing to say, and every one of those zeros reached the Monitor AS A TEMPERATURE, because
/// <c>Domain/Models.cs</c> defines 0 as a reading and only -1 as unavailable. The rule that fixes it is shared
/// and pinned by <see cref="AcerTemperatureHoldTests"/>; what could NOT be pinned there is the WIRING — that the
/// Windows rows go through it, one hold per channel, and that the fan rows do not. That is what this file drives.
///
/// THE SEAM, and why the snapshot takes a delegate at all: <c>WmiInvoker</c> is sealed, has no port interface and
/// opens a real WMI session in its constructor, so nothing here could reach <c>AcerDevice.Windows</c>'s snapshot if
/// the sysinfo read were a direct call — the same conclusion <c>AcerProfilePorts</c> and <c>AcerFanPort</c> reach
/// about their own I/O. It is injected, and these rows pass a fake EC channel.
///
/// A FAN'S 0 IS NOT A TEMPERATURE'S 0, and the two halves of this file are the two sides of that: an idle
/// laptop's fans legitimately read 0 rpm (measured on the AN18-61 — they stop at idle), so holding a fan row
/// would show a speed the fan is not running at, while a 0 on a temperature row is the channel declining to
/// answer. Only the two temperatures are held.
/// </summary>
public class AcerWindowsSensorsTests
{
    /// <summary>The EC's own sysinfo ids, spelled as numbers ON PURPOSE: the cross-OS pairing rests on them
    /// (0x01/0x0A are the kernel's temp1/temp2 and 0x02/0x06 are fan1/fan2 — see <c>AcerFanChannels</c>), and a
    /// test that read them out of the production class could not notice them being transposed.</summary>
    private const ulong CpuTemp = 0x01, GpuTemp = 0x0A, CpuFan = 0x02, GpuFan = 0x06;

    /// <summary>The EC channel, as the snapshot sees it: an id and a word/byte flag in, a value out. A channel
    /// that was never answered and one that answered -1 are deliberately the SAME here, because they are the same
    /// thing to the production read — <c>Sensor</c> collapses a non-zero EC status and a failed WMI call into the
    /// one "no value" the contract knows.</summary>
    private sealed class FakeSysInfo
    {
        private readonly Dictionary<ulong, int> _values = [];

        /// <summary>What this channel answers from now on.</summary>
        public void Answer(ulong id, int value) => _values[id] = value;

        public int Read(ulong id, bool word) => _values.TryGetValue(id, out var v) ? v : -1;
    }

    private static (int Cpu, int Gpu) Temps(SensorSnapshot s) => (s.CpuTempC, s.GpuTempC);

    /// <summary>Each row is read from its OWN id, and the two ids that are one transposition apart are not
    /// swapped: a swap would report the CPU's temperature as the GPU's, on both rows, silently.
    ///
    /// MUTATION THAT REDDENS IT: <c>private const ulong CpuTemp = 0x01, GpuTemp = 0x0A;</c> ->
    /// <c>0x0A, 0x01</c> in <c>AcerSysInfoSensors</c>.</summary>
    [Fact]
    public void EachChannelIsReadFromItsOwnSysinfoId()
    {
        var ec = new FakeSysInfo();
        ec.Answer(CpuTemp, 45);
        ec.Answer(GpuTemp, 50);
        ec.Answer(CpuFan, 2400);
        ec.Answer(GpuFan, 2500);

        var s = new AcerSysInfoSensors(ec.Read).Read();

        Assert.Equal(45, s.CpuTempC);
        Assert.Equal(50, s.GpuTempC);
        Assert.Equal([new FanReading("CPU", 2400), new FanReading("GPU", 2500)], s.Fans);
    }

    /// <summary>The reported bug, in the measured shape: the GPU channel drops out for a stretch and comes back
    /// (41, 40, 41, 40, 40, 40, 0, 0 on the Linux half), and the row holds its own last answer through it instead
    /// of dipping to 0 or to anything else. Each channel's memory is its own, so a CPU zero holds the CPU's value
    /// and not the GPU's.
    ///
    /// MUTATION THAT REDDENS IT: bypass the hold on either temperature row —
    /// <c>CpuTempC = _cpuTemp.Reading(readChannel(CpuTemp, false), -1)</c> ->
    /// <c>CpuTempC = readChannel(CpuTemp, false)</c> (and the GPU twin likewise) in <c>AcerSysInfoSensors.Read</c>.
    /// </summary>
    [Fact]
    public void AZeroOnATemperatureRowHoldsThatChannelsLastRealReading()
    {
        var ec = new FakeSysInfo();
        var sensors = new AcerSysInfoSensors(ec.Read);

        ec.Answer(CpuTemp, 45);
        ec.Answer(GpuTemp, 50);
        Assert.Equal((45, 50), Temps(sensors.Read()));

        ec.Answer(GpuTemp, 0);
        Assert.Equal((45, 50), Temps(sensors.Read()));   // held, and held however long the stretch runs
        Assert.Equal((45, 50), Temps(sensors.Read()));

        ec.Answer(CpuTemp, 0);
        Assert.Equal((45, 50), Temps(sensors.Read()));   // the CPU holds its own 45, never the GPU's 50

        ec.Answer(CpuTemp, 41);
        ec.Answer(GpuTemp, 39);
        Assert.Equal((41, 39), Temps(sensors.Read()));   // a real reading replaces the held one

        ec.Answer(CpuTemp, 0);
        ec.Answer(GpuTemp, 0);
        Assert.Equal((41, 39), Temps(sensors.Read()));
    }

    /// <summary>A zero on a channel that has NEVER answered has nothing to hold, and on Windows there is nothing
    /// to substitute either: the generic base wires no sensors at all, so -1 is the honest answer and the row
    /// hides. What must not happen — and did — is that 0 reaches the UI as a temperature.
    ///
    /// MUTATION THAT REDDENS IT: the same bypass as above; <c>readChannel(CpuTemp, false)</c> returns 0 where the
    /// contract says unavailable.</summary>
    [Fact]
    public void AZeroWithNothingRememberedIsUnavailableNotZero()
    {
        var ec = new FakeSysInfo();
        ec.Answer(CpuTemp, 0);
        ec.Answer(GpuTemp, 0);

        var s = new AcerSysInfoSensors(ec.Read).Read();

        Assert.Equal(-1, s.CpuTempC);
        Assert.Equal(-1, s.GpuTempC);
    }

    /// <summary>An absent channel is "no value" exactly like a zero — a failed WMI call and a non-zero EC status
    /// are the same -1 out of the read, and the rule must not have an exception for the one that does not trigger
    /// in practice. So: unavailable before anything is remembered, held after.
    ///
    /// MUTATION THAT REDDENS IT: the bypass above; the third read reports the absent -1 where 45 is held.</summary>
    [Fact]
    public void AnAbsentChannelIsUnavailableThenHolds()
    {
        var ec = new FakeSysInfo();
        var sensors = new AcerSysInfoSensors(ec.Read);

        Assert.Equal((-1, -1), Temps(sensors.Read()));   // never answered — nothing to hold, nothing to stand in

        ec.Answer(CpuTemp, 45);
        Assert.Equal((45, -1), Temps(sensors.Read()));

        ec.Answer(CpuTemp, -1);                          // the WMI call itself fails
        Assert.Equal((45, -1), Temps(sensors.Read()));
    }

    /// <summary>One hold per channel, asserted at the wiring rather than at the type: a GPU channel that has never
    /// answered must not inherit the CPU's memory. Sharing one instance would do exactly that, and the first
    /// answer the GPU row ever gave would be the CPU's temperature.
    ///
    /// MUTATION THAT REDDENS IT: hand the GPU row the CPU's hold —
    /// <c>GpuTempC = _gpuTemp.Reading(...)</c> -> <c>_cpuTemp.Reading(...)</c> in <c>AcerSysInfoSensors.Read</c>.</summary>
    [Fact]
    public void TheMemoryIsTheChannelsOwn()
    {
        var ec = new FakeSysInfo();
        var sensors = new AcerSysInfoSensors(ec.Read);

        ec.Answer(CpuTemp, 60);
        Assert.Equal((60, -1), Temps(sensors.Read()));

        ec.Answer(GpuTemp, 0);
        Assert.Equal((60, -1), Temps(sensors.Read()));   // not 60: the GPU row has no memory to borrow
    }

    /// <summary>The two fan channels are read RAW, at whatever they say. 0 rpm is a real reading — the fans stop
    /// at idle — and it must survive as one; a fan whose read fails stays unavailable rather than borrowing the
    /// other fan's speed or the CPU temperature held beside it.
    ///
    /// MUTATION THAT REDDENS IT: put a fan row through a hold —
    /// <c>new FanReading("CPU", readChannel(CpuFan, true))</c> ->
    /// <c>new FanReading("CPU", _cpuTemp.Reading(readChannel(CpuFan, true), -1))</c> in <c>AcerSysInfoSensors.Read</c>.</summary>
    [Fact]
    public void AFanReadingZeroRpmIsReportedAsZero()
    {
        var ec = new FakeSysInfo();
        var sensors = new AcerSysInfoSensors(ec.Read);

        ec.Answer(CpuFan, 0);
        ec.Answer(GpuFan, 0);
        Assert.Equal([new FanReading("CPU", 0), new FanReading("GPU", 0)], sensors.Read().Fans);

        ec.Answer(CpuFan, 2400);
        ec.Answer(GpuFan, -1);
        Assert.Equal([new FanReading("CPU", 2400), new FanReading("GPU", -1)], sensors.Read().Fans);
    }

    /// <summary>
    /// THE BACKEND IS ACTUALLY WIRED TO THAT CLASS — the hole every row above cannot see.
    ///
    /// They all drive <c>AcerSysInfoSensors</c> directly, so a backend that stopped using it — the reported bug's
    /// expression put back at the call site — would leave them all green. The Linux half has the same hole and
    /// closes it by reading its own source as text (<see cref="AcerLinuxWiringTests"/>), because
    /// <c>AcerDevice.Linux</c> is not compiled into this suite at all; here the file IS compiled, but
    /// <c>AcerDevice</c> still cannot be constructed without a real WMI session, so the same technique is the only
    /// one that reaches it.
    ///
    /// WHAT IT IS WORTH, stated so it is not over-read: it pins that the wiring is PRESENT, not that it behaves —
    /// the rows above pin the behaviour. What it catches is the one-line revert that would otherwise be invisible
    /// to the whole suite.
    ///
    /// MUTATION THAT REDDENS IT: deleting <c>Sensors = new SensorsPort(new AcerSysInfoSensors(Sensor).Read);</c>
    /// from <c>AcerDevice.Windows.InitVendor</c>.
    /// </summary>
    [Fact]
    public void TheWindowsBackendBuildsItsSnapshotThroughTheHold()
        => Assert.Contains("new SensorsPort(new AcerSysInfoSensors(Sensor).Read)",
                           Source("Infrastructure/Vendors/Acer/AcerDevice.Windows.cs"), StringComparison.Ordinal);

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree.</summary>
    private static string Source(string relativePath, [CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }
}
