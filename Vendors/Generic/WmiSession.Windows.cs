using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AcerHelper.Vendors.Generic;

/// <summary>A short-lived WMI connection to one namespace, built on the source-generated COM interop in
/// <see cref="Wbem"/>. Deliberately per-operation: WMI COM proxies are apartment-bound and can't be shared
/// across threads, and our callers run on both the UI thread (sensor polling) and thread-pool threads
/// (set operations). Rather than marshal proxies between apartments, each call opens its own session on the
/// calling thread (CoInitialize + connect + proxy-blanket) and tears it down. Connecting costs ~a few ms —
/// negligible for our low-frequency use.</summary>
internal sealed class WmiSession : IDisposable
{
    // ExecQuery flags: forward-only + return-immediately give a cheap in-proc enumerator.
    private const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x10;
    private const int WBEM_FLAG_FORWARD_ONLY = 0x20;
    private const int WBEM_INFINITE = -1;

    // Serializes every WMI transaction process-wide. The Acer ACPI-WMI methods talk to ONE embedded
    // controller, which does not tolerate overlapping calls: a background set (a toggle) firing while the
    // 3-second poll is mid-burst of sensor/profile reads would intermittently be rejected by the EC — the
    // switch moved but the hardware didn't ("sometimes doesn't apply / rolls back"). Every QueryFirst /
    // InvokeMethod takes this lock, so at most one EC transaction is ever in flight. Monitor is re-entrant,
    // so InvokeMethod's own call to QueryFirst (to fetch the instance) doesn't self-deadlock. Static, so it
    // spans all sessions/threads/classes (gaming, battery, APGe all hit the same EC). Each call still holds
    // it only for its own transaction (released in between), so reads and writes just interleave, never
    // overlap; the ~ms of blocking on the UI-thread poll is negligible.
    private static readonly Lock Gate = new();

    private readonly IWbemServices _svc;

    private WmiSession(IWbemServices svc) => _svc = svc;

    /// <summary>Open a session to a namespace such as <c>root\WMI</c> or <c>root\CIMV2</c>. Returns null
    /// (with <paramref name="error"/> set) if COM/WMI is unavailable or the connection is refused.</summary>
    public static WmiSession? Connect(string @namespace, out string? error)
    {
        error = null;
        // Any of S_OK / S_FALSE / RPC_E_CHANGED_MODE means COM is usable on this thread; we don't
        // uninitialize (the thread keeps its apartment). We never share proxies across threads, so the
        // apartment kind doesn't matter.
        Wbem.CoInitializeEx(0, Wbem.COINIT_MULTITHREADED);

        nint pLocator = 0, pSvc = 0, bstrNs = 0;
        try
        {
            var hr = Wbem.CoCreateInstance(in Wbem.CLSID_WbemLocator, 0, Wbem.CLSCTX_INPROC_SERVER,
                in Wbem.IID_IWbemLocator, out pLocator);
            if (hr < 0 || pLocator == 0) { error = Hr("CoCreateInstance(WbemLocator)", hr); return null; }

            var locator = (IWbemLocator)Wbem.ComWrappers.GetOrCreateObjectForComInstance(
                pLocator, CreateObjectFlags.UniqueInstance);
            try
            {
                bstrNs = Wbem.SysAllocString(@namespace);
                hr = locator.ConnectServer(bstrNs, 0, 0, 0, 0, 0, 0, out pSvc);
            }
            finally { (locator as IDisposable)?.Dispose(); }
            if (hr < 0 || pSvc == 0) { error = Hr("ConnectServer", hr); return null; }

            // Required or WMI calls fail with access-denied: set the standard auth blanket on the proxy.
            Wbem.CoSetProxyBlanket(pSvc, Wbem.RPC_C_AUTHN_WINNT, Wbem.RPC_C_AUTHZ_NONE, 0,
                Wbem.RPC_C_AUTHN_LEVEL_CALL, Wbem.RPC_C_IMP_LEVEL_IMPERSONATE, 0, Wbem.EOAC_NONE);

            var svc = (IWbemServices)Wbem.ComWrappers.GetOrCreateObjectForComInstance(
                pSvc, CreateObjectFlags.UniqueInstance);
            return new WmiSession(svc);
        }
        catch (Exception ex) { error = ex.Message; return null; }
        finally
        {
            if (bstrNs != 0) Wbem.SysFreeString(bstrNs);
            // We wrapped these into RCWs (which took their own refs); release our CoCreate/ConnectServer refs.
            if (pLocator != 0) Marshal.Release(pLocator);
            if (pSvc != 0) Marshal.Release(pSvc);
        }
    }

