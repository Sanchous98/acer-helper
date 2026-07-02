using System.IO;

namespace AcerHelper.Vendors.Generic;

// Linux source: the DMI attributes under /sys/class/dmi/id.
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read()
        => (ReadFile("/sys/class/dmi/id/sys_vendor"), ReadFile("/sys/class/dmi/id/product_name"));

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
