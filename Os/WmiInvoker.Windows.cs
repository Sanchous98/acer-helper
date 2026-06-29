using System.Management;

namespace AcerHelper.Os;

/// <summary>The single place <c>System.Management</c> (WMI) is used: a thin, vendor-agnostic
/// wrapper that binds to one root\WMI class and invokes its methods. Vendor codecs supply the
/// method/parameter names. Requires administrator privileges for the Acer ACPI-WMI methods.</summary>
public sealed class WmiInvoker : IDisposable
{
    private const string ScopePath = @"\\.\root\WMI";

    private readonly ManagementObject? _obj;

    public bool Available => _obj != null;
    public string? LastError { get; private set; }

    public WmiInvoker(string className)
    {
        try
        {
            var scope = new ManagementScope(ScopePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(className));
            foreach (ManagementBaseObject o in searcher.Get()) { _obj = (ManagementObject)o; break; }
            if (_obj == null) LastError = $"WMI class {className} not found.";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// <summary>Invoke a method with one input parameter; return one numeric output as U64.</summary>
    public ulong Invoke(string method, string inParam, object inValue, string outParam)
    {
        using ManagementBaseObject inp = _obj!.GetMethodParameters(method);
        inp[inParam] = Coerce(inp, inParam, inValue);
        using ManagementBaseObject outp = _obj.InvokeMethod(method, inp, null);
        return Convert.ToUInt64(outp[outParam]);
    }

    /// <summary>Invoke a method with several input parameters; the caller reads and disposes the result.</summary>
    public ManagementBaseObject Invoke(string method, IReadOnlyDictionary<string, object> args)
    {
        using ManagementBaseObject inp = _obj!.GetMethodParameters(method);
        foreach (var kv in args) inp[kv.Key] = Coerce(inp, kv.Key, kv.Value);
        return _obj.InvokeMethod(method, inp, null);
    }

    // WMI marshals strictly: assigning a value whose .NET type doesn't match the parameter's declared
    // CIM type throws "Specified cast is not valid". Convert each value to its parameter's actual type
    // first, so callers can pass any integer width without knowing the MOF signature. Array parameters
    // (e.g. Acer's uint8[] reserved/buffer fields) are passed through as-is.
    private static object Coerce(ManagementBaseObject inp, string name, object value)
    {
        PropertyData prop = inp.Properties[name];
        if (prop.IsArray) return value;
        return prop.Type switch
        {
            CimType.UInt8  => Convert.ToByte(value),
            CimType.SInt8  => Convert.ToSByte(value),
            CimType.UInt16 => Convert.ToUInt16(value),
            CimType.SInt16 => Convert.ToInt16(value),
            CimType.UInt32 => Convert.ToUInt32(value),
            CimType.SInt32 => Convert.ToInt32(value),
            CimType.UInt64 => Convert.ToUInt64(value),
            CimType.SInt64 => Convert.ToInt64(value),
            _              => value,
        };
    }

    public void Dispose() => _obj?.Dispose();
}
