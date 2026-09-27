using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Adapters;

namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// THE MACHINE A LOADED PLUGIN BUILDS (docs/vendor-plugins.md §4.1, §4.2): a <see cref="Device"/> whose slots are
/// filled from the decoded probe manifest plus a handful of managed adapters over <see cref="IPluginSession"/>. It
/// is the plugin's stand-in for a vendor backend's <c>InitVendor</c> — the one place that turns "the plugin said
/// this machine has X" into the host's own port types.
///
/// A MANIFEST IS A PARTIAL STATEMENT, SO AN UNLISTED SLOT STAYS NULL (§1.1, Device.cs:19-22). Every capability
/// the manifest does not declare leaves its slot as it was — <c>null</c> for a port — which is exactly how a
/// backend that probed a feature absent leaves it. The plugin therefore says what it HAS, never what the host
/// must remove, and the "the manifest did not list the property" reading of the Acer/Dell removal semantics
/// (§4.2) applies to the battery as well as to the slots.
///
/// IT IS NOT WIRED INTO <see cref="DeviceFactory"/>, DELIBERATELY. Phase 0 builds and tests the adapter path but
/// composes nothing with it (§6, "Adapters … not yet wired into DeviceFactory", and "the 1123 existing test
/// methods stay green by construction: nothing in DeviceFactory changed"). A guard in the tests asserts the
/// factory does not name this type.
///
/// <see cref="Dispose"/> OWNS THE SESSION (§4.3): disposing this device calls <c>ah_dispose</c> and releases the
/// library handle exactly once, which is why the session is not merely registered through <c>Own</c> but disposed
/// by the override. The adapter path assumes the caller hands over a live session it no longer uses elsewhere;
/// production wiring would create one per winning plugin at composition.
///
/// <see cref="FinalizeComposition"/> IS A NO-OP TODAY, and that is the documented honest state: the base
/// <c>GenericDevice.FinalizeComposition</c> makes decisions that depend on the FINAL port set (e.g. the CPU-power
/// overlay axis is only valid when the profiles are not themselves the overlay — GenericDevice.cs:37-44). A
/// plugin that replaces the generic profiles may need the same re-decision, but the manifest carries no flag that
/// asks for it, so this method does nothing until a manifest field says otherwise. It exists so the eventual
/// wiring is one line, not a signature change.
/// </summary>
internal sealed class PluginVendorDevice : Device
{
    private readonly IPluginSession _session;
    private bool _disposed;

    public PluginVendorDevice(IPluginSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        var manifest = session.Manifest;
        var capabilities = manifest.Capabilities;

        // Device metadata. VendorName falls back to the base "Generic" when the plugin names none; StatusMessage
        // is a localization KEY the UI resolves (§3.4), never a sentence.
        if (manifest.VendorName is { Length: > 0 } vendorName) VendorName = vendorName;
        if (manifest.StatusMessage is { Length: > 0 } status) StatusMessage = status;

        if (capabilities is null) return;

        if (capabilities.PowerProfiles is { } power)
            PowerProfiles = new PluginPowerProfiles(session, power);

        if (capabilities.Fan is { } fan)
            FanControl = new PluginFanControl(session, fan);

        if (capabilities.Sensors is not null)
            Sensors = new PluginSensors(session);

        if (capabilities.Battery is { } battery)
            PluginBattery.Fill(Battery, session, battery);

        if (capabilities.KeyboardBrightness is { } keyboard)
            KeyboardBrightness = new PluginKeyboardBrightness(session, keyboard);

        if (capabilities.Rgb is { } rgb)
            Lighting = new PluginRgbDevice(session, rgb);

        if (capabilities.GpuMux is { } gpuMux)
            GpuMux = new PluginGpuMux(session, gpuMux);

        if (capabilities.Hotkeys is not null)
        {
            // The native event sink is not built yet (§7 item 9, IPluginEventSource). A hotkeys declaration is
            // therefore ATTACHED with a source that never fires until the future wiring supplies one — the slot is
            // non-null so the capability is visible, rather than silently absent. The source is disposable and the
            // adapter is owned, so Dispose releases both.
            var hotkeys = new PluginHotkeys(new SilentPluginEventSource());
            Own(hotkeys);
            Hotkeys = hotkeys;
        }

        if (capabilities.DeclaredSettings is { } declared)
            foreach (var entry in declared)
                if (PluginDeclaredSetting.Build(session, entry) is { } setting)
                    Declare(setting);
    }

