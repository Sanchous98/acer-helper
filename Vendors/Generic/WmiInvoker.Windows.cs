namespace AcerHelper.Vendors.Generic;

/// <summary>The single place WMI method invocation is used: a thin, vendor-agnostic wrapper that binds to
/// one <c>root\WMI</c> class and invokes its methods. Vendor codecs supply the method/parameter names.
/// Requires administrator privileges for the Acer ACPI-WMI methods.
///
/// Backed by the source-generated COM interop in <see cref="WmiSession"/> (NOT System.Management, which is
/// not Native-AOT-safe). A fresh session is opened per call — see <see cref="WmiSession"/> for why (COM
/// apartment threading); connecting is cheap for our low-frequency use.</summary>
public sealed class WmiInvoker : IDisposable
{
    private const string Namespace = @"root\WMI";

    private readonly string _className;

    public bool Available { get; }
    public string? LastError { get; private set; }

    public WmiInvoker(string className)
    {
        _className = className;
        using var session = WmiSession.Connect(Namespace, out var e);
        if (session == null) { LastError = e; return; }
        using var probe = session.QueryFirst($"SELECT * FROM {className}", out e);
        LastError = probe == null ? e ?? $"WMI class {className} not found." : null;
        Available = probe != null;
    }

    /// <summary>Invoke a method with one input parameter; return one numeric output as U64.</summary>
    public ulong Invoke(string method, string inParam, object inValue, string outParam)
    {
        using var session = WmiSession.Connect(Namespace, out var e);
        if (session == null) { LastError = e; return 0; }
        using var outp = session.InvokeMethod(_className, method,
            new Dictionary<string, object> { [inParam] = inValue }, out e);
        LastError = e;
        return outp?.GetU64(outParam) ?? 0;
    }

    /// <summary>Invoke a method with several input parameters; the caller reads and disposes the result.
    /// The returned object is an in-process copy, so it stays valid after this session closes.</summary>
    public WmiObject? Invoke(string method, IReadOnlyDictionary<string, object> args)
    {
        using var session = WmiSession.Connect(Namespace, out string? e);
        if (session == null) { LastError = e; return null; }
        var outp = session.InvokeMethod(_className, method, args, out e);
        LastError = e;
        return outp;
    }

    // Nothing persistent to release — each call opens and closes its own WMI session. Kept so the
    // composition root can treat every device port uniformly as IDisposable.
    public void Dispose() { }
}
