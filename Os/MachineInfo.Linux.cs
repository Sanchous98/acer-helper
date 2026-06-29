using System.IO;

namespace AcerHelper.Os;

/// <summary>Reads the machine's manufacturer and product name (for vendor/model detection).
/// Linux: the DMI attributes under /sys/class/dmi/id.</summary>
public static class MachineInfo
{
    public static (string? Manufacturer, string? Product) Read()
        => (ReadFile("/sys/class/dmi/id/sys_vendor"), ReadFile("/sys/class/dmi/id/product_name"));

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