    /// <summary>Release the session (which runs <c>ah_dispose</c>) and everything registered through
    /// <c>Own</c> (the hotkey adapter). IDEMPOTENT at this level, not merely through the session's own guard: the
    /// base <see cref="Device.Dispose"/> disposes the <c>Own</c> list on every call, so a second call would
    /// re-dispose the hotkey adapter unless the whole override is guarded here. The guard is what makes "disposed
    /// exactly once" true for the object a caller holds.</summary>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        base.Dispose();
    }

    /// <summary>The counterpart of <c>GenericDevice.FinalizeComposition</c> (§4.1 step 5). A no-op today — see the
    /// class note — kept so the eventual composition wiring calls the same method on every device.
    /// </summary>
    internal void FinalizeComposition()
    {
        // Deliberately empty: no manifest field asks for a composition decision yet.
    }

    /// <summary>The declared, non-firing event source the hotkey adapter is built over until the native sink is
    /// wired (see <see cref="IPluginEventSource"/>). It exists so a manifest that declares hotkeys yields a
    /// visible, disposable <see cref="IHotkeys"/> slot rather than a null one, without inventing native plumbing.
    /// </summary>
    private sealed class SilentPluginEventSource : IPluginEventSource
    {
        public event Action<HotkeyAction>? Pressed
        {
            add { }
            remove { }
        }

        public event Action? InputActivity
        {
            add { }
            remove { }
        }
    }
}

/// <summary>
/// The keyboard-brightness adapter (docs/vendor-plugins.md §3.4, §3.5): <see cref="IKeyboardBrightness"/> over
/// <c>Capability.KeyboardBrightness</c>. It lives in this file because the Phase 0 task scopes the owned files to
/// the capabilities the proof plugin exercises; it is the same shape the in-host <c>LevelPort</c> has
/// (Infrastructure/Plugins/Sdk/DelegatePorts.cs:84-90). <see cref="MaxLevel"/> comes from the manifest; the
/// level reads and writes are calls.
/// </summary>
internal sealed class PluginKeyboardBrightness : IKeyboardBrightness
{
    private readonly IPluginSession _session;

    public PluginKeyboardBrightness(IPluginSession session, KeyboardBrightnessManifest manifest)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(manifest);
        MaxLevel = manifest.MaxLevel;
    }

    public string? LastError { get; private set; }
    public int MaxLevel { get; }

    /// <summary><c>Op.Get</c> (§3.5) → <c>{"level":0}</c>; a refusal or bad body reads as 0 (off).</summary>
    public int Get()
    {
        var result = _session.Invoke(Capability.KeyboardBrightness, Operation.KeyboardBrightness.Get,
                                     PluginBodies.Empty);
        return result.Status == AbiStatus.Ok
            ? PluginBodies.IntProperty(result.Body, "level", fallback: 0)
            : 0;
    }

    /// <summary><c>Op.Set</c> (§3.5) → <c>{"level":2}</c>; the reason on refusal belongs to this call.</summary>
    public bool Set(int level)
    {
        var request = new JsonObject { ["level"] = level }.ToJsonString();
        var result = _session.Invoke(Capability.KeyboardBrightness, Operation.KeyboardBrightness.Set, request);
        LastError = result.Status == AbiStatus.Ok ? null : PluginBodies.ErrorOf(result);
        return result.Status == AbiStatus.Ok;
    }
}

