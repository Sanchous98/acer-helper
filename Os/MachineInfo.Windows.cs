using System.Management;

namespace AcerHelper.Os;

/// <summary>Reads the machine's manufacturer and product name (for vendor/model detection).
/// Windows: WMI Win32_ComputerSystemProduct in root\CIMV2.</summary>
public static class MachineInfo
{
    public static (string? Manufacturer, string? Product) Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Vendor, Name FROM Win32_ComputerSystemProduct");
            foreach (ManagementBaseObject o in searcher.Get())
                return (o["Vendor"]?.ToString()?.Trim(), o["Name"]?.ToString()?.Trim());
        }
        catch { /* unavailable */ }
        return (null, null);
    }
}
