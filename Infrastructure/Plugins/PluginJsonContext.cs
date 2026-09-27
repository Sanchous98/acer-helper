using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// The AOT-safe serializer for the probe manifest (docs/vendor-plugins.md §3.3, §3.4), and the in-tree
/// precedent to copy is <c>AcerModelJsonContext</c> (plugins/AcerHelper.Vendor.acer-nitro/Acer/AcerModel.cs:34-37): a
/// <c>[JsonSourceGenerationOptions]</c>-annotated <c>partial class : JsonSerializerContext</c> with one
/// <c>[JsonSerializable]</c> per wire type. Source generation is the ONLY serialization path used here —
/// <c>JsonSerializer.Serialize(object)</c> is <c>RequiresDynamicCode</c> and is not AOT-safe (§2.2), whereas
/// these overloads take a <c>JsonTypeInfo</c> the generator emitted at compile time.
///
/// <c>CamelCase</c> matches the §3.4 JSON exactly while the DTOs stay ordinary PascalCase C# (<c>PluginId</c>
/// serialises as <c>pluginId</c>, <c>AvailableOnAc</c> as <c>availableOnAc</c>) — the same "wire spelling is a
/// serializer policy, not a property name" choice the tree already makes for <c>acer-models.json</c>. The
/// tolerant reader options mirror that context too and let the §3.4 sample's <c>//</c> comments and trailing
/// commas be pasted into a fixture verbatim.
///
/// The manifest is the one type the FOUNDATION needs; the §3.5 request/response DTOs are added by the adapter
/// task (T5) in this same context, so both directions of a capability's wire form are generated with the build
/// rather than reflected at runtime.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginJsonContext : JsonSerializerContext;
