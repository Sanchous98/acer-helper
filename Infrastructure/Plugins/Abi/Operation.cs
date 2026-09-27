namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// The per-capability operation numbers passed to <c>ah_invoke(handle, capability, op, ...)</c>
/// (docs/vendor-plugins.md §3.5).
///
/// THE NUMBERS ARE PER-CAPABILITY, so the same small integers recur across capabilities (e.g. both
/// <see cref="Power.Selectable"/> and <see cref="Fan.SetMode"/> are 1) and a flat class would be a pile of
/// cannot-collide renames. Nested static classes are the shape that keeps each capability's op set explicit and
/// self-documenting: the reader sees the §3.5 table row-for-row, and <c>Operation.Battery.ReadPowerSource</c>
/// says which capability's "6" it means. The host always passes a (capability, op) PAIR, so a bare op is never
/// interpreted on its own.
///
/// THE NUMBERS ARE WIRE VALUES (§3.8.5): compiled into both sides by source (see <see cref="VendorAbi"/>), not
/// renumbered within a major. The request/response JSON shapes the table column names are documented at §3.5
/// and, for the v1 foundation, carried by the DTOs in <c>PluginJsonContext</c> where they are needed.
/// </summary>
internal static class Operation
{
    /// <summary>Capability.Power (1) — switchable performance/platform profiles.</summary>
    public static class Power
    {
        public const uint Selectable = 1;
        public const uint Current = 2;
        public const uint Set = 3;
        public const uint AvailableOn = 4;
        public const uint Traits = 5;
    }

    /// <summary>Capability.Fan (2) — fan behaviour and custom speeds.</summary>
    public static class Fan
    {
        public const uint SetMode = 1;
        public const uint SetCustomSpeeds = 2;
    }

    /// <summary>Capability.Sensors (3) — live temperature/RPM telemetry.</summary>
    public static class Sensors
    {
        public const uint Read = 1;
    }

    /// <summary>Capability.Battery (4) — the vendor's charge properties and power-source read.</summary>
    public static class Battery
    {
        public const uint ReadTelemetry = 1;
        public const uint ReadToggle = 2;
        public const uint WriteToggle = 3;
        public const uint ReadChoice = 4;
        public const uint WriteChoice = 5;
        public const uint ReadPowerSource = 6;
    }

    /// <summary>Capability.KeyboardBrightness (5) — plain (non-RGB) keyboard backlight levels.</summary>
    public static class KeyboardBrightness
    {
        public const uint Get = 1;
        public const uint Set = 2;
    }

    /// <summary>Capability.Rgb (6) — zone-based lighting.</summary>
    public static class Rgb
    {
        public const uint ApplyEffect = 1;
        public const uint ApplySubZone = 2;
        public const uint SetProfileFlash = 3;
        public const uint Blank = 4;
        public const uint ReadBrightness = 5;
    }

    /// <summary>Capability.GpuMux (8) — the discrete-GPU routing switch.</summary>
    public static class GpuMux
    {
        public const uint Read = 1;
        public const uint Request = 2;
    }

    /// <summary>Capability.Settings (9) — declared settings, read/written by opaque key (§3.4).</summary>
    public static class Settings
    {
        public const uint Read = 1;
        public const uint Set = 2;
    }

    // Capability.Hotkeys (7) has NO op: it is event-only and raises through ah_set_event_sink (§3.2, §3.5).
}
