using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Infrastructure.Vendors.Dell;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>
/// Composition root. Its ONLY job is to identify the machine (by DMI manufacturer, via
/// <see cref="MachineInfo"/>) and pick the vendor device; everything else — transports, per-feature
/// availability probing, port wiring — lives inside the vendor device itself (e.g. <see cref="AcerDevice"/>).
/// Unknown vendors fall back to the plain <see cref="GenericDevice"/>. Adding a vendor = one more branch here.
/// </summary>
public static class DeviceFactory
{
    /// <summary>The machine, and the settings its backend declared while probing it.
    ///
    /// The two travel together because the declared set does not belong to the device any more: the settings
    /// MODEL holds it and switches it (Domain/Settings.cs), and <see cref="IDevice"/> carries no member for it,
    /// so this factory is the one place that can hand the backend's own list to the service that installs it —
    /// the concrete device is the only thing that has it.
    ///
    /// The list is complete by the time it is read: a backend declares during its own construction
    /// (<c>InitVendor</c>), and <see cref="GenericDevice.FinalizeComposition"/> only adjusts ports.</summary>
    public static (IDevice Device, IReadOnlyList<SettingDeclaration> DeclaredSettings) Create()
    {
        var (manufacturer, product) = MachineInfo.Read();

        var device =
            manufacturer?.Contains("Acer", StringComparison.OrdinalIgnoreCase) == true ? new AcerDevice(product) :
            manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true ? new DellDevice(product) :   // "Dell Inc."
            new GenericDevice();

        // Now that the vendor backend (if any) has finalized the port set — in particular whether the
        // performance profiles are a vendor WMI/EC port or the generic Windows overlay — let the device make
        // the composition decisions that depend on it (e.g. the overlay-CPU-power axis; see GenericDevice).
        device.FinalizeComposition();
        return (device, device.DeclaredSettings);
    }

    /// <summary>How the application obtains this machine's virtual LampArray surface (Windows Dynamic
    /// Lighting): the factory it names, built over this OS's transport — or null where the OS has none or the
    /// driver isn't installed, which is what keeps the feature (and its Options row) absent instead of promising
    /// a device that cannot exist. Kept here rather than in <see cref="LaptopService"/> so the OS choice stays
    /// in composition: the two implementations are picked by file name (LampArrayTransport.Windows.cs /
    /// .Linux.cs), like every other platform split.</summary>
    public static IDynamicLightingFactory? CreateDynamicLightingFactory()
        => LampArrayHost.Create() is { } transport ? new LampArrayBridgeFactory(transport) : null;
}
