using System.IO;

namespace AcerHelper.Vendors.Generic;

// Run at login via a freedesktop autostart entry (~/.config/autostart/acer-helper.desktop).
public sealed partial class Autostart
{
    private static string DesktopFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),  // ~/.config
        "autostart", "acer-helper.desktop");

    public partial string Label => "Start at login";

    public partial bool IsEnabled() => File.Exists(DesktopFile);

    public partial bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
                File.WriteAllText(DesktopFile,
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=Acer Helper\n" +
                    $"Exec=\"{ExePath}\"\n" +
                    "Terminal=false\n" +
                    // Tray app with only a skip-taskbar flyout: without this the login launch-feedback lingers
                    // as a stray taskbar icon (never cleared by a normal window mapping). See acer-helper.desktop.
                    "StartupNotify=false\n" +
                    "StartupWMClass=AcerHelper\n" +
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