    /// <summary>Return the first object of a query (or null). Used both to fetch a class instance before a
    /// method call and for plain data queries (Win32_*, smart-battery classes).</summary>
    public WmiObject? QueryFirst(string wql, out string? error)
    {
        lock (Gate)
        {
            error = null;
            nint bLang = 0, bQuery = 0;
            try
            {
                bLang = Wbem.SysAllocString("WQL");
                bQuery = Wbem.SysAllocString(wql);
                var hr = _svc.ExecQuery(bLang, bQuery, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                    0, out var en);
                if (hr < 0 || en == null) { error = Hr("ExecQuery", hr); return null; }
                try
                {
                    hr = en.Next(WBEM_INFINITE, 1, out var obj, out var returned);
                    if (hr < 0 || returned == 0 || obj == null) return null;   // no rows
                    return new WmiObject(obj);
                }
                finally { (en as IDisposable)?.Dispose(); }
            }
            catch (Exception ex) { error = ex.Message; return null; }
            finally { if (bLang != 0) Wbem.SysFreeString(bLang); if (bQuery != 0) Wbem.SysFreeString(bQuery); }
        }
    }

    /// <summary>Invoke a method on the (single) instance of <paramref name="className"/>, mirroring what
    /// System.Management did: find the instance, spawn the method's in-parameter object, fill it, and
    /// ExecMethod on the instance path. Returns the out-parameter object (caller reads + disposes it), or
    /// null on failure.</summary>
    public WmiObject? InvokeMethod(string className, string method,
        IReadOnlyDictionary<string, object>? args, out string? error)
    {
        lock (Gate)   // one EC transaction at a time (re-entrant: the QueryFirst below re-takes it safely)
        {
            // The method's in-parameter signature comes from the CLASS definition — IWbemClassObject::GetMethod
            // is invalid on an instance (returns an error), which silently aborted every method call. The
            // instance is still needed for its __PATH, which ExecMethod runs against.
            using var instance = QueryFirst($"SELECT * FROM {className}", out error);
            if (instance == null) { error ??= $"No instance of {className}"; return null; }

            nint bClass = 0, bMethod = 0, bPath = 0;
            WmiObject? classObj = null, inParams = null;
            try
            {
                var path = instance.GetString("__PATH");
                if (string.IsNullOrEmpty(path)) { error = "instance has no __PATH"; return null; }

                bClass = Wbem.SysAllocString(className);
                var hr = _svc.GetObject(bClass, 0, 0, out var classRaw, 0);
                if (hr < 0 || classRaw == null) { error = Hr("GetObject(class)", hr); return null; }
                classObj = new WmiObject(classRaw);

                bMethod = Wbem.SysAllocString(method);
                hr = classObj.Raw.GetMethod(bMethod, 0, out var inSig, out var outSig);
                (outSig as IDisposable)?.Dispose();
                if (hr < 0) { error = Hr("GetMethod", hr); return null; }

                if (inSig != null)
                {
                    try
                    {
                        hr = inSig.SpawnInstance(0, out var inInst);
                        if (hr < 0 || inInst == null) { error = Hr("SpawnInstance", hr); return null; }
                        inParams = new WmiObject(inInst);
                    }
                    finally { (inSig as IDisposable)?.Dispose(); }

                    if (args != null)
                        foreach (var kv in args) inParams.PutValue(kv.Key, kv.Value);
                }

                bPath = Wbem.SysAllocString(path);
                hr = _svc.ExecMethod(bPath, bMethod, 0, 0, inParams?.Raw, out var outObj, 0);
                if (hr >= 0 && outObj != null) return new WmiObject(outObj);
                error = Hr("ExecMethod", hr);
                return null;
            }
            catch (Exception ex) { error = ex.Message; return null; }
            finally
            {
                classObj?.Dispose();
                inParams?.Dispose();
                if (bClass != 0) Wbem.SysFreeString(bClass);
                if (bMethod != 0) Wbem.SysFreeString(bMethod);
                if (bPath != 0) Wbem.SysFreeString(bPath);
            }
        }
    }

