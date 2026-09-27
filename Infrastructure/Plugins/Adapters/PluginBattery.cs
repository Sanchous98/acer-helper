using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Plugins.Adapters;

/// <summary>
/// The battery adapter (docs/vendor-plugins.md §3.5, §4.2): it fills the vendor-overridable properties of the
/// host <see cref="Battery"/> object from the plugin, reusing the exact delegates that object already takes —
/// <see cref="BatteryToggle"/> and <see cref="BatteryChoice"/> (Domain/Battery.cs:63-69). It is NOT a port type
/// of its own, because the battery is a domain object that declares its own properties one by one rather than an
/// interface with four nullable slots.
///
/// "THE MANIFEST DID NOT LIST THE PROPERTY" IS THE REMOVAL SEMANTICS (§4.2). The Acer and Dell backends add a
/// property only when their own probe found it (AcerDevice.Windows.cs:101-103; DellDevice.Linux.cs:60) and drop
/// one the firmware itself supersedes; here the manifest's four presence flags are that same statement, read once
/// at composition. <see cref="Fill"/> assigns ONLY the properties the manifest listed and leaves every other
/// property exactly as it found it — so a slot the manifest is silent about stays whatever the host built (the
/// generic telemetry, or a property another backend added).
///
/// THE GENERIC TELEMETRY IS HOST-SIDE AND IS NOT WIRED HERE. §1.1 and the manifest's own note put
/// <see cref="Battery.Telemetry"/> in the cross-vendor group
/// (<c>Infrastructure/Plugins/PluginManifest.cs</c>, <c>BatteryManifest</c>), so a plugin has no telemetry op in
/// the manifest and this adapter touches only the four vendor properties §3.4 lists. The <c>ReadTelemetry</c> op
/// exists in §3.5 for a future version; wiring it would be a behaviour change this task must not make.
///
/// THE WRITE'S REASON BELONGS TO THE WRITE (Domain/Battery.cs:55-63, LaptopService.cs:151-160): each write
/// delegate returns <c>(ok, error)</c> from its own call, never a field read afterwards, so a concurrent call
/// cannot hand a caller another call's failure. The error text is parsed straight out of THIS invoke's refusal
/// body, the same rule the power-profiles adapter follows.
/// </summary>
internal static class PluginBattery
{
    /// <summary>
    /// Add the properties the manifest declared to <paramref name="battery"/>. A null or empty manifest is a
    /// no-op, as is a flag that is false: absence and <c>false</c> both mean "this machine does not have that
    /// property", which is the same thing to <see cref="Battery"/>.
    /// </summary>
    public static void Fill(Battery battery, IPluginSession session, BatteryManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(battery);
        ArgumentNullException.ThrowIfNull(session);
        if (manifest is null) return;

        if (manifest.ChargeLimit)
            battery.ChargeLimit = new BatteryToggle(Read(session, "chargeLimit"), Write(session, "chargeLimit"));

        if (manifest.Calibration)
            battery.Calibration = new BatteryToggle(Read(session, "calibration"), Write(session, "calibration"));

        if (manifest.ChargeMode)
            // A BatteryChoice carries its own option list, so its read/write are plain delegates over the
            // firmware's current id. The options themselves are NOT in the manifest today (§3.4 lists only the
            // presence flag), so the set is whatever the plugin's ReadChoice answers as its current id — a
            // future minor may add the list without changing this shape (§3.8.5).
            battery.ChargeMode = new BatteryChoice([], ReadChoice(session), WriteChoice(session));

        if (manifest.PowerSource)
            battery.PowerSource = () => ReadPowerSource(session);
    }

    /// <summary><c>Op.ReadToggle</c> (§3.5): <c>{"property":"chargeLimit"}</c> → <c>{"value":true}</c>. A
    /// non-<c>Ok</c> status or a bad body reads as false, the toggle's own "off" — the same degrade-to-default
    /// every read in this tree takes when it cannot answer (Domain/Battery.cs:52).</summary>
    private static Func<bool> Read(IPluginSession session, string property) => () =>
    {
        var request = new JsonObject { ["property"] = property }.ToJsonString();
        var result = session.Invoke(Capability.Battery, Operation.Battery.ReadToggle, request);
        return result.Status == AbiStatus.Ok
            && PluginBodies.BoolProperty(result.Body, "value", fallback: false);
    };

    /// <summary><c>Op.WriteToggle</c> (§3.5): <c>{"property":"calibration","value":true}</c> → <c>{}</c> or
    /// <c>{"error":…}</c>. Returns both halves of the outcome, the battery property's contract.</summary>
    private static Func<bool, (bool ok, string? error)> Write(IPluginSession session, string property) => value =>
    {
        var request = new JsonObject { ["property"] = property, ["value"] = value }.ToJsonString();
        var result = session.Invoke(Capability.Battery, Operation.Battery.WriteToggle, request);
        return result.Status == AbiStatus.Ok ? (true, null) : (false, PluginBodies.ErrorOf(result));
    };

    /// <summary><c>Op.ReadChoice</c> (§3.5): <c>{"property":"chargeMode"}</c> → <c>{"id":"Adaptive"}</c>. Null
    /// when unreadable, the choice's own "would not answer".</summary>
    private static Func<string?> ReadChoice(IPluginSession session) => () =>
    {
        var request = new JsonObject { ["property"] = "chargeMode" }.ToJsonString();
        var result = session.Invoke(Capability.Battery, Operation.Battery.ReadChoice, request);
        return result.Status == AbiStatus.Ok ? PluginBodies.StringProperty(result.Body, "id") : null;
    };

    /// <summary><c>Op.WriteChoice</c> (§3.5): <c>{"property":"chargeMode","id":"Custom"}</c> → <c>{}</c> or an
    /// error body.</summary>
    private static Func<string, (bool ok, string? error)> WriteChoice(IPluginSession session) => id =>
    {
        var request = new JsonObject { ["property"] = "chargeMode", ["id"] = id }.ToJsonString();
        var result = session.Invoke(Capability.Battery, Operation.Battery.WriteChoice, request);
        return result.Status == AbiStatus.Ok ? (true, null) : (false, PluginBodies.ErrorOf(result));
    };

    /// <summary><c>Op.ReadPowerSource</c> (§3.5): <c>{"source":"UsbC"}</c>. Maps the enum's own name onto
    /// <see cref="PowerSource"/>; an unknown string, a refusal or a malformed body is
    /// <see cref="PowerSource.Unknown"/>, which the UI hides rather than showing a made-up source
    /// (Domain/Models.cs:99-105).</summary>
    private static PowerSource ReadPowerSource(IPluginSession session)
    {
        var result = session.Invoke(Capability.Battery, Operation.Battery.ReadPowerSource, PluginBodies.Empty);
        if (result.Status != AbiStatus.Ok) return PowerSource.Unknown;

        return PluginBodies.StringProperty(result.Body, "source") switch
        {
            "Battery" => PowerSource.Battery,
            "Barrel"  => PowerSource.Barrel,
            "UsbC"    => PowerSource.UsbC,
            _         => PowerSource.Unknown,
        };
    }
}
