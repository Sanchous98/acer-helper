using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Dell;

// Windows transport for Dell's AGENTLESS BIOS-attribute interface: the ACPI-WMI classes Dell firmware
// itself publishes in root\dcim\sysman\biosattributes on 2018+ business models (Latitude/Precision/XPS/
// OptiPlex) — present on stock Windows, no Dell Command software needed. (Do not confuse with the DCIM_*
// classes in root\dcim\sysman, which only exist after installing Dell Command | Monitor.)
// Reads = query the *Attribute classes (one instance per BIOS setting); writes = the
// BIOSAttributeInterface.SetAttribute method, whose Status output is 0 on success (1 Failed, 2 Invalid
// Parameter, 3 Access Denied — e.g. a BIOS admin password is set, which this transport does not supply).
// The lighting analogue of WmiInvoker: a thin accessor; the attribute names/values live in
// DellDevice.Windows.cs. NOTE: exercised only through the shared WmiSession COM layer proven on Acer —
// not yet verified on Dell-Windows hardware.
internal sealed class DellBiosWmi
{
    private const string Ns = @"root\dcim\sysman\biosattributes";
    private const string SecNs = @"root\dcim\sysman\wmisecurity";

    public bool Available { get; }
    public string? LastError { get; private set; }

    public DellBiosWmi()
    {
        using var s = WmiSession.Connect(Ns, out var e);
        if (s == null) { LastError = e; return; }
        using var probe = s.QueryFirst("SELECT AttributeName FROM EnumerationAttribute", out e);
        Available = probe != null;
        LastError = probe == null ? e ?? "Dell BIOS-attribute WMI not present." : null;
    }

    /// <summary>Whether a BIOS admin password is configured. Dell firmware then refuses BIOSAttributeInterface
    /// writes unless the password is supplied in the security buffer (which this transport doesn't), so the
    /// gated controls should be hidden rather than fail with Status=Access Denied. Mirrors the Linux
    /// dell-wmi-sysman authentication/Admin/is_enabled check.</summary>
    public bool AdminPasswordSet()
    {
        using var s = WmiSession.Connect(SecNs, out _);
        if (s == null) return false;   // namespace absent / can't tell -> assume not gated
        using var row = s.QueryFirst("SELECT IsPasswordSet FROM PasswordObject WHERE NameId='Admin'", out _);
        return row != null && row.GetU64("IsPasswordSet") != 0;
    }

    /// <summary>An enumeration attribute's current value, or null (attribute absent on this model).</summary>
    public string? Get(string attribute)
    {
        using var s = WmiSession.Connect(Ns, out var e);
        if (s == null) { LastError = e; return null; }
        using var row = s.QueryFirst(
            $"SELECT CurrentValue FROM EnumerationAttribute WHERE AttributeName='{attribute}'", out e);
        LastError = e;
        return row?.GetString("CurrentValue");
    }

    public (bool ok, string? error) Set(string attribute, string value)
    {
        using var s = WmiSession.Connect(Ns, out var e);
        if (s == null) return (false, e);
        // SecType 0 + SecHndCount 0 = no BIOS admin password supplied (SecHandle stays null).
        using var outp = s.InvokeMethod("BIOSAttributeInterface", "SetAttribute",
            new Dictionary<string, object>
            {
                ["SecType"] = 0u,
                ["SecHndCount"] = 0u,
                ["AttributeName"] = attribute,
                ["AttributeValue"] = value,
            }, out e);
        if (outp == null) return (false, e);
        var status = outp.GetU64("Status");
        return status == 0 ? (true, null) : (false, $"SetAttribute({attribute}) status={status}");
    }
}
