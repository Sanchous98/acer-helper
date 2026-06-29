namespace AcerHelper.Os;

/// <summary>Reads the machine's manufacturer and product name (for vendor/model detection).
/// The source is OS-specific — WMI on Windows, DMI sysfs on Linux — supplied by the partial
/// <see cref="Read"/> in the matching MachineInfo.*.cs file.</summary>
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read();
}
