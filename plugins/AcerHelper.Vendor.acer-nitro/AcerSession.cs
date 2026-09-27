using System.Text.Json;
using System.Text.Json.Nodes;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Sdk;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Vendor.AcerNitro;

/// <summary>
/// THE ACER-NITRO SESSION: what <c>ah_create</c> probes and owns, and the one place the §3.5 op table is answered
/// (docs/vendor-plugins.md §3.4, §3.5, §6 Phase 1 step 3).
///
/// IT IS THE PLUGIN'S REPLACEMENT FOR <c>AcerDevice.InitVendor</c>. The host's wiring wrote into a
/// <c>GenericDevice</c>'s slots (<c>AcerDevice.Windows.cs:27-115</c>); this class instead probes the SAME
/// transports, keeps the SAME encoding helpers (ported into the plugin-owned <c>AcerSession.Windows.cs</c> /
/// <c>.Linux.cs</c>, because the host files extend <c>GenericDevice</c> and cannot move wholesale), and exposes
/// them through the manifest + <c>ah_invoke</c> rather than through host port objects.
///
/// THE MANIFEST IS DERIVED FROM WHAT WAS ACTUALLY WIRED. Each OS <c>ConnectPlatform</c> fills only the delegate
/// fields whose transport answered; <see cref="BuildManifest"/> declares a capability iff its field is non-null.
/// A capability with no transport is therefore ABSENT from the manifest, which is exactly how the host hides it
/// (§4.2 "A sub-object left null is 'this machine does not have that capability'"). This is the hard rule of the
/// slice: never declare what cannot be answered.
///
/// NO HOST TYPE IS TOUCHED. The session may use the SDK (<see cref="WmiInvoker"/>, <see cref="Hwmon"/>,
/// <see cref="SysfsLink"/>, <see cref="ProfileKind"/>, …), the ABI, <c>Domain</c> and the Acer files — never
/// <c>GenericDevice</c>, <c>LaptopService</c>, <c>Localization</c>, <c>Composition</c> or <c>UI</c>. Localization
/// keys travel as STRINGS (§3.4), never through <c>Loc.T</c>.
///
/// NO EXCEPTION CROSSES: every member here is callable from the thunk inside its try/catch, and the hardware
/// helpers swallow their own transport faults into statuses/reasons, matching the in-host ports' <c>(ok, error)</c>
/// shape.
/// </summary>
internal sealed partial class AcerSession
{
    private readonly AcerModel _model;
    private bool _disposed;

    // The wired capabilities. A NULL FIELD IS "THIS MACHINE DOES NOT HAVE IT": ConnectPlatform fills only the
    // ones whose transport answered, BuildManifest declares only those, and every op checks its own fields — the
    // plugin's version of the host's "a slot left null means absent" rule (Device.cs:19-22, §4.2).
    private IReadOnlyList<PerformanceProfile>? _profilesAll;
    private Func<IReadOnlyList<PerformanceProfile>>? _profilesSelectable;
    private Func<PerformanceProfile?>? _profilesCurrent;
    private Func<PerformanceProfile, (bool ok, string? error)>? _profilesSet;

    private FanCapability? _fan;
    private Func<FanMode, (bool ok, string? error)>? _fanMode;
    private Func<byte, byte, (bool ok, string? error)>? _fanSpeeds;

    private Func<SensorSnapshot>? _sensors;

    private Func<bool>? _chargeLimitRead;
    private Func<bool, (bool ok, string? error)>? _chargeLimitWrite;
    private Func<bool>? _calibRead;
    private Func<bool, (bool ok, string? error)>? _calibWrite;
    private Func<PowerSource>? _powerSource;

    private RgbDevice? _rgb;
    private bool _rgbReadBrightness;

    /// <summary>The EC HID performance-envelope channel; null when this model does not expose it. Shared by both
    /// OS halves (Windows uses it for the profile envelope + power source; Linux for the power source).</summary>
    private AcerEcHidController? _ec;

    private IGpuMux? _mux;

    private string? _statusMessage;

    private readonly List<SettingEntry> _settings = [];

    private AcerSession(AcerModel model) => _model = model;