    public void Dispose() => (_svc as IDisposable)?.Dispose();

    private static string Hr(string what, int hr) => $"{what} failed (0x{hr:X8})";
}

/// <summary>A read/write wrapper over an <see cref="IWbemClassObject"/> (a WMI object — an instance, a
/// query row, or a method's in/out parameter block). IWbemClassObject is in-process, so property access is
/// local and cheap. Exposes only the value shapes we use; disposes the underlying COM object.</summary>
public sealed class WmiObject : IDisposable
{
    internal WmiObject(IWbemClassObject obj) => Raw = obj;

    /// <summary>The underlying object, for passing as a method's in-parameters.</summary>
    internal IWbemClassObject Raw { get; }

    public ulong GetU64(string name)
    {
        Read(name, out var v);
        try { return ToU64(v); }
        finally { Clear(ref v); }
    }

    public byte GetByte(string name) => (byte)GetU64(name);
    public int GetInt(string name) => (int)GetU64(name);

    public string? GetString(string name)
    {
        Read(name, out var v);
        try { return v.vt == Wbem.VT_BSTR && v.ptrVal != 0 ? Marshal.PtrToStringBSTR(v.ptrVal) : null; }
        finally { Clear(ref v); }
    }

    public byte[] GetBytes(string name)
    {
        Read(name, out var v);
        try
        {
            if ((v.vt & Wbem.VT_ARRAY) == 0 || v.ptrVal == 0) return [];
            var psa = v.ptrVal;
            if (Wbem.SafeArrayGetLBound(psa, 1, out var lb) < 0 ||
                Wbem.SafeArrayGetUBound(psa, 1, out var ub) < 0) return [];
            var count = ub - lb + 1;
            if (count <= 0 || Wbem.SafeArrayAccessData(psa, out var data) < 0) return [];
            var result = new byte[count];
            Marshal.Copy(data, result, 0, count);
            Wbem.SafeArrayUnaccessData(psa);
            return result;
        }
        finally { Clear(ref v); }
    }

    /// <summary>Set an in-parameter, matching the property's real CIM type. Strings go in as VT_BSTR
    /// (CIM_STRING parameters — e.g. Dell's BIOSAttributeInterface AttributeName/AttributeValue). Byte
    /// arrays go in as a SAFEARRAY of VT_UI1 (Acer's firmware blocks expect uint8[] — uReserved[], etc.).
    /// 64-bit CIM integers must be a VT_BSTR decimal string (WMI's representation — a VT_I4/VT_UI8 there is
    /// silently rejected, which is what broke every Set*/Get* with a UInt64 gmInput/gmOutput). Everything
    /// else goes in as VT_I4, which WMI coerces to the property's 8/16/32-bit type.</summary>
    public void PutValue(string name, object value)
    {
        if (value is byte[] arr) { PutBytes(name, arr); return; }
        if (value is string s) { PutBstr(name, s); return; }

        ulong u = Convert.ToUInt64(value);
        int cim = GetCimType(name);
        if (cim is Wbem.CIM_UINT64 or Wbem.CIM_SINT64) PutBstr(name, u.ToString());
        else PutU32(name, (uint)u);
    }

