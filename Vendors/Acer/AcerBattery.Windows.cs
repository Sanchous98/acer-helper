using System.Management;
using AcerHelper.Features;
using AcerHelper.Os;

namespace AcerHelper.Vendors.Acer;

// Acer "BatteryControl" features for Windows (WMI). The WMI methods mirror the Acer firmware structs
// (see the acer-wmi-battery Linux driver): the input/output blocks contain packed byte fields, and
// crucially some are *arrays* — uReserved[2] / uReservedIn[5] (input) and the get-status output's
// uFunctionStatus[5]. Passing a scalar where the firmware expects a uint8[] is what made WMI throw
// "Specified cast is not valid". Function-mask bits: HEALTH_MODE = 1 (charge limit),
// CALIBRATION_MODE = 2. Both codecs share one BatteryControl WMI object via BatteryWmi.

internal static class BatteryWmi
{
    private const byte BatteryNo = 1;
    public const byte HealthMode = 0x01;
    public const byte CalibrationMode = 0x02;

    /// <summary>Availability + on-state for both modes, read in one call (uFunctionList is a bitmask
    /// of available modes; uFunctionStatus[0]/[1] are the health/calibration on-states).</summary>
    public static (bool HealthAvail, bool HealthOn, bool CalibAvail, bool CalibOn)? ReadStatus(
        WmiInvoker wmi, out string? error)
    {
        error = null;
        try
        {
            using ManagementBaseObject outp = wmi.Invoke("GetBatteryHealthControlStatus", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionQuery"] = (byte)0x1, ["uReserved"] = new byte[2],
            });
            byte list = Convert.ToByte(outp["uFunctionList"]);
            byte[] status = (byte[])outp["uFunctionStatus"];
            bool ha = (list & HealthMode) != 0;
            bool ca = (list & CalibrationMode) != 0;
            return (ha, ha && status.Length > 0 && status[0] > 0,
                    ca, ca && status.Length > 1 && status[1] > 0);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static bool SetControl(WmiInvoker wmi, byte mask, bool on, out string? error)
    {
        error = null;
        try
        {
            // uReturn is ignored (the Acer OEM driver doesn't gate on it); success = no exception.
            using ManagementBaseObject outp = wmi.Invoke("SetBatteryHealthControl", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionMask"] = mask,
                ["uFunctionStatus"] = (byte)(on ? 1 : 0), ["uReservedIn"] = new byte[5],
            });
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

/// <summary>~80% charge limit (battery-health mode, mask 0x01).</summary>
public sealed class AcerBatteryChargeLimit : IBatteryChargeLimit
{
    private readonly WmiInvoker _wmi;
    public AcerBatteryChargeLimit(WmiInvoker battery) => _wmi = battery;

    public string? LastError { get; private set; }

    public bool Get()
    {
        var s = BatteryWmi.ReadStatus(_wmi, out string? e);
        LastError = e;
        return s?.HealthOn ?? false;
    }

    public bool Set(bool on)
    {
        bool ok = BatteryWmi.SetControl(_wmi, BatteryWmi.HealthMode, on, out string? e);
        LastError = e;
        return ok;
    }
}

/// <summary>Battery auto-calibration (full cycle, mask 0x02).</summary>
public sealed class AcerBatteryCalibration : IBatteryCalibration
{
    private readonly WmiInvoker _wmi;
    public AcerBatteryCalibration(WmiInvoker battery) => _wmi = battery;

    public string? LastError { get; private set; }

    public bool Get()
    {
        var s = BatteryWmi.ReadStatus(_wmi, out string? e);
        LastError = e;
        return s?.CalibOn ?? false;
    }

    public bool Set(bool on)
    {
        bool ok = BatteryWmi.SetControl(_wmi, BatteryWmi.CalibrationMode, on, out string? e);
        LastError = e;
        return ok;
    }
}
