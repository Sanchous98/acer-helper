using System.Management;

namespace AcerHelper;

/// <summary>
/// Thin wrapper over the Acer gaming WMI interface (GUID 7A4DDFE7-…), exposed on
/// Windows as the root\WMI class "AcerGamingFunction". Verified on Acer Nitro 18.
///
/// Performance profile lives in misc-setting index 0x0B:
///   Set: SetGamingMiscSetting(gmInput:U64 = index | (value &lt;&lt; 8)) -> gmOutput:U32 (status in byte 0)
///   Get: GetGamingMiscSetting(gmInput:U32 = index)                    -> gmOutput:U64 (status in byte 0, value in byte 1)
/// Supported-profiles bitmask is index 0x0A.
///
/// Requires administrator privileges.
/// </summary>
public sealed class AcerWmi : IDisposable
{
    private const string ScopePath = @"\\.\root\WMI";
    private const string ClassName = "AcerGamingFunction";

    private const uint IDX_PLATFORM_PROFILE   = 0x0B;
    private const uint IDX_SUPPORTED_PROFILES = 0x0A;

    // Fan ids and sensor ids (GetGamingSysInfo). Verified on Nitro 18.
    // NOTE: GPU uses DIFFERENT ids for behaviour mask (0x08) vs speed (0x04).
    private const uint FAN_CPU          = 0x01;  // CPU: behaviour mask bit AND speed id
    private const uint FAN_GPU_MASK     = 0x08;  // GPU behaviour mask bit (BIT3)
    private const uint FAN_GPU_SPEED    = 0x04;  // GPU speed id
    private const uint SENSOR_READ      = 0x0001;
    private const uint SENSOR_CPU_TEMP  = 0x01;
    private const uint SENSOR_CPU_FAN   = 0x02;
    private const uint SENSOR_GPU_FAN   = 0x06;
    private const uint SENSOR_GPU_TEMP  = 0x0A;

    private readonly ManagementObject? _obj;

    public bool Available => _obj != null;
    public string? LastError { get; private set; }

    public AcerWmi()
    {
        try
        {
            var scope = new ManagementScope(ScopePath);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(ClassName));
            foreach (ManagementBaseObject o in searcher.Get())
            {
                _obj = (ManagementObject)o;
                break;
            }

            if (_obj == null)
                LastError = $"WMI class {ClassName} not found (not an Acer gaming model?).";
        }
        catch (Exception ex)
        {
            _obj = null;
            LastError = ex.Message;
        }
    }

    private ulong InvokeGet(uint gmInput)
    {
        using ManagementBaseObject inParams = _obj!.GetMethodParameters("GetGamingMiscSetting");
        inParams["gmInput"] = gmInput;                       // U32
        using ManagementBaseObject outParams = _obj.InvokeMethod("GetGamingMiscSetting", inParams, null);
        return Convert.ToUInt64(outParams["gmOutput"]);      // U64
    }

    private uint InvokeSet(ulong gmInput)
    {
        using ManagementBaseObject inParams = _obj!.GetMethodParameters("SetGamingMiscSetting");
        inParams["gmInput"] = gmInput;                       // U64
        using ManagementBaseObject outParams = _obj.InvokeMethod("SetGamingMiscSetting", inParams, null);
        return Convert.ToUInt32(outParams["gmOutput"]);      // U32
    }

    /// <summary>Current performance profile, or null on failure.</summary>
    public AcerProfile? GetProfile()
    {
        if (_obj == null) return null;
        try
        {
            ulong outv = InvokeGet(IDX_PLATFORM_PROFILE);
            if ((outv & 0xFF) != 0) { LastError = $"GetGamingMiscSetting status={(outv & 0xFF)}"; return null; }
            return (AcerProfile)((outv >> 8) & 0xFF);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    /// <summary>Bitmask of supported profiles (index 0x0A). 0 on failure.</summary>
    public byte GetSupportedMask()
    {
        if (_obj == null) return 0;
        try
        {
            ulong outv = InvokeGet(IDX_SUPPORTED_PROFILES);
            if ((outv & 0xFF) != 0) return 0;
            return (byte)((outv >> 8) & 0xFF);
        }
        catch (Exception ex) { LastError = ex.Message; return 