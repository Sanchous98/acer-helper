namespace AcerHelper.Vendors.Generic;

// Generic Windows common capabilities: performance profiles via the power-mode overlay, blue-light gamma,
// and clamshell. (Battery telemetry + autostart are wired cross-platform in GenericDevice.cs.) A vendor
// backend extends this and overrides/adds its own.
public partial class GenericDevice
{
    partial void InitPlatform()
    {
        var profiles = new OverlayPowerProfiles();
        if (profiles.Available) PowerProfiles = profiles;

        DisplayTint = new DisplayTint();

        var clamshell = new Clamshell();
        if (clamshell.Supported) { Clamshell = clamshell; Own(clamshell); }
    }
}
