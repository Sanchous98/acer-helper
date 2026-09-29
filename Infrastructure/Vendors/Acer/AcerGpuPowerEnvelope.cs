using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Acer;

/// <summary>
/// The GPU power envelope as a port (<see cref="IGpuPowerEnvelope"/>), over the Acer EC HID channel. Lives in
/// this UN-SUFFIXED file for the same reason <c>EcSyncedProfiles</c> does: the test project targets
/// <c>net10.0-windows</c> while the app excludes <c>**/*.Linux.cs</c> from that TFM, so policy left in the Linux
/// file is unreachable by the suite. The policy here is the level list and the refuse-before-send guard; the I/O
/// stays with the controller and arrives as a delegate, exactly as the profile-side envelope does.
///
/// THE DELEGATE, NOT THE CONTROLLER: <c>AcerEcHidController</c> is sealed, internal and has no port interface, so
/// a class typed against it could not be driven by a test. A null delegate means this model has NO EC channel,
/// and every write is then a value — false, no throw — which is what the UI reads as "the row is hidden".
///
/// <see cref="LastError"/> is deliberately null: <c>AcerEcHidController.ApplyLevel</c> is enqueue-only and
/// fire-and-forget, so a false from it means "could not enqueue", not "the EC refused" — there is no transport
/// reason to carry. A caller that needs one would be inventing a failure the channel never reported.
/// </summary>
internal sealed class EcPowerEnvelope(Func<GpuPowerLevel, bool>? apply) : IGpuPowerEnvelope
{
    /// <summary>The EC's fixed rows this app offers, measured — the same table <c>AcerEcHidController.PowerLevels</c>
    /// holds, so the port and the byte mapping read one list.</summary>
    public IReadOnlyList<GpuPowerOption> Levels => AcerEcHidController.PowerLevels;

    /// <summary>Apply one row. A level the port does not offer is refused WITHOUT reaching the EC: an
    /// out-of-range mode is ACKed by the firmware exactly like a good one and then ignored, so sending it would
    /// look like success and move nothing. A null delegate — no EC channel — is the same value false.</summary>
    public bool SetLevel(GpuPowerLevel level)
    {
        if (apply is null || !AcerEcHidController.PowerLevels.Any(o => o.Level == level)) return false;
        return apply(level);
    }
}
