namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// WHY A LOAD ENDED THE WAY IT DID (docs/vendor-plugins.md §4.1, §4.1.1, §3.8.4). The loader never throws on a
/// bad plugin — it discards the candidate and continues (§4.3 "never a crash") — so the caller needs a
/// machine-readable reason to decide between the two documented fallbacks:
///
///   * <see cref="NoMatch"/> and <see cref="NoCandidates"/> are the ordinary unknown-vendor case: fall back to
///     <c>GenericDevice</c> SILENTLY (the current <c>DeviceFactory.cs:35</c> behaviour, §4.1.1).
///   * <see cref="UnsupportedAbi"/>, <see cref="AbiMismatch"/> and <see cref="AmbiguousMatch"/> (and, by
///     extension, a corrupt <see cref="BindingFailed"/> candidate) are "matched but not usable" — a broken
///     install the user paid for — so §4.1.1 requires <c>GenericDevice</c> PLUS the
///     <c>status.plugin_incompatible</c> status line rather than a silent fallback. The loader returns the reason
///     so the caller can choose; it does not set the status line itself (that is wiring, deliberately absent in
///     Phase 0, §6).
///
/// Exhaustive enum, not strings, so a caller switches and the compiler flags a new reason that has no branch.
/// </summary>
internal enum PluginLoadReason
{
    /// <summary>A session was created and the manifest decoded. <see cref="PluginLoadResult.Session"/> is
    /// non-null.</summary>
    Loaded = 0,

    /// <summary>The candidate list was empty — no plugin dir, no cached plugin (§4.1.1 silent fallback).</summary>
    NoCandidates = 1,

    /// <summary>The injected binding factory returned null for a candidate — the module was absent, not a native
    /// image, or lacked an export (<c>NativePluginBinding.TryLoad</c> maps every such case to null). A skip, not a
    /// crash.</summary>
    BindingFailed = 2,

    /// <summary>§3.8.4 gate 2: the plugin's <c>ah_abi_version().major</c> is not in the registry's
    /// <c>SupportedMajors</c> (a removed or newer major). This is the "matched but incompatible" status-line case
    /// (§4.1.1).</summary>
    UnsupportedAbi = 3,

    /// <summary>Every candidate was evaluated but none was both <c>match:true</c> and at or above the confidence
    /// threshold. The ordinary "not this machine" answer (§3.2); silent fallback.</summary>
    NoMatch = 4,

    /// <summary>Two or more candidates tied at the same highest confidence above the threshold. A tie is refused
    /// rather than broken by filesystem order (§3.2): two plugins claiming one line is a bug the host surfaces
    /// instead of hiding. Silent fallback plus, ideally, a status line.</summary>
    AmbiguousMatch = 5,

    /// <summary><c>ah_create</c> returned a non-<c>Ok</c> status other than <c>-2</c> (the probe refused to build
    /// a session). Discarded with the status carried in <see cref="PluginLoadResult.Detail"/>.</summary>
    CreateRefused = 6,

    /// <summary>§3.8.4 gate 3: <c>ah_create</c> returned <c>-2 AbiMismatch</c> — the plugin needs a newer API
    /// than the host sent, or cannot adapt the declared capability/API version. The status-line case.</summary>
    AbiMismatch = 7,

    /// <summary><c>ah_create</c> succeeded but the winning adapter could not decode the returned manifest bytes
    /// (malformed JSON, or the literal <c>null</c>). The plugin is broken at its own boundary; discarded rather
    /// than surfaced as a null manifest.</summary>
    ManifestInvalid = 8,
}

/// <summary>
/// The loader's verdict (§4.1): either a live <see cref="IPluginSession"/> with the model-line id and confidence
/// that won, or a null session plus a <see cref="PluginLoadReason"/>. A class rather than a record struct because
/// the successful case owns a disposable session and must not be copied by value.
///
/// <see cref="Reason"/> is <see cref="PluginLoadReason.Loaded"/> exactly when <see cref="Session"/> is non-null,
/// so the invariant "loaded ⇔ session exists" holds by construction through the factories. <see cref="Detail"/>
/// is an optional human-readable note (the <c>ah_matches</c> reason, the create status, the exception) for a log
/// or a status line — never parsed for a decision.
/// </summary>
internal sealed class PluginLoadResult
{
    private PluginLoadResult(IPluginSession? session, PluginLoadReason reason, string? pluginId,
                             int confidence, string? detail)
    {
        Session = session;
        Reason = reason;
        PluginId = pluginId;
        Confidence = confidence;
        Detail = detail;
    }

    /// <summary>The live session, or null when no plugin was loaded. The caller owns it and must dispose it
    /// (which calls <c>ah_dispose</c>, §4.3).</summary>
    public IPluginSession? Session { get; }

    /// <summary>Why the load ended this way.</summary>
    public PluginLoadReason Reason { get; }

    /// <summary>The winning module's <c>ah_plugin_id</c> for diagnostics/telemetry (§3.2/§1.4), or null when no
    /// winner. This is what the plugin CLAIMS to be; the <c>ah_matches</c> decision is what made it win.</summary>
    public string? PluginId { get; }

    /// <summary>The winning <c>ah_matches</c> confidence (0..100), or 0 when nothing won.</summary>
    public int Confidence { get; }

    /// <summary>Optional human-readable detail — never used for control flow.</summary>
    public string? Detail { get; }

    /// <summary>Convenience: a session exists.</summary>
    public bool IsLoaded => Session is not null;

    /// <summary>A successful load. Only the loader builds these, so the loaded⇔session invariant cannot be
    /// broken from outside.</summary>
    public static PluginLoadResult Loaded(IPluginSession session, string? pluginId, int confidence) =>
        new(session, PluginLoadReason.Loaded, pluginId, confidence, detail: null);

    /// <summary>A refused load. <see cref="PluginLoadResult.PluginId"/>/<see cref="PluginLoadResult.Confidence"/>
    /// may still be set when the refusal happened after a match (the "matched but incompatible" case, §4.1.1) so
    /// the caller can log which plugin was refused.</summary>
    public static PluginLoadResult Failed(PluginLoadReason reason, string? detail = null,
                                          string? pluginId = null, int confidence = 0) =>
        new(session: null, reason, pluginId, confidence, detail);
}
