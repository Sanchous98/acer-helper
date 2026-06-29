using System.IO;

namespace AcerHelper.Os;

/// <summary>Run at login via a freedesktop autostart entry (~/.config/autostart/acer-helper.desktop).
/// Vendor-agnostic.</summary>
public sealed class Autostart : IAutostart
{
    private static string DesktopFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),  // ~/.config
        "autostart", "acer-helper.desktop");

    public string Label => "Start at login";

    public bool IsEnabled() => File.Exists(DesktopFile);

    public bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
                string exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                File.WriteAllText(DesktopFile,
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=Acer Helper\n" +
                    $"Exec=\"{exe}\"\n" +
                    "X-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(DesktopFile))
            {
                File.Delete(DesktopFile);
            }
            return true;
        }
        catch { return false; }
    }
}
