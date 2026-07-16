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

        GenericDevice device =
            manufacturer?.Contains("Acer", StringComparison.OrdinalIgnoreCase) == true ? new AcerDevice(product) :
            manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true ? new DellDevice(product) :   // "Dell Inc."
            new GenericDevice();

        // Now that the vendor backend (if any) has finalized the port set — in particular whether the
        // performance profiles are a vendor WMI/EC port or the generic Windows overlay — let the device make
        // the composition decisions that depend on it (e.g. the overlay-CPU-power axis; see GenericDevice).
        device.FinalizeComposition();
        return device;
    }
}
