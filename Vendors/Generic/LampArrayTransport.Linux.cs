using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Composition helper: the LampArray transport for this OS, or null where there is none.
///
/// Linux has no equivalent consumer of HID LampArray in the desktop stack (Dynamic Lighting is a Windows
/// feature), so this returns null and the whole bridge stays absent — the Options row for it never appears.
///
/// It is, however, the cheap side to implement if that changes: <c>/dev/uhid</c> lets a plain user-space
/// process create a HID device with an arbitrary report descriptor and answer GET/SET_REPORT itself, so the
/// same <see cref="ILampArrayTransport"/> could be satisfied here with no kernel module and no code signing at
/// all (the kernel's own virtual LampArray for TUXEDO laptops is the same idea, done in-kernel). See
/// docs/lamparray.md.</summary>
internal static class LampArrayHost
{
    public static ILampArrayTransport? Create() => null;
}
