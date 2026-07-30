using AcerHelper.Features;
using AcerHelper.Vendors.Acer;
using AcerHelper.Vendors.Dell;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Composition;

/// <summary>
/// Composition root. Its ONLY job is to identify the machine (by DMI manufacturer, via
/// <see cref="MachineInfo"/>) and pick the vendor device; everything else — transports, per-feature
/// availability probing, port wiring — lives inside the vendor device itself (e.g. <see cref="AcerDevice"/>).
/// Unknown vendors fall back to the plain <see cref="GenericDevice"/>. Adding a vendor = one more branch here.
/// </summary>
public static class DeviceFactory
{
    public static IDevice Create()
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
        return device;
    }

    /// <summary>The OS's transport for publishing this laptop's zones as a virtual HID LampArray (Windows
    /// Dynamic Lighting), or null where there is none / the driver isn't installed. Kept here rather than in
    /// <see cref="LaptopService"/> so the OS choice stays in composition: the two implementations are picked by
    /// file name (LampArrayTransport.Windows.cs / .Linux.cs), like every other platform split.</summary>
    public static ILampArrayTransport? CreateLampArrayTransport() => LampArrayHost.Create();
}
