namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// THE MANAGED FACADE OVER A LOADED PLUGIN'S RAW C EXPORTS (docs/vendor-plugins.md §3.2, §4.1). It is the one
/// seam between "a Native AOT shared library mapped into this process" and the rest of the loader/session, and
/// its whole reason to exist is TESTABILITY: no method here takes or returns a pointer, so
/// <see cref="VendorPluginLoader"/>, <see cref="PluginSession"/> and every test can drive the full load/select/
/// invoke path with an ordinary fake — the same choice T3 made by keeping JSON, not pointers, at the adapter
/// boundary (<c>IPluginAbiAdapter</c>'s own note) and the same choice <c>IPluginSession</c> makes over the
/// session (see its note, <c>IPluginSession.cs:7-12</c>).
///
/// WHY A SEPARATE INTERFACE FROM <c>IPluginSession</c>. The session is the host's INTERNAL call model (JSON in,
/// <see cref="PluginCallResult"/> out). The binding is one level lower: it still carries the raw ABI shapes —
/// the encoded <c>ah_abi_version</c> int, the model-line id, the raw UTF-8 manifest bytes, the opaque session
/// handle — because the loader needs those BEFORE a session exists. Collapsing the two would force the loader to
/// know about an adapter it has not chosen yet (§3.8.4 gate 2 chooses the adapter from the version).
///
/// ALL UNSAFE POINTER CODE LIVES IN EXACTLY ONE IMPLEMENTATION, <see cref="NativePluginBinding"/>. That is the
/// load-bearing constraint of the whole task: local Native AOT cannot be built here (no MSVC/clang), so the
/// loader and session MUST be provable without a real plugin, and the unprovable part must be as small as
/// possible. This contract is that "as small as possible": seven managed methods, each documented against the C
/// export it wraps.
///
/// THE BINDING OWNS BUFFER LIFETIME, NOT THE CALLER. <c>ah_invoke</c>/<c>ah_create</c> return buffers the plugin
/// allocated and the HOST must release with <c>ah_free</c> (§3.2). Doing that inside the binding — never
/// surfacing a pointer — is what keeps <c>ah_free</c> a single call site and makes "exactly once" assertable,
/// matching the §4.3 lifetime rule and the Phase 0 verification item ("a test that <c>ah_free</c>/<c>ah_dispose</c>
/// are called exactly once", §6).
/// </summary>
internal interface INativePluginBinding : IDisposable
{
    /// <summary>Wrap <c>uint32 ah_abi_version(void)</c> (§3.2) — the API version the plugin was BUILT for,
    /// <c>(major &lt;&lt; 16) | minor</c>. Returns false only when the call itself failed (a managed exception
    /// was caught); a plugin always answers, so a false here is a broken module, not a "no". The loader feeds the
    /// value to <c>AbiCompatibility.DecideFromEncoded</c> for §3.8.4 gate 2.</summary>
    bool TryGetAbiVersion(out uint encodedVersion);

    /// <summary>Wrap <c>int32 ah_plugin_id(uint8* outBuf, int32 cap)</c> (§3.2) — the MODEL-LINE id
    /// ("acer-nitro"), used for diagnostics/telemetry only: it names what the plugin CLAIMS to be, while
    /// <see cref="Matches"/> is the gate. Null when the export failed or returned nothing usable.</summary>
    string? PluginId();

    /// <summary>Wrap <c>int32 ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap,
    /// int32* outLen)</c> (§3.2) — the cheap DMI gate. The decision JSON
    /// <c>{"match":bool,"confidence":0..100,"reason":"..."}</c> is decoded here into a managed record so no
    /// caller parses it; a failed call or malformed decision becomes <c>Matched=false</c> rather than a throw
    /// (the loader treats a throw as a discard, §4.3 "never a crash").</summary>
    PluginMatchResult Matches(string descriptorJson);

    /// <summary>Wrap <c>int32 ah_create(const uint8* descJson, int32 descLen, uint8** outManifest,
    /// int32* outManifestLen, uint64* outHandle)</c> (§3.2) — probe the hardware and build a session. The
    /// manifest is returned as RAW UTF-8 BYTES, deliberately: decoding is the winning ADAPTER's job
    /// (<c>IPluginAbiAdapter.DecodeManifest</c>) because the wire shape is major-specific (§3.8.2), and this
    /// layer must not know a major. Also the §3.8.4 gate-3 handshake: a status of <c>-2 AbiMismatch</c> is the
    /// plugin refusing the host's API version. The binding remembers a successful handle so <see cref="Dispose"/>
    /// can call <c>ah_dispose</c>.</summary>
    PluginCreateResult Create(string descriptorJson);

    /// <summary>Wrap <c>int32 ah_invoke(uint64 handle, uint32 capability, uint32 op, const uint8* request,
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> (§3.2). The response buffer is FREED
    /// WITH <c>ah_free</c> INSIDE this method, so the returned body is a managed string and the caller cannot
    /// leak or double-free; a failed call returns <c>AbiStatus.Internal</c> with a null body rather than
    /// throwing.</summary>
    PluginInvokeResult Invoke(ulong handle, uint capability, uint op, string requestJson);

    /// <summary>An optional diagnostic hook: the last exception a wrapped export body caught. The ABI forbids a
    /// managed exception from crossing the boundary (§3.2 — "no exception crosses"), so a failure is returned as
    /// a status/no-result and the exception is parked here for a log or a test to read. Null until a call fails.
    /// It is deliberately NOT part of any decision path — the loader branches on statuses, never on this.</summary>
    Exception? LastError { get; }
}

/// <summary>The decoded <c>ah_matches</c> decision (§3.2). A record struct because it is a small, transient,
/// identity-less result — the same shape the tree uses for <see cref="PluginCallResult"/> and
/// <c>GpuMuxChange</c>. <see cref="Confidence"/> is the host's selection key (§3.2): the loader takes the unique
/// maximum at or above its threshold and refuses to pick on a tie.</summary>
internal readonly record struct PluginMatchResult(bool Matched, int Confidence, string? Reason);

/// <summary>The result of <c>ah_create</c> (§3.2). <see cref="Status"/> is the raw ABI status (0 Ok,
/// -2 AbiMismatch, …); <see cref="ManifestUtf8"/> is the RAW manifest bytes the winning adapter decodes (empty
/// when the create was refused); <see cref="Handle"/> is the opaque session handle <c>ah_invoke</c> needs, or 0
/// when no session was created. The array (rather than a span) because the result outlives the call that
/// produced it and is handed to <see cref="PluginSession"/>.</summary>
internal readonly record struct PluginCreateResult(int Status, byte[] ManifestUtf8, ulong Handle);

/// <summary>The result of one <c>ah_invoke</c>, with the plugin's response buffer already released (§3.2).
/// <see cref="Status"/> is the raw ABI status and <see cref="Body"/> the decoded UTF-8 body, or null when the
/// plugin returned none. It mirrors <see cref="PluginCallResult"/> on purpose — the session maps one onto the
/// other — but is a distinct type because it is produced by the raw layer and carries no adapter translation
/// yet.</summary>
internal readonly record struct PluginInvokeResult(int Status, string? Body);
