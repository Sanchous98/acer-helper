namespace AcerHelper.Vendors.Generic;

// Windows source: WMI Win32_ComputerSystemProduct in root\CIMV2 (via the AOT-safe WMI COM layer).
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read()
    {
        using var session = WmiSession.Connect(@"root\CIMV2", out _);
        if (session == null) return (null, null);
        using var row = session.QueryFirst("SELECT Vendor, Name FROM Win32_ComputerSystemProduct", out _);
        if (row == null) 
            return (null, null);
        return (row.GetString("Vendor")?.Trim(), row.GetString("Name")?.Trim());
    }
}
