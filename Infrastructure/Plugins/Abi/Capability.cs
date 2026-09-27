namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// The capability codes the host passes to <c>ah_invoke(handle, capability, op, ...)</c>
/// (docs/vendor-plugins.md §3.5). Each code names a VENDOR-OVERRIDABLE port — the ones a plugin may replace or
/// extend — because a capability code is exactly the question "which vendor slot is this call about".
///
/// The generic, cross-vendor axes (display tint, GPU overclock, CPU power, curve optimizer, autostart,
/// clamshell, core affinity, and generic battery telemetry) have NO code here: the host builds those in
/// <c>GenericDevice</c> itself and never routes them through a plugin (§3.5, §4.2). A code this file does not
/// name is therefore not "an unused op" but "a slot the ABI deliberately does not cross".
///
/// THE NUMBERS ARE WIRE VALUES (§3.8.5): they are compiled into both sides by source (see
/// <see cref="VendorAbi"/>) and may not be renumbered within a major.
/// </summary>
internal static class Capability
{
    public const uint Power = 1;
    public const uint Fan = 2;
    public const uint Sensors = 3;
    public const uint Battery = 4;
    public const uint KeyboardBrightness = 5;
    public const uint Rgb = 6;
    public const uint Hotkeys = 7;
    public const uint GpuMux = 8;
    public const uint Settings = 9;
}