/// <summary>
/// The RGB adapter (docs/vendor-plugins.md §3.4, §3.5): <see cref="IRgbDevice"/> built from the manifest's zone
/// declarations, forwarding the effect/sub-zone/blank/profile-flash ops to <c>Capability.Rgb</c>. It lives in this
/// file for the same scoping reason as <see cref="PluginKeyboardBrightness"/>. The ZONES are manifest data (name,
/// sub-zone count, effect list, follow-profile flag), so the host builds the same <see cref="RgbZone"/> bricks the
/// in-host <c>RgbDevice</c> concatenates — only the apply ops cross the boundary.
/// </summary>
internal sealed class PluginRgbDevice : IRgbDevice
{
    private readonly IPluginSession _session;

    public PluginRgbDevice(IPluginSession session, RgbManifest manifest)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(manifest);
        ProfileFollowKey = manifest.ProfileFollowKey;

        var zones = new List<RgbZone>(manifest.Zones?.Count ?? 0);
        if (manifest.Zones is { } declared)
            foreach (var zone in declared)
            {
                var name = zone.Name ?? "";
                var effects = new List<RgbModeInfo>(zone.Effects?.Count ?? 0);
                if (zone.Effects is { } declaredEffects)
                    foreach (var effect in declaredEffects)
                        effects.Add(new RgbModeInfo(effect.Name ?? "", effect.HasColor, effect.HasSpeed,
                                                    effect.Handle));

                // The manifest's opaque handle is an int (§3.4); RgbModeInfo carries it as object, so the
                // closure captures the int directly rather than unboxing the domain record's handle back out.
                zones.Add(new RgbZone(
                    name,
                    zone.SubZones,
                    effects,
                    applyEffect: (effect, brightness, speed, direction, color) =>
                        ApplyEffect(name, effect.Handle is int handle ? handle : 0,
                                    brightness, speed, direction, color),
                    applySubZone: (index, brightness, color) => ApplySubZone(name, index, brightness, color),
                    readBrightness: zone.ReadBrightness ? () => ReadBrightness(name) : null,
                    canFollowProfile: zone.CanFollowProfile));
            }
        Zones = zones;
    }

    public IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>The manifest's follow-profile settings key, or null (§3.4).</summary>
    public string? ProfileFollowKey { get; }

    /// <summary><c>Op.SetProfileFlash</c> (§3.5) → <c>{"color":[r,g,b]}</c>.</summary>
    public bool SetProfileFlash(AccentColor color)
    {
        var request = new JsonObject { ["color"] = ColorJson(color) }.ToJsonString();
        return _session.Invoke(Capability.Rgb, Operation.Rgb.SetProfileFlash, request).Status == AbiStatus.Ok;
    }

    /// <summary><c>Op.Blank</c> (§3.5).</summary>
    public bool Blank() =>
        _session.Invoke(Capability.Rgb, Operation.Rgb.Blank, PluginBodies.Empty).Status == AbiStatus.Ok;

    private bool ApplyEffect(string zone, int effectHandle, byte brightness, byte speed, byte direction,
                             AccentColor color)
    {
        var request = new JsonObject
        {
            ["zone"] = zone,
            ["effectHandle"] = effectHandle,
            ["brightness"] = brightness,
            ["speed"] = speed,
            ["direction"] = direction,
            ["color"] = ColorJson(color),
        }.ToJsonString();
        return _session.Invoke(Capability.Rgb, Operation.Rgb.ApplyEffect, request).Status == AbiStatus.Ok;
    }

    private bool ApplySubZone(string zone, int index, byte brightness, AccentColor color)
    {
        var request = new JsonObject
        {
            ["zone"] = zone,
            ["index"] = index,
            ["brightness"] = brightness,
            ["color"] = ColorJson(color),
        }.ToJsonString();
        return _session.Invoke(Capability.Rgb, Operation.Rgb.ApplySubZone, request).Status == AbiStatus.Ok;
    }

    private int? ReadBrightness(string zone)
    {
        var request = new JsonObject { ["zone"] = zone }.ToJsonString();
        var result = _session.Invoke(Capability.Rgb, Operation.Rgb.ReadBrightness, request);
        return result.Status == AbiStatus.Ok
            ? PluginBodies.IntProperty(result.Body, "brightness", fallback: -1) is var level && level >= 0
                ? level : null
            : null;
    }

    private static JsonArray ColorJson(AccentColor color) => [color.R, color.G, color.B];
}

