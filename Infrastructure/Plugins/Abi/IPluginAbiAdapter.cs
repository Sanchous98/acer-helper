namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// ONE SUPPORTED API MAJOR, seen from the host (docs/vendor-plugins.md §3.8.2). An adapter is the ONLY place
/// allowed to know how one major's wire form differs from the host's internal call model, and it is what lets the
/// host hold SEVERAL majors at once (current + deprecated) without a single major-conditional branch anywhere
/// outside <c>Abi/</c>. §3.8.2 states the rule directly: "There is no major-conditional code outside <c>Abi/</c>."
///
/// THE INTERNAL CALL MODEL IS THE CURRENT MAJOR'S MODEL. That is the pivot the whole interface is shaped around:
/// §3.8.2 says "the internal model is the current major's model", so the adapter for <c>PluginApi.Major</c> is a
/// pass-through (the identity) while a DEPRECATED adapter translates ITS major's wire form onto the current one.
/// When the host bumps its major, the new current adapter becomes the identity and the old adapter becomes a
/// translator; the internal model does not move, and no OTHER adapter is touched. That is exactly the property
/// that makes "adding a major must not change the internal model — it adds an adapter" (§3.8.2) true rather than
/// aspirational.
///
/// WHY JSON STRINGS/BYTES AND NOT TYPED DTOs OR A GENERIC <c>T</c>. The internal call model over a session is
/// already JSON: <see cref="IPluginSession.Invoke"/> takes a request body string and returns
/// <see cref="PluginCallResult"/> whose body is a string, and the §3.3 payload decision is JSON via the generated
/// <c>PluginJsonContext</c>. Expressing the adapter's boundary in the SAME currency — UTF-8 bytes for the
/// manifest, strings for requests and responses — means:
///   * <b>Add a major = add an adapter with no compile-time fan-out.</b> A translator only has to know the OLD
///     major's wire DTOs plus the current major's JSON shape; it cannot reach into the internal model's types and
///     so cannot force an edit there. A generic <c>DecodeResponse&lt;T&gt;</c> would instead make every adapter
///     name every response DTO the internal model will ever gain, so a new capability would edit them all — the
///     opposite of the rule.
///   * <b>AOT-safe by construction.</b> Each adapter parses with its own source-generated context (§3.3); the
///     interface carries no <c>RequiresDynamicCode</c> surface and no reflection (§2.2).
///   * <b>Testable without a plugin.</b> A fake adapter and a JSON fixture exercise every path, exactly as the
///     in-tree fake ports stand in for hardware (<see cref="IPluginSession"/>'s own note).
/// A future adapter whose major's op numbers or field names changed implements <see cref="EncodeRequest"/> by
/// mapping the host's (capability, op) pair and internal body onto its wire form, and <see cref="DecodeResponse"/>
/// by mapping the wire body back — both ordinary code, both confined to that adapter's file.
///
/// WHAT THIS INTERFACE DOES <b>NOT</b> DO, deliberately: it does not read raw pointers, load the library, or know
/// the <c>delegate* unmanaged[Cdecl]</c> exports. That is the binding/session's job (T4, <see cref="IPluginSession"/>
/// and <c>VendorAbiExports</c>); the adapter works on the decoded payload only. Keeping the pointer marshalling
/// out of here is what keeps an adapter unit-testable with a string.
/// </summary>
internal interface IPluginAbiAdapter
{
    /// <summary>The API major this adapter speaks, as declared by a plugin's <c>ah_abi_version()</c> high half
    /// (§3.2). It is the registry's lookup key (<see cref="PluginAbiRegistry.ForMajor"/>) and the value compared
    /// against the plugin's own major at §3.8.4 gate 2. Never 0: major 0 is not a shipped API.</summary>
    int Major { get; }

    /// <summary>True when this adapter is kept ONLY so an already-downloaded plugin built for a previous major
    /// keeps loading after the host bumped (§3.8.3). A deprecated adapter is still fully functional; the flag is
    /// what lets the caller log/telemeter "running a deprecated plugin" and what the registry uses to enforce the
    /// §3.8.3 lifecycle. The CURRENT major's adapter is always <c>false</c>.</summary>
    bool Deprecated { get; }

    /// <summary>Decode a plugin's probe manifest (§3.4) — the UTF-8 bytes <c>ah_create</c> returned — into the
    /// internal <see cref="PluginManifest"/>. For the current major this is the baseline decode through
    /// <c>PluginJsonContext</c>; a deprecated adapter translates ITS manifest shape (renamed members, a moved
    /// capability, a different enum spelling) into the current model, which is why the return type is the internal
    /// model and not something major-specific. Unknown JSON members are ignored (§3.8.5), so a plugin built for a
    /// NEWER minor of a supported major still decodes.</summary>
    PluginManifest DecodeManifest(ReadOnlySpan<byte> utf8Json);

    /// <summary>Encode an internal-model request body into THIS major's wire request JSON (§3.5). The
    /// (capability, op) pair is passed because a future major may renumber either; the current major's adapter
    /// returns <paramref name="internalRequestJson"/> unchanged because the internal model IS its wire form. The
    /// empty object <c>{}</c> is the conventional no-payload request, matching <see cref="IPluginSession.Invoke"/>.
    /// </summary>
    string EncodeRequest(uint capability, uint op, string internalRequestJson);

    /// <summary>Decode THIS major's wire response body into the internal-model response body (§3.5), the inverse
    /// of <see cref="EncodeRequest"/>. Nullable because a plugin may return no body
    /// (<see cref="PluginCallResult.Body"/> is null in that case); the current major's adapter returns the body
    /// unchanged. Only the BODY is adapted here — the ABI status travelling beside it is already major-neutral
    /// (<c>AbiStatus</c>) and is not the adapter's concern.
    /// </summary>
    string? DecodeResponse(uint capability, uint op, string? wireResponseJson);
}
