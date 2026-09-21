namespace AcerHelper.Infrastructure;

// THE WINDOWS HALF, and it is deliberately an answer rather than a probe: there is no /sys/kernel/ryzen_smu_drv
// on Windows at all — the CPU undervolt there goes through PawnIO (docs/pawnio.md) — so no node this package
// covers can exist and the grant can never be missing. Returning false is the same answer the Linux half gives
// for a machine without the driver, which is what keeps the policy's "absence is not a lost grant" rule true on
// both sides.
//
// It is a method with a body rather than a #if or a build error because the partial declaration in
// SmuMailboxAccess.cs has to have an implementation part in every TFM; RulesNeeded() also returns before it on a
// non-Linux OS, so in practice this is never the answer the user sees.

internal static partial class SmuMailboxAccessHost
{
    /// <summary>Always false: Windows has no sysfs and therefore no SMU nodes of this package's kind.</summary>
    internal static partial bool GrantMissing() => false;
}
