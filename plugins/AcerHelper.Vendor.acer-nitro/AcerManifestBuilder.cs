using System.Text.Json;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Vendor.AcerNitro;

/// <summary>One declared setting as the manifest carries it (§3.4): a key, a shape and (for a choice) its options.
/// A pure data row, separate from the session's value ops, so the manifest builder can be driven without one.</summary>
internal sealed record AcerSettingDeclaration(
    string Key, string Shape, IReadOnlyList<ChoiceOption>? Options, bool ReadbackVerifiesWrite);

/// <summary>One RGB zone as the manifest carries it (§3.4). Effects are the codec's own list; the builder assigns
/// each effect its positional handle (the index into this list), which is what the host adapter and the plugin's
/// <c>Rgb</c> op agree on.</summary>
internal sealed record AcerRgbZone(
    string Name, int SubZones, bool CanFollowProfile, bool ReadBrightness, IReadOnlyList<RgbModeInfo> Effects);

/// <summary>
/// THE DECLARED CAPABILITIES OF ONE SESSION, AS PLAIN DATA (§3.4). This is the seam that makes the manifest
/// testable without hardware: <see cref="AcerSession"/> builds one from what its probe actually wired, and a test
/// builds one by hand and asserts the JSON it yields. A capability that is absent here is absent from the
/// manifest — the "null means this machine does not have it" rule (Device.cs:19-22, §4.2) made explicit.
///
/// It carries the model NAME, the STATUS KEY and the plugin identity too, because the manifest's top level is the
/// same statement as its capabilities — "what this plugin is and what this machine has".
/// </summary>
internal sealed record AcerManifestInput(
    string PluginId,
    string Abi,
    string Vendor,
    string ModelName,
    string? StatusMessage,
    IReadOnlyList<PerformanceProfile>? Profiles,
    string? CurrentProfileId,
    FanCapability? Fan,
    bool Sensors,
    bool ChargeLimit,
    bool Calibration,
    bool PowerSource,
    IReadOnlyList<AcerRgbZone>? RgbZones,
    string? RgbProfileFollowKey,
    bool GpuMux,
    bool GpuMuxSupported,
    IReadOnlyList<ChoiceOption>? GpuMuxModes,
    IReadOnlyList<AcerSettingDeclaration>? Settings);

/// <summary>
/// BUILD THE §3.4 PROBE MANIFEST FROM <see cref="AcerManifestInput"/>. Pure and hardware-free: it is the whole
/// "what does this machine have" statement, serialised through the shared source-generated context so the plugin
/// emits exactly the shape the host decodes (§3.3, §3.4) — never a hand-typed literal free to drift.
///
/// The Acer profile table's traits and per-source availability are derived from <c>AcerProfiles</c> here, so the
/// session only has to say "profiles are wired" and name the current one.
/// </summary>
internal static class AcerManifestBuilder
{
    internal static byte[] Build(AcerManifestInput input)
    {
        var capabilities = new CapabilitiesManifest();

        if (input.Profiles is { } profiles)
        {
            capabilities.PowerProfiles = new PowerProfilesManifest
            {
                All = profiles.Select(p => new ProfileManifest { Id = p.Id, DisplayName = p.DisplayName }).ToList(),
                CurrentId = input.CurrentProfileId,
                AvailableOnAc = profiles.Where(p => AcerProfiles.IsAvailable(p, onAc: true)).Select(p => p.Id).ToList(),
                AvailableOnBattery = profiles.Where(p => AcerProfiles.IsAvailable(p, onAc: false)).Select(p => p.Id).ToList(),
                Traits = profiles.Select(Trait).ToList(),
            };
        }

        if (input.Fan is { } fan)
            capabilities.Fan = new FanManifest
            {
                Capability = new FanCapabilityManifest
                {
                    HasMax = fan.HasMax, HasCustom = fan.HasCustom, HasGpuFan = fan.HasGpuFan,
                },
            };

        if (input.Sensors) capabilities.Sensors = new SensorsManifest();

        if (input.ChargeLimit || input.Calibration || input.PowerSource)
            capabilities.Battery = new BatteryManifest
            {
                ChargeLimit = input.ChargeLimit,
                Calibration = input.Calibration,
                ChargeMode = false,
                PowerSource = input.PowerSource,
            };

        if (input.RgbZones is { Count: > 0 } zones)
            capabilities.Rgb = new RgbManifest
            {
                ProfileFollowKey = input.RgbProfileFollowKey,
                Zones = zones.Select(z => new RgbZoneManifest
                {
                    Name = z.Name,
                    SubZones = z.SubZones,
                    CanFollowProfile = z.CanFollowProfile,
                    ReadBrightness = z.ReadBrightness,
                    Effects = z.Effects.Select((e, index) => new RgbEffectManifest
                    {
                        Name = e.Name, HasColor = e.HasColor, HasSpeed = e.HasSpeed, Handle = index,
                    }).ToList(),
                }).ToList(),
            };

        // The MUX is declared whenever the Acer backend was built (even unsupported), so the host's shared card can
        // state the refusal instead of the section vanishing (AcerDevice.cs:26-29). `supported:false` carries that.
        if (input.GpuMux)
            capabilities.GpuMux = new GpuMuxManifest
            {
                Supported = input.GpuMuxSupported,
                Modes = input.GpuMuxSupported
                    ? input.GpuMuxModes?.Select(m => new ChoiceOptionManifest { Id = m.Id, DisplayName = m.DisplayName }).ToList()
                    : null,
            };

        if (input.Settings is { Count: > 0 } settings)
            capabilities.DeclaredSettings = settings.Select(s => new DeclaredSettingManifest
            {
                Key = s.Key,
                Shape = s.Shape,
                Options = s.Options?.Select(o => new ChoiceOptionManifest { Id = o.Id, DisplayName = o.DisplayName }).ToList(),
                ReadbackVerifiesWrite = s.ReadbackVerifiesWrite,
            }).ToList();

        var manifest = new PluginManifest
        {
            Abi = input.Abi,
            PluginId = input.PluginId,
            Vendor = input.Vendor,
            VendorName = input.ModelName,
            StatusMessage = input.StatusMessage,
            Capabilities = capabilities,
        };

        return JsonSerializer.SerializeToUtf8Bytes(manifest, PluginJsonContext.Default.PluginManifest);
    }

    private static ProfileTraitsManifest Trait(PerformanceProfile profile)
    {
        var traits = AcerProfiles.TraitsOf(profile);
        return new ProfileTraitsManifest
        {
            Id = profile.Id,
            Kind = traits.Kind.ToString(),
            Accent = Color(traits.Accent),
            Flash = Color(traits.FlashColor),
        };
    }

    private static int[]? Color(AccentColor? c) => c is { } v ? [v.R, v.G, v.B] : null;
}
