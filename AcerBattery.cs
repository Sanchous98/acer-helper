using System.Management;

namespace AcerHelper;

/// <summary>
/// Acer battery health control (the "~80% charge limit" / health mode) via the
/// root\WMI class BatteryControl (GUID 79772EC5-04B1-4bfd-843C-61E7F77B6CC9).
///
///   SetBatteryHealthControl(uBatteryNo, uFunctionMask, uFunctionStatus, uReservedIn) -> uReturn(U16)
///   GetBatteryHealthControlStatus(uBatteryNo, uFunctionQuery, uReserved) -> uFunctionList, uFunctionStatus, uReturn
///
/// Health mode is function-mask bit 0x01 (Linuwu-Sense: HEALTH_MODE = 1). Requires admin.
/// </summary>
public sealed class AcerBattery : IDisposable
{
    private const string ScopePath  = @"\\.\root\WMI";
    private const string ClassName  = "BatteryControl";
    private const byte   BATTERY_NO  = 1;
    private const byte   HEALTH_MODE = 0x01;   // uFunctionMask bit for the charge-limit health mode

    private readonly ManagementObject? _obj;

    public bool Available => _obj != null;
    public string? LastError { get; private set; }

    public AcerBattery()
    {
        try
        {
            var scope = new ManagementScope(ScopePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(ClassName));
            foreach (ManagementBaseObject o in searcher.Get()) { _obj = (ManagementObject)o; break; }
            if (_obj == null) LastError = $"WMI class {ClassName} not found.";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// <summary>True if the ~80% charge limit is currently on; null on failure/unsupported.</summary>
    public bool? GetLimit()
    {
        if (_obj == null) return null;
        try
        {
            using ManagementBaseObject inp = _obj.GetMethodParameters("GetBatteryHealthControlStatus");
            inp["uBatteryNo"]     = (byte)BATTERY_NO;
            inp["uFunctionQuery"] = (byte)HEALTH_MODE;
            inp["uReserved"]      = (byte)0;
            using ManagementBaseObject outp = _obj.InvokeMethod("GetBatteryHealthControlStatus", inp, null);
            byte status = Convert.ToByte(outp["uFunctionStatus"]);
            return (status & HEALTH_MODE) != 0;
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    /// <summary>Enable/disable the ~80% charge limit. Returns true on success.</summary>
    public bool SetLimit(bool on)
    {
        if (_obj == null) return false;
        try
        {
            using ManagementBaseObject inp = _obj.GetMethodParameters("SetBatteryHealthControl");
            inp["uBatteryNo"]      = (byte)BATTERY_NO;
            inp["uFunctionMask"]   = (byte)HEALTH_MODE;
            inp["uFunctionStatus"] = (byte)(on ? HEALTH_MODE : 0);
            inp["uReservedIn"]     = (byte)0;
            using ManagementBaseObject outp = _obj.InvokeMethod("SetBatteryHealthControl", inp, null);
            ushort ret = Convert.ToUInt16(outp["uReturn"]);
            if (ret != 0) { LastError = $"SetBatteryHealthControl ret={ret}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public void Dispose() => _obj?.Dispose();
}
