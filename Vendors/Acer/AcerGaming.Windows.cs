using AcerHelper.Os;

namespace AcerHelper.Vendors.Acer;

// Acer "AcerGamingFunction" feature implementations for Windows (WMI). The Acer encoding (EC
// indices, fan/sensor ids, LCD-overdrive constants, bit packing) lives here together with the
// WMI calls — there is no separate platform layer. A Linux build supplies *.Linux.cs versions.
// Verified on Acer Nitro 18 (AN18-61). The four codecs share one AcerGamingFunction WMI object.

/// <summary>Performance/platform profiles via misc-setting indices 0x0B (current) / 0x0A (mask).</summary>
public sealed class AcerPowerProfiles : IPowerProfiles
{
    private const uint IdxPlatformProfile   = 0x0B;
    private const uint IdxSupportedProfiles = 0x0A;

    private readonly WmiInvoker _wmi;
    public AcerPowerProfiles(WmiInvoker gaming) => _wmi = gaming;

    public string? LastError { get; private set; }
    // The profile enum is shared across the Acer gaming line; the available set is discovered live
    // from the supported-mask below (not per-model).
    public IReadOnlyList<PerformanceProfile> All => AcerProfiles.All;

    public IReadOnlyList<PerformanceProfile> Selectable()
    {
        try
        {
            ulong outv = _wmi.Invoke("GetGamingMiscSetting", "gmInput", IdxSupportedProfiles, "gmOutput");
            if ((outv & 0xFF) != 0) return AcerProfiles.All;            // unknown -> all
            return AcerProfiles.FromMask((byte)((outv >> 8) & 0xFF));
        }
        catch (Exception ex) { LastError = ex.Message; return AcerProfiles.All; }
    }

    public PerformanceProfile? Current()
    {
        try
        {
            ulong outv = _wmi.Invoke("GetGamingMiscSetting", "gmInput", IdxPlatformProfile, "gmOutput");
            if ((outv & 0xFF) != 0) { LastError = $"GetGamingMiscSetting status={(outv & 0xFF)}"; return null; }
            return AcerProfiles.ToDomain((byte)((outv >> 8) & 0xFF));
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public bool Set(PerformanceProfile profile)
    {
        try
        {
            ulong packed = IdxPlatformProfile | ((ulong)AcerProfiles.ToByte(profile) << 8);
            uint status = (uint)_wmi.Invoke("SetGamingMiscSetting", "gmInput", packed, "gmOutput");
            if ((status & 0xFF) != 0) { LastError = $"SetGamingMiscSetting status={(status & 0xFF)}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}

/// <summary>Fan behaviour + custom speeds. GPU uses different ids for behaviour (0x08) vs speed (0x04).</summary>
public sealed class AcerFanControl : IFanControl
{
    private const uint FanCpu      = 0x01;
    private const uint FanGpuMask  = 0x08;
    private const uint FanGpuSpeed = 0x04;

    private readonly WmiInvoker _wmi;
    public AcerFanControl(WmiInvoker gaming) => _wmi = gaming;

    public string? LastError { get; private set; }
    // Modern Acer gaming is hardwired dual-fan (CPU + GPU); not a per-model axis.
    public FanCapability Capability => new(HasMax: true, HasCustom: true, HasGpuFan: true);

    public bool SetMode(FanMode mode)
    {
        try
        {
            ulong mask    = FanCpu | FanGpuMask;   // 0x09
            ulong gmInput = mask | ((ulong)(byte)mode << 16) | ((ulong)(byte)mode << 22);
            ulong outv    = _wmi.Invoke("SetGamingFanBehavior", "gmInput", gmInput, "gmOutput");
            if ((outv & 0xFF) != 0) { LastError = $"SetGamingFanBehavior status={(outv & 0xFF)}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent)
        => SetMode(FanMode.Custom) && SetSpeed(gpu: false, cpuPercent) && SetSpeed(gpu: true, gpuPercent);

    private bool SetSpeed(bool gpu, byte percent)
    {
        try
        {
            uint  id      = gpu ? FanGpuSpeed : FanCpu;
            ulong gmInput = id | ((ulong)percent << 8);
            ulong outv    = _wmi.Invoke("SetGamingFanSpeed", "gmInput", gmInput, "gmOutput");
            if ((outv & 0xFF) != 0) { LastError = $"SetGamingFanSpeed status={(outv & 0xFF)}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}

/// <summary>Live temperatures (°C) and fan speeds (rpm) via GetGamingSysInfo.</summary>
public sealed class AcerSensors : ISensors
{
    private const uint SensorRead    = 0x0001;
    private const uint SensorCpuTemp = 0x01;
    private const uint SensorCpuFan  = 0x02;
    private const uint SensorGpuFan  = 0x06;
    private const uint SensorGpuTemp = 0x0A;

    private readonly WmiInvoker _wmi;
    public AcerSensors(WmiInvoker gaming) => _wmi = gaming;

    public SensorSnapshot Read() => new()
    {
        CpuTempC = ReadSensor(SensorCpuTemp, word: false),
        GpuTempC = ReadSensor(SensorGpuTemp, word: false),
        Fans =
        [
            new FanReading("CPU", ReadSensor(SensorCpuFan, word: true)),
            new FanReading("GPU", ReadSensor(SensorGpuFan, word: true)),
        ],
    };

    private int ReadSensor(uint sensorId, bool word)
    {
        try
        {
            uint  gmInput = SensorRead | (sensorId << 8);
            ulong outv    = _wmi.Invoke("GetGamingSysInfo", "gmInput", gmInput, "gmOutput");
            if ((outv & 0xFF) != 0) return -1;
            return word ? (int)((outv >> 8) & 0xFFFF) : (int)((outv >> 8) & 0xFF);
        }
        catch { return -1; }
    }
}

/// <summary>LCD overdrive (response-time boost) via Get/SetGamingProfile. Encoding from Linuwu-Sense.</summary>
public sealed class AcerLcdOverdrive : ILcdOverdrive
{
    private const ulong SetOnValue  = 0x1000000000010;
    private const ulong SetOffValue = 0x10;
    private const ulong GetBit      = 0x1000000000000;

    private readonly WmiInvoker _wmi;
    public AcerLcdOverdrive(WmiInvoker gaming) => _wmi = gaming;

    public string? LastError { get; private set; }

    public bool Get()
    {
        try { return (_wmi.Invoke("GetGamingProfile", "gmInput", (uint)0x00, "gmOutput") & GetBit) != 0; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public bool Set(bool on)
    {
        try
        {
            ulong outv = _wmi.Invoke("SetGamingProfile", "gmInput", on ? SetOnValue : SetOffValue, "gmOutput");
            if ((outv & 0xFF) != 0) { LastError = $"SetGamingProfile status={(outv & 0xFF)}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}
