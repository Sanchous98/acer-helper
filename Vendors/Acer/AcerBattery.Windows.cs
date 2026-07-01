using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Acer;

// Windows encoding helper for the "BatteryControl" WMI object. The methods mirror the Acer firmware structs
// (see the acer-wmi-battery Linux driver): the input/output blocks contain packed byte fields, some *arrays*
// — uReserved[2] / uReservedIn[5] (input) and the get-status output's uFunctionStatus[5]. Function-mask
// bits: HEALTH_MODE = 1 (charge limit), CALIBRATION_MODE = 2. Used by AcerDevice.Windows.cs to build the
// battery AcerFlags; there is no per-feature battery class anymore.
internal static class BatteryWmi
{
    private const byte BatteryNo = 1;
    public const byte HealthMode = 0x01;
    public const byte CalibrationMode = 0x02;

    /// <summary>Availability + on-state for both modes in one call (uFunctionList is a bitmask of available
    /// modes; uFunctionStatus[0]/[1] are the health/calibration on-states).</summary>
    public static (bool HealthAvail, bool HealthOn, bool CalibAvail, bool CalibOn)? ReadStatus(
        WmiInvoker wmi, out string? error)
    {
        error = null;
        try
        {
            using var outp = wmi.Invoke("GetBatteryHealthControlStatus", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionQuery"] = (byte)0x1, ["uReserved"] = new byte[2],
            });
            if (outp == null) { error = wmi.LastError; return null; }
            var list = outp.GetByte("uFunctionList");
            var status = outp.GetBytes("uFunctionStatus");
            var ha = (list & HealthMode) != 0;
            var ca = (list & CalibrationMode) != 0;
            return (ha, ha && status.Length > 0 && status[0] > 0,
                    ca, ca && status.Length > 1 && status[1] > 0);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static (bool ok, string? error) SetControl(WmiInvoker wmi, byte mask, bool on)
    {
        try
        {
            // uReturn is ignored (the Acer OEM driver doesn't gate on it); success = the call went through.
            using var outp = wmi.Invoke("SetBatteryHealthControl", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionMask"] = mask,
                ["uFunctionStatus"] = (byte)(on ? 1 : 0), ["uReservedIn"] = new byte[5],
            });
            return outp != null ? (true, null) : (false, wmi.LastError);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
