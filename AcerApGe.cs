using System.Management;

namespace AcerHelper;

/// <summary>
/// Acer "APGeAction" WMI interface (GUID 61EF69EA-865C-4BC3-A502-A0DEBA0CB531):
///   SetFunction(uiInput:U64) -> uiOutput:U32
///   GetFunction(uiInput:U32) -> uiOutput:U64
///
/// Used here for two Predator/Nitro extras (encodings from Linuwu-Sense):
///   USB charging when powered off:  Get 0x4; Set 663300=off / 659204=on(10%).
///   Keyboard backlight timeout:     Get 0x88401 (0x1E0000080000=on / 0x80000=off);
///                                   Set 0x1E0000088402=on / 0x88402=off.
/// Each feature reports "unsupported" if the read fails, so the UI can grey it out.
/// Requires admin.
/// </summary>
public sealed class AcerApGe : IDisposable
{
    private const string ScopePath = @"\\.\root\WMI";
    private const string ClassName = "APGeAction";

    private const uint  USB_GET = 0x4;
    private const ulong USB_OFF = 663300;          // 0  = off
    private const ulong USB_10  = 659204;          // stop charging at 10%
    private const ulong USB_20  = 1314564;         // stop charging at 20%
    private const ulong USB_30  = 1969924;         // stop charging at 30%

    private const uint  KBD_GET     = 0x88401;
    private const ulong KBD_GET_ON  = 0x1E0000080000;
    private const ulong KBD_GET_OFF = 0x80000;
    private const ulong KBD_SET_ON  = 0x1E0000088402;
    private const ulong KBD_SET_OFF = 0x88402;

    private readonly ManagementObject? _obj;

    public bool Available => _obj != null;
    public string? LastError { get; private set; }

    public AcerApGe()
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

    private ulong GetFunction(uint input)
    {
        using ManagementBaseObject inp = _obj!.GetMethodParameters("GetFunction");
        inp["uiInput"] = input;                       // U32
        using ManagementBaseObject outp = _obj.InvokeMethod("GetFunction", inp, null);
        return Convert.ToUInt64(outp["uiOutput"]);    // U64
    }

    private void SetFunction(ulong input)
    {
        using ManagementBaseObject inp = _obj!.GetMethodParameters("SetFunction");
        inp["uiInput"] = input;                       // U64
        using ManagementBaseObject outp = _obj.InvokeMethod("SetFunction", inp, null);
        _ = outp["uiOutput"];
    }

    // ---- USB charging while powered off (off / stop at 10% / 20% / 30%) ----

    /// <summary>Current USB-charging threshold: 0=off, 10/20/30; null if unsupported.</summary>
    public int? GetUsbChargingLevel()
    {
        if (_obj == null) return null;
        try
        {
            ulong r = GetFunction(USB_GET);
            if (r == USB_OFF) return 0;
            if (r == USB_10)  return 10;
            if (r == USB_20)  return 20;
            if (r == USB_30)  return 30;
            return null;
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public bool SetUsbChargingLevel(int level)
    {
        if (_obj == null) return false;
        ulong v = level switch { 10 => USB_10, 20 => USB_20, 30 => USB_30, _ => USB_OFF };
        try { SetFunction(v); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    // ---- Keyboard backlight timeout (auto-off after ~30s) ----

    public bool? GetBacklightTimeout()
    {
        if (_obj == null) return null;
        try
        {
            ulong r = GetFunction(KBD_GET);
            if (r == KBD_GET_ON)  return true;
            if (r == KBD_GET_OFF) return false;
            return null;   // unrecognised -> treat as unsupported (e.g. on Nitro)
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public bool SetBacklightTimeout(bool on)
    {
        if (_obj == null) return false;
        try { SetFunction(on ? KBD_SET_ON : KBD_SET_OFF); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public void Dispose() => _obj?.Dispose();
}
