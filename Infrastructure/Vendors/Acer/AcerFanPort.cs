using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// The Acer fan-control port, the channel table it shares with the sensors override, the encodings the two of
// them need (mode map, duty scale, chip identity, and the rule that decides what a temperature row carries) and
// the Windows snapshot that rule is applied in — in an UN-SUFFIXED file deliberately, for the reason
// AcerProfilePorts.cs states at length: the test project targets net10.0-windows while AcerHelper.csproj excludes
// **/*.Linux.cs from that TFM, so policy left in AcerDevice.Linux.cs cannot be reached by the suite at all. What
// stays there is the I/O — enumerating /sys/class/hwmon, resolving a class symlink, probing write permission — and
// it arrives here as already-resolved paths plus one write delegate, the shape DelegatePorts.cs uses.
//
// IT DOES NOT EXTEND HwmonFanControl, and that is a decision rather than an oversight. That class drives a bare
// pwmN of WHATEVER chip it finds, is single-channel, and — the part that settles it — lives in Hwmon.Linux.cs, so
// putting THIS mode encoding there would move the most dangerous number of the whole feature (see AcerFanEnable)
// into a file no test can compile. The two coexist: HwmonFanControl stays the generic fallback for chips that
// expose a plain PWM channel, this is the Acer path.

/// <summary>
/// The Acer fan channels, in the order the UI lists them and the order their writes go out: channel 1 is the CPU
/// fan, channel 2 the GPU one.
///
/// The numbering is the KERNEL's, and it is also the EC's — which is why this table can be shared by the fan port
/// and the sensor override instead of each spelling its own. acer-wmi's own tables are
/// <c>fan_channel_to_fan_id = {1, 4}</c> (the WMI fan ids, the same ones <c>AcerDevice.Windows</c> passes to
/// SetGamingFanSpeed for CPU 0x01 / GPU 0x04) and <c>fan_channel_to_sensor_id = {2, 6}</c> (the sysinfo ids the
/// Windows backend reads the same two fans' RPMs from). One channel is therefore one physical fan on both OSes,
/// and <see cref="Channel.Label"/> is the string Windows shows for it: that pairing is a cross-OS display
/// contract, not a local habit, so it is written down once.
/// </summary>
internal static class AcerFanChannels
{
    /// <param name="Number">The channel number as it appears in the hwmon file names (<c>fanN_input</c>,
    /// <c>pwmN</c>, <c>pwmN_enable</c>).</param>
    /// <param name="Label">What the UI shows for this fan — byte-for-byte what the Windows backend reports.</param>
    /// <param name="WmiId">The WMI fan id of the same physical fan (Windows' SetGamingFanSpeed argument). Not
    /// used on Linux — nothing here speaks WMI — and kept because it is what makes "the same fan" checkable
    /// rather than asserted: it is the column that ties this table to the kernel's and to Windows'.</param>
    internal readonly record struct Channel(int Number, string Label, byte WmiId);

    internal static readonly Channel Cpu = new(1, "CPU", 0x01);
    internal static readonly Channel Gpu = new(2, "GPU", 0x04);

    /// <summary>CPU first — the order both the sensor list and the fan writes use, so the two cannot come to
    /// disagree about which number is which fan.</summary>
    internal static readonly IReadOnlyList<Channel> All = [Cpu, Gpu];
}

