using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Infrastructure.Vendors.Asus;
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
    /// <summary>The machine: the model <see cref="Device"/> this laptop's backend filled while probing it, with
    /// the settings that backend declared travelling on it (<see cref="Device.DeclaredSettings"/>).
    ///
    /// A MACHINE AND NOT AN INVENTORY, which is what the return type used to be. It carried a tuple because the
    /// declared set had no home: the interface had no member for it, so composition had to hand the list over
    /// beside the device. A machine can hold what was declared about it, so the second half of the tuple is gone
    /// and the list is read off the model (<c>LaptopService</c>'s constructor is where).
    ///
    /// The list is COMPLETE by the time the machine is returned, and that is load-bearing rather than merely
    /// true: the settings model copies the set when it is built (<c>Settings</c>'s constructor), so a backend
    /// that declared after this line would be declaring into nothing. It holds because a backend declares during
    /// its own construction (<c>InitVendor</c>), and <see cref="GenericDevice.FinalizeComposition"/> only
    /// adjusts ports.</summary>
    public static Device Create()
    {
        var (manufacturer, product) = MachineInfo.Read();

        var device =
            manufacturer?.Contains("Acer", StringComparison.OrdinalIgnoreCase) == true ? new AcerDevice(product) :
            manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true ? new DellDevice(product) :   // "Dell Inc."
            // "ASUSTeK COMPUTER INC." contains "ASUS", so one test covers both DMI spellings.
            manufacturer?.Contains("ASUS", StringComparison.OrdinalIgnoreCase) == true ? new AsusDevice(product) :
            new GenericDevice();

        // Now that the vendor backend (if any) has finalized the port set — in particular whether the
        // performance profiles are a vendor WMI/EC port or the generic Windows overlay — let the device make
        // the composition decisions that depend on it (e.g. the overlay-CPU-power axis; see GenericDevice).
        device.FinalizeComposition();
        return device;
    }
}
