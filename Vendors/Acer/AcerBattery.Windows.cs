using System.Management;
using AcerHelper.Features;
using AcerHelper.Os;

namespace AcerHelper.Vendors.Acer;

// Acer "BatteryControl" features for Windows (WMI). Function-mask bits (Linuwu-Sense):
// HEALTH_MODE = 1 (charge limit), CALIBRATION_MODE = 2. Both codecs share one BatteryControl
// WMI object; the small WMI plumbing is shared via BatteryWmi.

internal static class BatteryWmi
{
    private const byte BatteryNo = 1;

    public static byte? GetStatus(WmiInvoker wmi, byte query, out string? error)
    {
        error = null;
        try
        {
            using ManagementBaseObject outp = wmi.Invoke("GetBatteryHealthControlStatus", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionQuery"] = query, ["uReserved"] = (byte)0,
            });
            return Convert.ToByte(outp["uFunctionStatus"]);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static bool SetControl(WmiInvoker wmi, byte mask, bool on, out string? error)
    {
        error = null;
        try
        {
            using ManagementBaseObject outp = wmi.Invoke("SetBatteryHealthControl", new Dictionary<string, object>
            {
                ["uBatteryNo"] = BatteryNo, ["uFunctionMask"] = mask,
                ["uFunctionStatus"] = (byte)(on ? 1 : 0), ["uReservedIn"] = (byte)0,
            });
            ushort ret = Convert.ToUInt16(outp["uReturn"]);
            if (ret != 0) { error = $"SetBatteryHealthControl ret={ret}"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

/// <summary>~80% charge limit (battery-health mode, mask 0x01).</summary>
public sealed class AcerBatteryChargeLimit : IBatteryChargeLimit
{
    private const byte HealthMode = 0x01;

    private readonly WmiInvoker _wmi;
    public AcerBatteryChargeLimit(WmiInvoker battery) => _wmi = battery;

    public string? LastError { get; private set; }

    public bool Get()
    {
        byte? status = BatteryWmi.GetStatus(_wmi, HealthMode, out string? e);
        LastError = e;
        return status != null && (status.Value & HealthMode) != 0;
    }

    public bool Set(bool on)
    {
        bool ok = BatteryWmi.SetControl(_wmi, HealthMode, on, out string? e);
        LastError = e;
        return ok;
    }
}

/// <summary>Battery auto-calibration (full cycle, mask 0x02).</summary>
public sealed class AcerBatteryCalibration : IBatteryCalibration
{
    private const byte CalibrationMode = 0x02;

    private readonly WmiInvoker _wmi;
    public AcerBatteryCalibration(WmiInvoker battery) => _wmi = battery;

    public string? LastError { get; private set; }

    public bool Get()
    {
        byte? status = BatteryWmi.GetStatus(_wmi, CalibrationMode, out string? e);
        LastError = e;
        return status != null && (status.Value & CalibrationMode) != 0;
    }

    public bool Set(bool on)
    {
        bool ok = BatteryWmi.SetControl(_wmi, CalibrationMode, on, out string? e);
        LastError = e;
        return ok;
    }
}