/// <summary>
/// The three <see cref="FanMode"/>s as the acer chip's <c>pwmN_enable</c> node spells them.
///
/// THE TRAP, and the reason this is a named function in a tested file rather than a cast or a switch at the call
/// site: the app's values and the kernel's are two DIFFERENT sets that happen to be the same small integers.
/// <see cref="FanMode"/> is Auto = 1, Max = 2, Custom = 3 (Domain/Models.cs), while the node takes TURBO = 0,
/// CUSTOM = 1, AUTO = 2 (acer-wmi's hwmon write path maps 0/1/2 onto ACER_WMID_FAN_MODE_TURBO / CUSTOM / AUTO).
/// An identity mapping — the obvious <c>((byte)mode).ToString()</c>, or the "1 means manual" intuition a generic
/// PWM background gives — would put the fans in TURBO whenever the user asked for Auto: full speed, permanently,
/// silently, on the loudest setting the machine has. So the map is written out, and the test that pins it is named
/// after the trap rather than after the table.
///
/// "2" for Auto is not a mistake either: 2 is the kernel's AUTO, i.e. the fans follow the thermal profile the EC
/// is running, which is what <see cref="FanMode.Auto"/> means on Windows (SetGamingFanBehavior with behaviour 1).
/// </summary>
internal static class AcerFanEnable
{
    /// <summary>The node value for a mode, or false for a value the enum does not name. Refusing is the second
    /// door, not the first: <c>FanModes.FromStored</c> already refuses an undefined stored value, but
    /// <see cref="FanMode"/>'s underlying type is <c>byte</c>, so a cast can still produce one and a wrong
    /// write here is a fan at full speed.</summary>
    internal static bool TryFor(FanMode mode, out string enable)
    {
        switch (mode)
        {
            case FanMode.Auto:   enable = "2"; return true;
            case FanMode.Max:    enable = "0"; return true;   // the kernel calls 0 TURBO — Max's equivalent
            case FanMode.Custom: enable = "1"; return true;
            default:             enable = "";  return false;
        }
    }
}

/// <summary>
/// A duty percentage as the acer chip's duty node takes it: the node's scale is 0..255 for 0..100%, NOT percents
/// — the driver interpolates its value linearly onto the 0..100 speed the EC's SetGamingFanSpeed takes (and
/// interpolates the read side back out the same way). Windows writes the percentage to WMI directly, so the same
/// user-facing number has to be scaled on this side; that scale is the one thing a wrong guess would get subtly
/// wrong everywhere, hence one named function instead of a literal at the call site. It is the same 0..255 scale
/// <c>HwmonFanControl</c> uses for a generic pwmN.
/// </summary>
internal static class AcerFanDuty
{
    internal static string Node(byte percent) => (Math.Clamp((int)percent, 0, 100) * 255 / 100).ToString();
}

/// <summary>
/// Which hwmon chip is the Acer fan chip — TWO conditions, both required: the chip is named <c>acer</c> (the
/// hwmon name acer-wmi registers) AND the real path of its parent device is under acer-wmi's platform device.
///
/// WHY NOT <c>Hwmon.BestFanSource</c>, which is right there and already picks a chip that has fans: that
/// heuristic answers "which chip shows this laptop's fans", which is the right question for read-only TELEMETRY
/// and the wrong one here. It would bind a CONTROL — writes into a fan's behaviour register — to whichever chip
/// happens to expose the most fans, and on a machine carrying a second fan-exposing driver (dell_smm, an ITE
/// chip) that is not this one. The device path is the driver's identity; the name alone is a string a driver
/// chooses for itself.
/// </summary>
internal static class AcerHwmonChip
{
    internal const string Name = "acer";

    /// <summary>The platform device the chip hangs off. /sys/class/hwmon/hwmonN/device resolves to it — verified
    /// on the AN18-61, where <c>readlink</c> reports <c>../../../acer-wmi</c> and the resolved path is
    /// /sys/devices/platform/acer-wmi — so the comparison is an absolute-path prefix, never a bare substring
    /// (a path merely CONTAINING the name would match an unrelated device whose directory happens to spell it).
    /// </summary>
    internal const string DevicePrefix = "/sys/devices/platform/acer-wmi";

    internal static bool Matches(string? chipName, string? resolvedDevicePath)
        => chipName == Name && resolvedDevicePath != null
           && (resolvedDevicePath == DevicePrefix
               || resolvedDevicePath.StartsWith(DevicePrefix + "/", StringComparison.Ordinal));
}

