using System.Management;

namespace AcerHelper.Os;

// Windows source: WMI Win32_ComputerSystemProduct in root\CIMV2.
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read()
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
