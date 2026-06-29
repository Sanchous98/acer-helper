using AcerHelper.Features;
using AcerHelper.Os;

namespace AcerHelper.Vendors.Acer;

// Acer "APGeAction" features for Windows (WMI), encodings from Linuwu-Sense. Each probes support
// at construction; the composition root omits the port when unsupported. Both share one
// APGeAction WMI object.

/// <summary>USB charging while powered off: off / stop at 10% / 20% / 30%.</summary>
public sealed class AcerUsbCharging : IUsbCharging
{
    private const uint  Query = 0x4;
    private const ulong Off   = 663300;
    private const ulong At10  = 659204;
    private const ulong At20  = 1314564;
    private const ulong At30  = 1969924;

    private readonly WmiInvoker _wmi;

    public AcerUsbCharging(WmiInvoker apge)
    {
        _wmi = apge;
        Supported = _wmi.Available && Decode(SafeGet()) >= 0;
    }

    /// <summary>True if the device reports a recognised USB-charging level (composition gate).</summary>
    public bool Supported { get; }
    public string? LastError { get; private set; }
    public IReadOnlyList<int> Levels { get; } = [0, 10, 20, 30];

    public int Get()
    {
        int v = Decode(SafeGet());
        return v < 0 ? 0 : v;
    }

    public bool Set(int level)
    {
        ulong v = level switch { 10 => At10, 20 => At20, 30 => At30, _ => Off };
        try { _wmi.Invoke("SetFunction", "uiInput", v, "uiOutput"); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    private ulong SafeGet()
    {
        try { return _wmi.Invoke("GetFunction", "uiInput", Query, "uiOutput"); }
        catch (Exception ex) { LastError = ex.Message; return ulong.MaxValue; }
    }

    private static int Decode(ulong r) => r switch { Off => 0, At10 => 10, At20 => 20, At30 => 30, _ => -1 };
}

/// <summary>Keyboard backlight auto-off timeout (~30s).</summary>
public sealed class AcerKeyboardBacklight : IKeyboardBacklight
{
    private const uint  Query  = 0x88401;
    private const ulong GetOn  = 0x1E0000080000;
    private const ulong GetOff = 0x80000;
    private const ulong SetOn  = 0x1E0000088402;
    private const ulong SetOff = 0x88402;

    private readonly WmiInvoker _wmi;

    public AcerKeyboardBacklight(WmiInvoker apge)
    {
        _wmi = apge;
        ulong r = SafeGet();
        Supported = _wmi.Available && (r == GetOn || r == GetOff);
    }

    /// <summary>True if the device reports a recognised backlight-timeout state (composition gate).</summary>
    public bool Supported { get; }
    public string? LastError { get; private set; }

    public bool GetTimeout() => SafeGet() == GetOn;

    public bool SetTimeout(bool on)
    {
        try { _wmi.Invoke("SetFunction", "uiInput", on ? SetOn : SetOff, "uiOutput"); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    private ulong SafeGet()
    {
        try { return _wmi.Invoke("GetFunction", "uiInput", Query, "uiOutput"); }
        catch (Exception ex) { LastError = ex.Message; return ulong.MaxValue; }
    }
}
