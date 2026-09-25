namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS Windows ATK ACPI wire — PURE, no I/O. The transport (`\\.\ATKACPI` and DeviceIoControl) is a
// delegate so every buffer and every decode is unit-testable without a machine.
//
// SOURCES (both public, and both transcribe the same ACPI methods):
//   * Linux `drivers/platform/x86/asus-wmi.c` + `include/linux/platform_data/x86/asus-wmi.h` — the authoritative
//     device IDs (`ASUS_WMI_DEVID_*`), the method names (`ASUS_WMI_METHODID_DEVS`/`DSTS`), the management GUID
//     (`97845ED0-4E6D-11DE-8A39-0800200C9A66`) and the DSTS presence bit (`ASUS_WMI_DSTS_PRESENCE_BIT`).
//   * G-Helper `app/AsusACPI.cs` — the Windows transport: `\\.\ATKACPI`, IOCTL `0x0022240C`, the 8-byte call
//     header ([method u32 LE][args length u32 LE][args…]), the 16-byte output, and the `- 0x10000` presence
//     decode. The two agree on every ID this file carries.
//
// NO ID HERE IS INVENTED. An implementation that needs a device ID this file does not name must add it with its
// source; nothing guesses.
//
// UNVERIFIED ON HARDWARE: no ASUS laptop was available to this change. The buffers, decodes and mappings are
// pinned by tests; the device never saw a call.

/// <summary>The ATK ACPI protocol: buffer builders and response decoders.</summary>
internal static class AsusAtk
{
    /// <summary>The `DeviceIoControl` code G-Helper uses on `\\.\ATKACPI` (G-Helper: `CONTROL_CODE`).</summary>
    internal const uint ControlCode = 0x0022240C;

    /// <summary>Method-name constants, little-endian ASCII of the Linux `ASUS_WMI_METHODID_*` values:
    /// `DEVS` = "DEVS", `DSTS` = "DSTS".</summary>
    internal const uint MethodDevs = 0x53564544;   // ASUS_WMI_METHODID_DEVS
    internal const uint MethodDsts = 0x53545344;   // ASUS_WMI_METHODID_DSTS

    /// <summary>`ASUS_WMI_DSTS_PRESENCE_BIT`: a supported device sets bit 16 in its DSTS reply.</summary>
    internal const uint PresenceBit = 0x00010000;

    // ---- device IDs (source: asus-wmi.h ASUS_WMI_DEVID_*, cross-checked against G-Helper AsusACPI.cs) ----
    internal const uint ThermalPolicy = 0x00120075;        // profiles; G-Helper PerformanceMode
    internal const uint ThermalPolicyVivo = 0x00110019;    // VivoBook/Zenbook variant
    internal const uint BatteryRsoc = 0x00120057;          // max charge %
    internal const uint GpuMux = 0x00090016;               // 0 = dGPU, 1 = Optimus
    internal const uint GpuMuxVivo = 0x00090026;
    internal const uint DgpuDisable = 0x00090020;
    internal const uint PanelOverdrive = 0x00050019;
    internal const uint PanelOverdriveSupported = 0x00050020;
    internal const uint CpuFanCurve = 0x00110024;
    internal const uint GpuFanCurve = 0x00110025;
    internal const uint MidFanCurve = 0x00110032;
    internal const uint CpuFanRpm = 0x00110013;
    internal const uint GpuFanRpm = 0x00110014;
    internal const uint MidFanRpm = 0x00110031;
    internal const uint CpuTemp = 0x00120094;
    internal const uint GpuTemp = 0x00120097;
    internal const uint TufRgbMode = 0x00100056;           // TUF keyboard RGB (ATK, not HID)
    internal const uint TufRgbState = 0x00100057;

    /// <summary>Windows performance-mode values (G-Helper `PerformanceBalanced/Turbo/Silent`).</summary>
    internal const int ModeBalanced = 0, ModeTurbo = 1, ModeSilent = 2;

    // ---- buffers ----

