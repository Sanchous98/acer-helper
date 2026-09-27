using System.IO;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// Linux source: the DMI attributes under /sys/class/dmi/id.
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read()
        => (ReadFile("/sys/class/dmi/id/sys_vendor"), ReadFile("/sys/class/dmi/id/product_name"));

    // The §3.2 board fields: board_vendor / board_name, the other cheap DMI pair the descriptor carries.
    internal static partial (string? Board, string? BoardProduct) ReadBoard()
        => (ReadFile("/sys/class/dmi/id/board_vendor"), ReadFile("/sys/class/dmi/id/board_name"));

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
