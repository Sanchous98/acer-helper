using System.Text.Json;

namespace AcerHelper.Infrastructure.Plugins.Abi.V1;

/// <summary>
/// The adapter for API major 1 — the FIRST shipped plugin API, and therefore the CURRENT major's adapter today
/// (docs/vendor-plugins.md §3.8.2, "Status today": "the first shipped API is 1.0, so the host initially carries a
/// single adapter (<c>V1/</c>, current)").
///
/// IT IS THE IDENTITY BECAUSE V1 <b>IS</b> THE INTERNAL MODEL. §3.8.2 fixes the internal call model to be the
/// current major's model, so while the host runs major 1 there is nothing to translate: the manifest decode is the
/// baseline <c>PluginJsonContext</c> decode and request/response pass through untouched. That is not a shortcut —
/// writing a translation here would invent a difference the ABI does not have, and the tests in
/// <c>PluginAbiVersioningTests</c> pin the pass-through so a future edit cannot quietly start mangling payloads.
///
/// WHY THE FOLDER IS NAMED BY MAJOR AND NOT BY ROLE. §3.8.2 is explicit that the folder stays <c>V1/</c> when V1 is
/// removed and <c>V2/</c> stays <c>V2/</c>: the current-major adapter is whatever <see cref="PluginAbiRegistry"/>
/// says it is, so the files never need renaming — only the registry's default factory is repointed. A future
/// <c>V2/AbiV2Adapter</c> would be the identity once major 2 ships, and THIS file would become the deprecated
/// translator that maps V1's struct/field/op layout onto whatever the internal model has become by then. Doing
/// that here — and nowhere else — is precisely the "add a major = add an adapter" rule (§3.8.2).
///
/// IT TOUCHES NO POINTERS. The binding to <c>ah_create</c>/<c>ah_invoke</c> and the buffer lifetime are T4's
/// (<c>VendorAbiExports</c>, <c>IPluginSession</c>); this class only ever sees decoded UTF-8/JSON.
/// </summary>
internal sealed class AbiV1Adapter : IPluginAbiAdapter
{
    /// <summary>Major 1 — the first and, at the time of writing, only shipped plugin API (§3.8.2).</summary>
    public int Major => 1;

    /// <summary>False: V1 is the current major, not a deprecated one. When the host bumps to 2 this flips to
    /// <c>true</c> as part of the same change that repoints <see cref="PluginAbiRegistry.CreateDefault"/>
    /// (§3.8.3).</summary>
    public bool Deprecated => false;

    /// <summary>The baseline manifest decode — the internal model's own shape (§3.4). Source-generated and
    /// AOT-safe (§3.3): the generated context emits the <c>JsonTypeInfo</c>, so no runtime reflection is used and
    /// the tolerant reader options (camelCase, comments, trailing commas, unknown-member tolerance, §3.8.5) are the
    /// context's, exactly as <c>PluginAbiFoundationTests</c> exercises for the raw context. A null result (the
    /// bytes are the JSON literal <c>null</c>) is treated as malformed rather than surfaced as a null manifest: the
    /// caller asked to decode a manifest and a plugin that returns <c>null</c> has none.</summary>
    public PluginManifest DecodeManifest(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, PluginJsonContext.Default.PluginManifest)
        ?? throw new JsonException("Plugin manifest decoded to null (the body was the JSON literal 'null').");

    /// <summary>Identity: the internal request body IS V1's wire request body (the empty object <c>{}</c> when the
    /// op takes none, §3.5). (capability, op) are ignored only because V1 needs no remapping; a future major's
    /// adapter is free to use them.</summary>
    public string EncodeRequest(uint capability, uint op, string internalRequestJson) => internalRequestJson;

    /// <summary>Identity: the internal response body IS V1's wire response body. Null passes through because a
    /// plugin may return no body (<see cref="PluginCallResult.Body"/>). The ABI status beside it is not the
    /// adapter's concern — it is already major-neutral (<c>AbiStatus</c>).</summary>
    public string? DecodeResponse(uint capability, uint op, string? wireResponseJson) => wireResponseJson;
}