    /// <summary>The property's declared CIM type (needed to pick the right VARIANT shape for a 64-bit int).</summary>
    private unsafe int GetCimType(string name)
    {
        var bn = Wbem.SysAllocString(name);
        Variant v = default;
        int type = 0;
        try { Raw.Get(bn, 0, (nint)(&v), (nint)(&type), 0); }
        finally { Clear(ref v); Wbem.SysFreeString(bn); }
        return type;
    }

    private unsafe void PutBstr(string name, string s)
    {
        var bn = Wbem.SysAllocString(name);
        var bs = Wbem.SysAllocString(s);
        var v = new Variant { vt = Wbem.VT_BSTR, ptrVal = bs };
        try { Raw.Put(bn, 0, (nint)(&v), 0); }
        finally { Wbem.SysFreeString(bs); Wbem.SysFreeString(bn); }
    }

    private unsafe void PutU32(string name, uint value)
    {
        var bn = Wbem.SysAllocString(name);
        var v = new Variant { vt = Wbem.VT_I4, lVal = (int)value };
        try { Raw.Put(bn, 0, (nint)(&v), 0); }
        finally { Wbem.SysFreeString(bn); }
    }

    private unsafe void PutBytes(string name, byte[] data)
    {
        var psa = Wbem.SafeArrayCreateVector(Wbem.VT_UI1, 0, (uint)data.Length);
        if (psa == 0) return;
        var bn = Wbem.SysAllocString(name);
        try
        {
            if (data.Length > 0 && Wbem.SafeArrayAccessData(psa, out nint p) >= 0)
            {
                Marshal.Copy(data, 0, p, data.Length);
                Wbem.SafeArrayUnaccessData(psa);
            }
            var v = new Variant { vt = Wbem.VT_ARRAY | Wbem.VT_UI1, ptrVal = psa };
            Raw.Put(bn, 0, (nint)(&v), 0);   // Put copies the value into the object's local block
        }
        finally { Wbem.SysFreeString(bn); Wbem.SafeArrayDestroy(psa); }
    }

    private unsafe void Read(string name, out Variant v)
    {
        v = default;
        var bn = Wbem.SysAllocString(name);
        try
        {
            fixed (Variant* pv = &v)
                if (Raw.Get(bn, 0, (nint)pv, 0, 0) < 0) v = default;
        }
        finally { Wbem.SysFreeString(bn); }
    }

    private static unsafe void Clear(ref Variant v)
    {
        fixed (Variant* pv = &v) Wbem.VariantClear((nint)pv);
    }

    private static ulong ToU64(in Variant v) => v.vt switch
    {
        Wbem.VT_I1 => (ulong)v.cVal,
        Wbem.VT_UI1 => v.bVal,
        Wbem.VT_I2 => (ulong)v.iVal,
        Wbem.VT_UI2 => v.uiVal,
        Wbem.VT_I4 or Wbem.VT_INT => (ulong)v.lVal,
        Wbem.VT_UI4 or Wbem.VT_UINT => v.ulVal,
        Wbem.VT_I8 => (ulong)v.llVal,
        Wbem.VT_UI8 => v.ullVal,
        Wbem.VT_BOOL => v.iVal != 0 ? 1UL : 0UL,
        // WMI returns 64-bit CIM integers (CIM_UINT64/SINT64) as a decimal BSTR, never VT_UI8. Parse it.
        Wbem.VT_BSTR => ParseBstr(v.ptrVal),
        _ => 0UL,
    };

    private static ulong ParseBstr(nint bstr)
    {
        if (bstr == 0) return 0;
        var s = Marshal.PtrToStringBSTR(bstr);
        if (string.IsNullOrEmpty(s)) return 0;
        if (ulong.TryParse(s, out var u)) return u;
        return long.TryParse(s, out var l) ? unchecked((ulong)l) : 0;   // negative sint64 -> reinterpret
    }

    public void Dispose() => (Raw as IDisposable)?.Dispose();
}