/// <summary>
/// What a temperature row carries, and the short memory the EC's intermittent channel needs in order to answer.
///
/// MEASURED INTERMITTENCY — this is why it is a holder with state and not an expression. The EC's channels do not
/// read "0 or a value" as a stable property of the machine: the GPU channel (<c>temp2_input</c>, the EC's
/// <c>predator_v4_sensor_id</c> 0x0A) sampled eight times, 4 s apart, gave 41, 40, 41, 40, 40, 40, 0, 0 — six real
/// readings and then two zeros — while earlier in the same session the same channel read 0 for twelve consecutive
/// samples (≥60 s), and at another moment 36-39 °C. A zero there means "this sensor is not answering THIS
/// instant", not "the temperature fell to nothing". (The owner's report was the first symptom: "gpu показывает 0
/// градусов, когда, видимо, отключается".)
///
/// THE RULE. A real EC reading (&gt; 0) is used AND remembered; a zero uses the last remembered EC reading, because
/// a few-seconds-old temperature is still the right information for a thermally inert object and it is the RIGHT
/// SENSOR, unlike a substitute; with nothing remembered yet — the channel has never answered, or the node is
/// absent, which is the same "no value" here — the generic source stands in; and with nothing anywhere the row
/// carries -1, which <c>Domain/Models.cs</c> defines as unavailable and the UI hides.
///
/// WHY THE HELD VALUE BEATS THE SUBSTITUTE. Without the memory the two answers alternate as the channel drops in
/// and out: the EC's ~40 °C and the generic fallback's ~35 °C (on this machine the amdgpu edge — the integrated
/// GPU, the only GPU Linux can see, and what the app showed before this backend read the EC at all). A flickering
/// number is worse than either stable answer; the generic source remains the fallback for the one case where it is
/// the only information there is, i.e. before the channel has ever answered.
///
/// THE TRADE, stated because a long zero stretch is possible: through one, the row shows a stale-but-correct EC
/// value rather than a live reading from another sensor. That is deliberate — the sensor is the right one, and its
/// last answer is seconds old rather than wrong.
///
/// ONE INSTANCE PER CHANNEL: the memory is that channel's own, and sharing one between the CPU and GPU rows would
/// let the CPU's real reading stand in for a GPU channel that has never answered at all.
/// </summary>
internal sealed class AcerTemperatureHold
{
    private int _lastEc;   // the last real EC reading, or 0 when this channel has never answered

    /// <param name="fromEc">The EC's reading in °C this time, or -1 for a node that is absent/unreadable.</param>
    /// <param name="fromGeneric">The generic hwmon source's reading in °C, or -1 when there is none.</param>
    internal int Reading(int fromEc, int fromGeneric)
    {
        if (fromEc > 0) { _lastEc = fromEc; return fromEc; }     // a real reading wins and is remembered
        if (_lastEc > 0) return _lastEc;                         // a zero holds the channel's last real answer
        return fromGeneric > 0 ? fromGeneric : -1;               // nothing remembered -> the substitute, or -1
    }
}

