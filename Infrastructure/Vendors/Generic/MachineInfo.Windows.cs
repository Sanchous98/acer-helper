using AcerHelper.Infrastructure.Plugins.Sdk;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Windows source: WMI Win32_ComputerSystemProduct / Win32_BaseBoard in root\CIMV2 (via the AOT-safe WMI COM layer).
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read()
    {
        using var session = WmiSession.Connect(@"root\CIMV2", out _);
        if (session == null) return (null, null);
        using var row = session.QueryFirst("SELECT Vendor, Name FROM Win32_ComputerSystemProduct", out _);
        return row == null ? (null, null) : (row.GetString("Vendor")?.Trim(), row.GetString("Name")?.Trim());
    }

    // The §3.2 board fields: Win32_BaseBoard.Manufacturer / .Product, the other cheap DMI pair the descriptor
    // carries. Same session/row shape as Read above; a machine with no Win32_BaseBoard row yields nulls.
    internal static partial (string? Board, string? BoardProduct) ReadBoard()
    {
        using var session = WmiSession.Connect(@"root\CIMV2", out _);
        if (session == null) return (null, null);
        using var row = session.QueryFirst("SELECT Manufacturer, Product FROM Win32_BaseBoard", out _);
        return row == null ? (null, null) : (row.GetString("Manufacturer")?.Trim(), row.GetString("Product")?.Trim());
    }
}