    /// <summary>The full call buffer the device is handed: the method name, the argument length, then the
    /// argument bytes (G-Helper `CallMethod`).</summary>
    internal static byte[] CallBuffer(uint method, byte[] args)
    {
        var buffer = new byte[8 + args.Length];
        WriteU32(buffer, 0, method);
        WriteU32(buffer, 4, (uint)args.Length);
        args.CopyTo(buffer, 8);
        return buffer;
    }

    /// <summary>The `DSTS` arguments: the device id, then an optional status word (0 for a plain read).</summary>
    internal static byte[] GetArgs(uint deviceId, uint status = 0)
    {
        var args = new byte[8];
        WriteU32(args, 0, deviceId);
        WriteU32(args, 4, status);
        return args;
    }

    /// <summary>The `DEVS` arguments: the device id, then the value.</summary>
    internal static byte[] SetArgs(uint deviceId, int value)
    {
        var args = new byte[8];
        WriteU32(args, 0, deviceId);
        WriteU32(args, 4, unchecked((uint)value));
        return args;
    }

    internal static byte[] DstsCall(uint deviceId, uint status = 0) => CallBuffer(MethodDsts, GetArgs(deviceId, status));
    internal static byte[] DevsCall(uint deviceId, int value) => CallBuffer(MethodDevs, SetArgs(deviceId, value));

    // ---- responses ----

    /// <summary>
    /// A `DSTS` reply as G-Helper reads it: the raw dword MINUS the presence bit. A value &gt;= 0 is a supported
    /// device's status; a NEGATIVE result is "this device is not present" (the unsupported method reply
    /// 0xFFFFFFFE decodes to -65538) — never a valid value, so a caller must treat it as a refusal.
    /// </summary>
    internal static int DecodeGet(byte[] output)
    {
        if (output.Length < 4) return int.MinValue;
        return unchecked((int)(ReadU32(output, 0) - PresenceBit));
    }

    /// <summary>A `DEVS` reply: the device returns 1 on success (G-Helper treats anything else as a failure).</summary>
    internal static int DecodeSet(byte[] output) => output.Length < 4 ? -1 : unchecked((int)ReadU32(output, 0));

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadU32(byte[] buffer, int offset)
        => (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}

/// <summary>
/// The ATK device as a small typed facade over one control delegate: (input buffer) -&gt; (16-byte output).
/// Every Windows ASUS port is built over this, so the pure protocol above is the only thing between a port and
/// the wire, and a test drives the whole backend with a recorded delegate.
/// </summary>
internal sealed class AsusAtkDevice(Func<byte[], byte[]> control)
{
    /// <summary>The device status, or a negative value when the device is absent (see <see cref="AsusAtk.DecodeGet"/>).</summary>
    internal int Get(uint deviceId) => AsusAtk.DecodeGet(control(AsusAtk.DstsCall(deviceId)));

    /// <summary>Set a device value; 1 means the firmware accepted it.</summary>
    internal int Set(uint deviceId, int value) => AsusAtk.DecodeSet(control(AsusAtk.DevsCall(deviceId, value)));

    /// <summary>The whole `DSTS` output buffer (for the buffer-shaped devices such as fan curves).</summary>
    internal byte[] GetBuffer(uint deviceId, uint status = 0) => control(AsusAtk.DstsCall(deviceId, status));

    internal byte[] SetBuffer(uint deviceId, byte[] parameters) => control(AsusAtk.CallBuffer(AsusAtk.MethodDevs, BuildSetBufferArgs(deviceId, parameters)));

    /// <summary>Whether the firmware exposes this device at all (its `DSTS` reply is a real value).</summary>
    internal bool Supported(uint deviceId) => Get(deviceId) >= 0;

    private static byte[] BuildSetBufferArgs(uint deviceId, byte[] parameters)
    {
        var args = new byte[4 + parameters.Length];
        args[0] = (byte)deviceId;
        args[1] = (byte)(deviceId >> 8);
        args[2] = (byte)(deviceId >> 16);
        args[3] = (byte)(deviceId >> 24);
        parameters.CopyTo(args, 4);
        return args;
    }
}
