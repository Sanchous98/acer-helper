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

        if (manufacturer?.Contains("Acer", StringComparison.OrdinalIgnoreCase) == true)
            return new AcerDevice(product);

        if (manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true)   // "Dell Inc."
            return new DellDevice(product);

        return new GenericDevice();
    }
}
