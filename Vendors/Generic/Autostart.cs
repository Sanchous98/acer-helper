using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Run-at-login. The mechanism is OS-specific (a Scheduled Task on Windows, a freedesktop
/// autostart .desktop on Linux) and lives in the partial members of Autostart.*.cs; the shared
/// bits — the <see cref="IAutostart"/> contract and the executable path — live here.</summary>
public sealed partial class Autostart : IAutostart
{
    /// <summary>The running executable (single-file aware), used to register autostart.</summary>
    private static string ExePath => Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    public partial string Label { get; }
    public partial bool IsEnabled();
    public partial bool SetEnabled(bool enable);
}