    /// <summary>One declared setting's row: the manifest shape (§3.4) plus the value ops the Settings capability
    /// answers (§3.5). Read/Write are the SAME delegate shape the host's <c>FlagPort</c>/<c>ChoicePort</c> take, so
    /// the transport refusal <c>(ok, error)</c> travels unchanged.</summary>
    private sealed record SettingEntry(
        string Key,
        string Shape,
        IReadOnlyList<ChoiceOption>? Options,
        bool ReadbackVerifiesWrite,
        Func<string?>? Read,
        Func<string, (bool ok, string? error)>? Write);

    /// <summary>Probe the machine and build a session (the §3.8.4 gate-3 handshake is the caller's). Never
    /// returns null and never throws: a machine with no Acer hardware yields a session whose manifest declares
    /// the little that IS answerable (constraint 4 of the slice — be honest, not aspirational).</summary>
    internal static AcerSession Connect(string? product)
    {
        var session = new AcerSession(AcerModels.Detect(product));
        try
        {
            session.ConnectPlatform();
        }
        catch
        {
            // Probing must never escape as an exception (§3.2). Whatever was wired before the fault still forms
            // an honest (smaller) manifest; the rest stays absent.
        }
        return session;
    }

    /// <summary>The product name this session resolved (for diagnostics — <c>ah_create</c> does not return it).</summary>
    internal string ModelName => _model.Name;

    // ---- the probe manifest (§3.4) ---------------------------------------------------------------------------

    /// <summary>Build the probe manifest from the capabilities that were actually wired. The manifest SHAPE lives
    /// in the pure <see cref="AcerManifestBuilder"/> (so a test can drive it without hardware); this method only
    /// gathers what the probe wired into an <see cref="AcerManifestInput"/> — a field left null here is a
    /// capability absent from the manifest.</summary>
    internal byte[] BuildManifest()
    {
        var rgbZones = _rgb?.Zones
            .Select((z, i) => new AcerRgbZone(z.Name, z.SubZones, z.CanFollowProfile,
                                              ReadBrightness: _rgbReadBrightness && i == 0, z.Effects))
            .ToList();

        var input = new AcerManifestInput(
            PluginId: AcerPlugin.PluginId,
            Abi: $"{AcerPluginApi.Major}.{AcerPluginApi.Minor}",
            Vendor: "acer",
            ModelName: _model.Name,
            StatusMessage: _statusMessage,
            Profiles: _profilesAll,
            CurrentProfileId: SafeCurrentProfile()?.Id,
            Fan: _fan,
            Sensors: _sensors is not null,
            ChargeLimit: _chargeLimitRead is not null,
            Calibration: _calibRead is not null,
            PowerSource: _powerSource is not null,
            RgbZones: rgbZones,
            RgbProfileFollowKey: _rgb?.ProfileFollowKey,
            GpuMux: _mux is not null,
            GpuMuxSupported: _mux?.Supported ?? false,
            GpuMuxModes: _mux?.Supported == true ? _mux.Modes : null,
            Settings: _settings.Count == 0 ? null
                : _settings.Select(s => new AcerSettingDeclaration(s.Key, s.Shape, s.Options, s.ReadbackVerifiesWrite)).ToList());

        return AcerManifestBuilder.Build(input);
    }

    private PerformanceProfile? SafeCurrentProfile()
    {
        try { return _profilesCurrent?.Invoke(); }
        catch { return null; }
    }

    // ---- ah_invoke dispatch (§3.5) ---------------------------------------------------------------------------

    /// <summary>Answer one capability operation. Returns the ABI status and the JSON body. Never throws.</summary>
    internal (int Status, string Body) Invoke(uint capability, uint op, string requestJson)
    {
        if (_disposed) return (AbiStatus.NotSupported, Body(Error("error.session_disposed")));
        try
        {
            return capability switch
            {
                Capability.Power => Power(op, requestJson),
                Capability.Fan => Fan(op, requestJson),
                Capability.Sensors => Sensors(op),
                Capability.Battery => Battery(op, requestJson),
                Capability.Rgb => Rgb(op, requestJson),
                Capability.GpuMux => GpuMux(op, requestJson),
                Capability.Settings => Settings(op, requestJson),
                _ => (AbiStatus.NotSupported, Body(Error("error.unknown_capability"))),
            };
        }
        catch
        {
            return (AbiStatus.Internal, Body(Error("error.internal")));
        }
    }