/// <summary>
/// The GPU-MUX adapter (docs/vendor-plugins.md §3.4, §3.5): <see cref="IGpuMux"/> over <c>Capability.GpuMux</c>. It
/// lives in this file for the same scoping reason. <see cref="Supported"/> and <see cref="Modes"/> are manifest
/// data; <see cref="Read"/> and <see cref="Request"/> are calls. The port's safety contract (queue, never apply —
/// Domain/GpuMux.cs:5-13) is the plugin's to honour; the adapter only carries the payload.
/// </summary>
internal sealed class PluginGpuMux : IGpuMux
{
    private readonly IPluginSession _session;
    private readonly Dictionary<string, ChoiceOption> _byId = new(StringComparer.Ordinal);

    public PluginGpuMux(IPluginSession session, GpuMuxManifest manifest)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(manifest);
        Supported = manifest.Supported;

        var modes = new List<ChoiceOption>(manifest.Modes?.Count ?? 0);
        if (manifest.Modes is { } declared)
            foreach (var mode in declared)
            {
                var option = new ChoiceOption(mode.Id ?? "", mode.DisplayName ?? "");
                modes.Add(option);
                _byId.TryAdd(option.Id, option);
            }
        Modes = modes;
    }

    public string? LastError { get; private set; }
    public bool Supported { get; }
    public IReadOnlyList<ChoiceOption> Modes { get; }

    /// <summary><c>Op.Read</c> (§3.5) → <c>{"current":"1","pending":null,…}</c>, folded through
    /// <see cref="GpuMuxState.From"/> so a pending value equal to the current one is reported as no change. Ids the
    /// manifest does not list are unknown to this build and read as null rather than invented.</summary>
    public GpuMuxState Read()
    {
        var result = _session.Invoke(Capability.GpuMux, Operation.GpuMux.Read, PluginBodies.Empty);
        if (result.Status != AbiStatus.Ok) return GpuMuxState.From(null, null);

        return GpuMuxState.From(Lookup(PluginBodies.StringProperty(result.Body, "current")),
                                Lookup(PluginBodies.StringProperty(result.Body, "pending")));
    }

    /// <summary><c>Op.Request</c> (§3.5) → <c>{"ok":true,"queued":true,"noOp":false,"error":null}</c>. A refusal
    /// carries the plugin's reason, and sets <see cref="LastError"/> for a caller that reads it after a returned
    /// write, matching the port's own field.</summary>
    public GpuMuxChange Request(string modeId)
    {
        ArgumentNullException.ThrowIfNull(modeId);
        var request = PluginBodies.Id(modeId);
        var result = _session.Invoke(Capability.GpuMux, Operation.GpuMux.Request, request);

        if (result.Status != AbiStatus.Ok)
        {
            var error = PluginBodies.ErrorOf(result);
            LastError = error;
            return new GpuMuxChange(Ok: false, Queued: false, Error: error);
        }

        LastError = null;
        var ok = PluginBodies.BoolProperty(result.Body, "ok", fallback: true);
        var queued = PluginBodies.BoolProperty(result.Body, "queued", fallback: false);
        var noOp = PluginBodies.BoolProperty(result.Body, "noOp", fallback: false);
        return new GpuMuxChange(Ok: ok, Queued: queued, Error: null, NoOp: noOp);
    }

    private ChoiceOption? Lookup(string? id)
        => id is not null && _byId.TryGetValue(id, out var option) ? option : null;
}