/// <summary>
/// The Windows backend's sensor snapshot: the four channels of Acer's gaming-WMI <c>GetGamingSysInfo</c> method,
/// with the EC's intermittency handled by <see cref="AcerTemperatureHold"/>.
///
/// THE SAME EC SENSORS LINUX READS, and the id table is where that is checkable rather than asserted. The sysinfo
/// ids are the kernel's <c>predator_v4_sensor_id</c>: 0x01/0x0A are the acer hwmon chip's <c>temp1_input</c> /
/// <c>temp2_input</c>, and 0x02/0x06 are <c>fan1_input</c>/<c>fan2_input</c> (acer-wmi's
/// <c>fan_channel_to_sensor_id = {2, 6}</c>) — the same pairing <see cref="AcerFanChannels"/> states for the
/// fans. So the two OSes report the same four things, which is why this row set is the one
/// <c>AcerDevice.Linux.ReadAcerSensors</c> mirrors.
///
/// WHY THE TEMPERATURES GO THROUGH A HOLD AND THE FANS DO NOT. The EC's temperature channel answers 0 when it has
/// nothing to say — measured on this silicon from the Linux half: the GPU channel (0x0A) sampled eight times 4 s
/// apart gave 41, 40, 41, 40, 40, 40, 0, 0, and had read 0 for a ≥60 s stretch earlier in the same session. On
/// Windows that 0 reached the Monitor AS A TEMPERATURE, which is the owner's report ("GPU часто показывает 0
/// градусов"). A fan's 0 is the opposite case and must not be swallowed by the same rule: the AN18-61's fans STOP
/// at idle (measured), so 0 rpm is a real reading the UI has to be able to show, and holding it would show a
/// speed the fan is not running at. The two fan channels are therefore passed through untouched, at whatever the
/// channel says — including -1.
///
/// ONE HOLD PER CHANNEL, for the reason <see cref="AcerTemperatureHold"/> gives: a shared one would let the CPU's
/// real reading stand in for a GPU channel that has never answered at all.
///
/// NO GENERIC SUBSTITUTE EXISTS ON WINDOWS, and passing -1 is that finding rather than a placeholder. On Linux
/// the generic hwmon port stands in until a channel has ever answered (<c>AcerDevice.Linux.ReadAcerSensors</c>
/// hands over paths from <c>Hwmon</c>). Windows has no equivalent to hand over: the generic base wires no sensors
/// AT ALL — <c>GenericDevice.Windows.InitPlatform</c> never touches <c>Sensors</c>, so the slot stays null — and
/// nothing else in this tree reads a Windows temperature (no hwmon, no thermal-zone WMI; <c>NvidiaGpu</c> is the
/// clock-overclock port with no thermal surface). So the fallback is -1, the contract's "unavailable"
/// (<c>Domain/Models.cs</c>), which the UI renders as "—" and hides: for a channel that has never answered and
/// has no substitute, that is the honest answer, and a 0 is never what comes back instead.
///
/// WHY THE READ IS A DELEGATE: <c>WmiInvoker</c> is sealed and opens a real WMI session in its constructor, so the
/// snapshot could not be driven by any test — the same reason <c>AcerProfilePorts</c> and <c>AcerFanPort</c> take
/// their I/O as delegates. Production passes the gaming-WMI read (<c>AcerDevice.Windows.Sensor</c>); the suite
/// passes a fake.
/// </summary>
/// <param name="readChannel">One sysinfo read: the EC id, and whether the value is a 16-bit word (the fans are;
/// the temperatures are bytes).</param>
internal sealed class AcerSysInfoSensors(Func<ulong, bool, int> readChannel)
{
    // The sysinfo ids — the kernel's numbers (see the remark above), named rather than spelled at the call site:
    // 0x01/0x0A are one transposition apart, and a swapped pair would report the CPU's temperature as the GPU's,
    // silently, on both rows.
    private const ulong CpuTemp = 0x01, GpuTemp = 0x0A;
    private const ulong CpuFan = 0x02, GpuFan = 0x06;

    private readonly AcerTemperatureHold _cpuTemp = new();
    private readonly AcerTemperatureHold _gpuTemp = new();

    internal SensorSnapshot Read() => new()
    {
        CpuTempC = _cpuTemp.Reading(readChannel(CpuTemp, false), -1),
        GpuTempC = _gpuTemp.Reading(readChannel(GpuTemp, false), -1),
        Fans = [new FanReading("CPU", readChannel(CpuFan, true)),
                new FanReading("GPU", readChannel(GpuFan, true))],
    };
}

/// <summary>The four sysfs nodes this port writes: one behaviour register and one duty value per fan channel, all
/// resolved (and write-probed) by the caller — AcerDevice.Linux.cs — because finding them needs a filesystem.
/// </summary>
internal readonly record struct AcerFanNodes(string CpuEnable, string CpuDuty, string GpuEnable, string GpuDuty);