    // ---- Power (1) -------------------------------------------------------------------------------------------

    private (int, string) Power(uint op, string request)
    {
        if (_profilesAll is null) return NotSupported();
        switch (op)
        {
            case Operation.Power.Selectable:
                return (AbiStatus.Ok, IdsBody(SafeSelectable().Select(p => p.Id)));
            case Operation.Power.Current:
                return (AbiStatus.Ok, Body(new JsonObject { ["id"] = SafeCurrentProfile()?.Id }));
            case Operation.Power.Set:
                return SetProfile(request);
            case Operation.Power.AvailableOn:
                var onAc = Bool(request, "onAc");
                return (AbiStatus.Ok, IdsBody(_profilesAll.Where(p => AcerProfiles.IsAvailable(p, onAc)).Select(p => p.Id)));
            case Operation.Power.Traits:
                return Traits(request);
            default:
                return NotSupported();
        }
    }

    private IReadOnlyList<PerformanceProfile> SafeSelectable()
    {
        try { return _profilesSelectable?.Invoke() ?? _profilesAll ?? []; }
        catch { return _profilesAll ?? []; }
    }

    private (int, string) SetProfile(string request)
    {
        var id = String(request, "id");
        if (id is null) return InvalidArg();
        var profile = _profilesAll!.FirstOrDefault(p => p.Id == id);
        if (profile is null || _profilesSet is null) return (AbiStatus.Refused, Body(Error("error.profile_not_offered")));

        var (ok, error) = _profilesSet(profile);
        return ok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error(error ?? "error.profile_set_failed")));
    }

    private (int, string) Traits(string request)
    {
        var id = String(request, "id");
        var profile = id is null ? null : _profilesAll!.FirstOrDefault(p => p.Id == id);
        if (profile is null) return (AbiStatus.Refused, Body(Error("error.profile_not_offered")));

        var traits = AcerProfiles.TraitsOf(profile);
        var body = new JsonObject
        {
            ["kind"] = traits.Kind.ToString(),
            ["accent"] = ColorArray(traits.Accent),
            ["flash"] = ColorArray(traits.FlashColor),
        };
        return (AbiStatus.Ok, Body(body));
    }

    private static JsonArray? ColorArray(AccentColor? c)
        => c is { } v ? new JsonArray(v.R, v.G, v.B) : null;

    // ---- Fan (2) ---------------------------------------------------------------------------------------------

    private (int, string) Fan(uint op, string request)
    {
        if (_fan is null || _fanMode is null || _fanSpeeds is null) return NotSupported();
        switch (op)
        {
            case Operation.Fan.SetMode:
                var modeName = String(request, "mode");
                if (modeName is null || !Enum.TryParse<FanMode>(modeName, ignoreCase: true, out var mode))
                    return InvalidArg();
                var (mok, merr) = _fanMode(mode);
                return mok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error(merr ?? "error.fan_mode_failed")));
            case Operation.Fan.SetCustomSpeeds:
                var (sok, serr) = _fanSpeeds((byte)Math.Clamp(Int(request, "cpu"), 0, 100),
                                             (byte)Math.Clamp(Int(request, "gpu"), 0, 100));
                return sok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error(serr ?? "error.fan_speed_failed")));
            default:
                return NotSupported();
        }
    }

    // ---- Sensors (3) -----------------------------------------------------------------------------------------

    private (int, string) Sensors(uint op)
    {
        if (op != Operation.Sensors.Read || _sensors is null) return NotSupported();
        var s = _sensors();
        var fans = new JsonArray();
        foreach (var fan in s.Fans)
            fans.Add((JsonNode)new JsonObject { ["label"] = fan.Label, ["rpm"] = fan.Rpm });
        var body = new JsonObject
        {
            ["cpuTempC"] = s.CpuTempC,
            ["gpuTempC"] = s.GpuTempC,
            ["fans"] = fans,
        };
        return (AbiStatus.Ok, Body(body));
    }

    // ---- Battery (4) -----------------------------------------------------------------------------------------

    private (int, string) Battery(uint op, string request)
    {
        switch (op)
        {
            case Operation.Battery.ReadToggle:
                var property = String(request, "property");
                var reader = property switch
                {
                    "chargeLimit" => _chargeLimitRead,
                    "calibration" => _calibRead,
                    _ => null,
                };
                return reader is null
                    ? NotSupported()
                    : (AbiStatus.Ok, Body(new JsonObject { ["value"] = reader() }));
            case Operation.Battery.WriteToggle:
                var wproperty = String(request, "property");
                var writer = wproperty switch
                {
                    "chargeLimit" => _chargeLimitWrite,
                    "calibration" => _calibWrite,
                    _ => null,
                };
                if (writer is null) return NotSupported();
                var (ok, error) = writer(Bool(request, "value"));
                return ok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error(error ?? "error.battery_write_failed")));
            case Operation.Battery.ReadPowerSource:
                return _powerSource is null
                    ? NotSupported()
                    : (AbiStatus.Ok, Body(new JsonObject { ["source"] = PowerSourceName(_powerSource()) }));
            default:
                return NotSupported();
        }
    }

    private static string PowerSourceName(PowerSource source) => source switch
    {
        PowerSource.Battery => "Battery",
        PowerSource.Barrel => "Barrel",
        PowerSource.UsbC => "UsbC",
        _ => "Unknown",
    };

    // ---- Rgb (6) ---------------------------------------------------------------------------------------------

    private (int, string) Rgb(uint op, string request)
    {
        if (_rgb is null) return NotSupported();
        switch (op)
        {
            case Operation.Rgb.ApplyEffect:
                return ApplyEffect(request);
            case Operation.Rgb.ApplySubZone:
                return ApplySubZone(request);
            case Operation.Rgb.SetProfileFlash:
                return _rgb.SetProfileFlash(ReadColor(request)) ? (AbiStatus.Ok, "{}")
                                                                : (AbiStatus.Refused, Body(Error("error.rgb_failed")));
            case Operation.Rgb.Blank:
                return _rgb.Blank() ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error("error.rgb_failed")));
            case Operation.Rgb.ReadBrightness:
                var zone = FindZone(String(request, "zone"));
                var level = zone?.ReadBrightness();
                return level is null
                    ? (AbiStatus.Refused, Body(Error("error.rgb_no_brightness")))
                    : (AbiStatus.Ok, Body(new JsonObject { ["brightness"] = level.Value }));
            default:
                return NotSupported();
        }
    }

    private (int, string) ApplyEffect(string request)
    {
        var zone = FindZone(String(request, "zone"));
        if (zone is null) return (AbiStatus.Refused, Body(Error("error.rgb_unknown_zone")));

        var handle = Int(request, "effectHandle");
        if (handle < 0 || handle >= zone.Effects.Count) return (AbiStatus.Refused, Body(Error("error.rgb_unknown_effect")));

        var ok = zone.ApplyEffect(zone.Effects[handle],
            (byte)Math.Clamp(Int(request, "brightness"), 0, 100),
            (byte)Math.Clamp(Int(request, "speed"), 0, 255),
            (byte)Math.Clamp(Int(request, "direction"), 0, 255),
            ReadColor(request));
        return ok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error("error.rgb_failed")));
    }

    private (int, string) ApplySubZone(string request)
    {
        var zone = FindZone(String(request, "zone"));
        if (zone is null) return (AbiStatus.Refused, Body(Error("error.rgb_unknown_zone")));

        var ok = zone.ApplySubZone(Int(request, "index"),
            (byte)Math.Clamp(Int(request, "brightness"), 0, 100), ReadColor(request));
        return ok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error("error.rgb_failed")));
    }

    private RgbZone? FindZone(string? name)
        => name is null ? null : _rgb?.Zones.FirstOrDefault(z => z.Name == name);

    private static AccentColor ReadColor(string request)
    {
        if (ReadArray(request, "color") is { Count: >= 3 } c)
            return new AccentColor((byte)Math.Clamp(c[0], 0, 255), (byte)Math.Clamp(c[1], 0, 255), (byte)Math.Clamp(c[2], 0, 255));
        return default;
    }

    // ---- GpuMux (8) ------------------------------------------------------------------------------------------

    private (int, string) GpuMux(uint op, string request)
    {
        if (_mux is null) return NotSupported();
        switch (op)
        {
            case Operation.GpuMux.Read:
                var state = _mux.Read();
                return (AbiStatus.Ok, Body(new JsonObject
                {
                    ["current"] = state.Current?.Id,
                    ["pending"] = state.Pending?.Id,
                    ["rebootRequired"] = state.RebootRequired,
                }));
            case Operation.GpuMux.Request:
                var id = String(request, "id");
                if (id is null) return InvalidArg();
                var change = _mux.Request(id);
                return change.Ok
                    ? (AbiStatus.Ok, Body(new JsonObject
                    {
                        ["ok"] = true, ["queued"] = change.Queued, ["noOp"] = change.NoOp,
                    }))
                    : (AbiStatus.Refused, Body(Error(change.Error ?? "error.gpu_mux_failed")));
            default:
                return NotSupported();
        }
    }

    // ---- Settings (9) ----------------------------------------------------------------------------------------

    private (int, string) Settings(uint op, string request)
    {
        var key = String(request, "key");
        var setting = key is null ? null : _settings.FirstOrDefault(s => s.Key == key);
        if (setting is null) return (AbiStatus.Refused, Body(Error("error.setting_not_declared")));

        switch (op)
        {
            case Operation.Settings.Read:
                var value = setting.Read?.Invoke();
                return value is null
                    ? (AbiStatus.Refused, Body(Error("error.setting_unreadable")))
                    : (AbiStatus.Ok, Body(new JsonObject { ["value"] = value }));
            case Operation.Settings.Set:
                var requested = String(request, "value");
                if (requested is null) return InvalidArg();
                var (ok, error) = setting.Write?.Invoke(requested) ?? (false, "error.setting_not_writable");
                return ok ? (AbiStatus.Ok, "{}") : (AbiStatus.Refused, Body(Error(error ?? "error.setting_write_failed")));
            default:
                return NotSupported();
        }
    }

    // ---- JSON helpers ----------------------------------------------------------------------------------------

    private static (int, string) NotSupported() => (AbiStatus.NotSupported, Body(Error("error.not_supported")));
    private static (int, string) InvalidArg() => (AbiStatus.InvalidArg, Body(Error("error.invalid_arg")));

    private static string Body(JsonObject o) => o.ToJsonString();
    private static JsonObject Error(string key) => new() { ["error"] = key };
    private static string IdsBody(IEnumerable<string> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids) array.Add((JsonNode)id);
        return Body(new JsonObject { ["ids"] = array });
    }

    private static JsonElement? Parse(string request)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request) ? "{}" : request);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? String(string request, string name)
        => Parse(request) is { } root && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(string request, string name)
        => Parse(request) is { } root && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(string request, string name)
        => Parse(request) is { } root && root.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static List<int>? ReadArray(string request, string name)
    {
        if (Parse(request) is not { } root || !root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<int>(v.GetArrayLength());
        foreach (var e in v.EnumerateArray())
            list.Add(e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 0);
        return list;
    }

    // ---- lifecycle -------------------------------------------------------------------------------------------

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisposePlatform(); } catch { /* best-effort teardown */ }
        _rgb?.Dispose();
        _rgb = null;
    }

    // ---- per-OS probe / teardown (AcerSession.Windows.cs / AcerSession.Linux.cs) ------------------------------

    /// <summary>Probe this OS's Acer transports and wire the capability fields the manifest is then built from.</summary>
    partial void ConnectPlatform();

    /// <summary>Release the per-OS transports (HID streams, WMI sessions, worker threads). Idempotent enough:
    /// called once from <see cref="Dispose"/>.</summary>
    partial void DisposePlatform();
}
