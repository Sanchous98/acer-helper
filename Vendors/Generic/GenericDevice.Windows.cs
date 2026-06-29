using AcerHelper.Os;
using AcerHelper.Composition;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// The generic Windows device: the basics any laptop has through standard OS APIs — performance
/// profiles via the power-mode overlay, blue-light gamma, autostart, clamshell. Used as the
/// fallback when no vendor (e.g. Acer) backend matches, or when the vendor WMI isn't accessible.
/// </summary>
public static class GenericDevice
{
    public static IDevice Create(string? status = null)
    {
        var profiles = new OverlayPowerProfiles();
        var clamshell = new Clamshell();
        return new CompositeDevice
        {
            VendorName    = "Generic",
            StatusMessage = status,
            PowerProfiles = profiles.Available ? profiles : null,
            DisplayTint   = new DisplayTint(),
            Autostart     = new Autostart(),
            Clamshell     = clamshell.Supported ? clamshell : null,
            Owned = [clamshell],
        };
    }
}