/// <summary>
/// Acer fan control through the mainline acer-wmi hwmon chip: the same EC channel the Windows backend drives with
/// SetGamingFanBehavior/SetGamingFanSpeed, reached through sysfs instead of WMI.
///
/// WHAT THE USER GETS is Windows' shape exactly: both fans, Max and Custom (<see cref="Capability"/>), with Auto
/// always implied. Auto and Max are one write each to the fans' behaviour registers; Custom is the behaviour write
/// followed by the two duties, so a curve the user configured is what the fans actually run.
///
/// THE TWO CALLS, IN THE ORDER THE APPLICATION MAKES THEM. <c>LaptopService.Fans.ApplyFan</c> puts the fans into
/// custom behaviour FIRST and pushes the speeds second, deliberately ("the EC honours a manual speed only once the
/// fan is in CUSTOM behaviour; setting the speed alone is silently ignored"), so <see cref="SetMode"/> carries the
/// behaviour write and <see cref="SetCustomSpeeds"/> carries the values. The sequence on the wire for a custom
/// curve is therefore behaviour -> CPU duty -> GPU duty, which is what the plan calls for and what
/// <c>AcerFanPortTests</c> pins by driving the port the way the service does.
/// </summary>
internal sealed class AcerFanPort(AcerFanNodes nodes, Func<string, string, (bool ok, string? error)> write)
    : IFanControl, IDisposable
{
    public string? LastError { get; private set; }

    /// <summary>Windows' capability, exactly: both fans, Max and Custom available.</summary>
    public FanCapability Capability => new(HasMax: true, HasCustom: true, HasGpuFan: true);

    /// <summary>Auto and Max write the behaviour registers and NOTHING else — no duty write, so the EC keeps
    /// whatever speed the firmware or our own last custom write left in place. That is Windows' behaviour too:
    /// its SetGamingFanBehavior carries a mode and no speed, and a Max that also invented a duty would be a
    /// different fan curve than the firmware's own turbo ramp.
    ///
    /// BOTH channels are always written, and that is a correction to "write the enable": this driver's
    /// <c>pwmN_enable</c> is PER-CHANNEL — the write path resolves the channel to its own fan-bitmap bit before
    /// calling the EC — so a single write to <c>pwm1_enable</c> would leave the GPU fan in whatever behaviour it
    /// was in. "Max" that turbos the CPU fan and leaves the GPU fan on auto is not Max, and Windows' one call
    /// carries both fans at once. Each channel is attempted regardless of the other: they are two fans' own
    /// registers, and a failure on one must not leave the other latched in the previous mode (the first error is
    /// the one reported, because LastError is read straight after a false return).
    /// </summary>
    public bool SetMode(FanMode mode)
    {
        if (!AcerFanEnable.TryFor(mode, out var enable))
        {
            LastError = $"{mode} is not a fan mode this port can encode";
            return false;
        }

        string? firstError = null;
        foreach (var path in new[] { nodes.CpuEnable, nodes.GpuEnable })
        {
            var (ok, error) = write(path, enable);
            if (!ok) firstError ??= error;
        }
        LastError = firstError;
        return firstError == null;
    }

    /// <summary>CPU then GPU, aborting when the CPU write failed — Windows' shape exactly
    /// (<c>AcerDevice.Windows.SetFanSpeeds</c>: <c>var a = GmSet(...cpu...); return a.ok ? GmSet(...gpu...) : a;</c>).
    /// The pair is ONE curve for two fans, so half of it is worse than none: a GPU duty written while the CPU's
    /// own write was refused leaves the two fans disagreeing about something the user set as a single thing.</summary>
    public bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent)
    {
        var (ok, error) = write(nodes.CpuDuty, AcerFanDuty.Node(cpuPercent));
        if (!ok) { LastError = error; return false; }

        (ok, error) = write(nodes.GpuDuty, AcerFanDuty.Node(gpuPercent));
        LastError = ok ? null : error;
        return ok;
    }

    /// <summary>DISPOSE WRITES NOTHING — deliberately, and it is the one way this port differs from
    /// <c>HwmonFanControl</c>, which hands its fan back to the firmware on exit. The difference is what sits
    /// behind the node: a generic PWM channel is not a latch (its value survives only while something keeps
    /// writing it, so leaving ours behind would strand a fan at the last duty), while this path latches the EC's
    /// fan behaviour register — the fans keep doing what the user asked for after the app exits. Windows leaves it
    /// exactly like that, and parity is the point of this port.
    ///
    /// THE ALTERNATIVE IS AN OWNER DECISION, recorded rather than forgotten: restoring AUTO (write "2" to both
    /// enables) on exit or on a settings reload. It is defensible — a user who picked Max once and then quit would
    /// get the firmware curve back — but it is NOT what Windows does, and it would silently undo a latch the user
    /// set. If it is ever wanted it belongs here, behind that decision, and this test has to be deleted to make
    /// room for it: <c>AcerFanPortTests.DisposeWritesNothing</c>.</summary>
    public void Dispose()
    {
        // Intentionally empty — see the remarks above.
    }
}
