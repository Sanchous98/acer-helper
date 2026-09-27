namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// The OUTCOME CATEGORY of the §3.8.4 gate-2 decision: accepted via the current adapter, accepted via the
/// deprecated one, or refused because no adapter exists for the major. A small enum rather than a bool pair so a
/// caller can switch exhaustively and the compiler flags a new category.
/// </summary>
internal enum AbiCompatibilityReason
{
    /// <summary>The plugin's major is the host's current major — the ordinary, fully-supported case.</summary>
    SupportedCurrent = 0,

    /// <summary>The plugin's major is supported only by the deprecated adapter (§3.8.3). It LOADS, but the caller
    /// should log/telemeter that the user is running a deprecated plugin.</summary>
    SupportedDeprecated = 1,

    /// <summary>No adapter exists for the plugin's major — either a REMOVED major or one NEWER than the host
    /// (§3.8.4 gate 2). The session is refused.</summary>
    UnsupportedMajor = 2,
}

/// <summary>
/// The compatibility verdict for one plugin (§3.8.4 gate 2). A record struct because it is a small, transient,
/// identity-less result — the same shape the tree uses for <see cref="PluginCallResult"/> and
/// <c>GpuMuxChange</c>. <see cref="Reason"/> is the machine-readable category (the caller may build the
/// <c>status.plugin_incompatible</c> line and a log message from it); <see cref="IsDeprecated"/> is broken out as a
/// bool because that is the one fact a caller needs WITHOUT switching, to decide whether to surface a "deprecated
/// plugin" notice.
/// </summary>
internal readonly record struct AbiCompatibilityResult(bool IsCompatible, bool IsDeprecated, AbiCompatibilityReason Reason)
{
    /// <summary>Convenience inverse of <see cref="IsCompatible"/>, for the gate's own reading
    /// ("if incompatible, fall back + status line").</summary>
    public bool IsIncompatible => !IsCompatible;
}

/// <summary>
/// The §3.8.4 GATE-2 DECISION as a pure, testable function: given the host's adapter <see cref="PluginAbiRegistry"/>
/// and a plugin's declared API major, is this plugin loadable, and if so is it the deprecated path?
///
/// WHY IT LIVES BESIDE THE REGISTRY. The rule is exactly "the plugin's major is in <c>SupportedMajors</c>", and
/// <c>SupportedMajors</c> is a registry fact; expressing the decision once here (rather than at every caller)
/// keeps the refusal reason and the deprecation notice identical for the manifest gate's status line and the load
/// gate's, so the two cannot disagree about the same plugin (§3.8.4 gates 1 and 2).
///
/// THE MAJOR IS THE GATE; THE MINOR IS INFORMATIONAL (§3.8.5). This decision takes only a major: a plugin built
/// for a NEWER minor of a supported major is ACCEPTED (additive evolution; the decoder ignores unknown JSON
/// members), and a plugin built for an OLDER minor is accepted too. The host never refuses on minor, which is why
/// no method here accepts one — a signature that cannot express the wrong rule is better than one that documents
/// it. <see cref="MajorOf"/> and <see cref="MinorOf"/> split the <c>(major &lt;&lt; 16) | minor</c> encoding
/// (§3.2/§3.8.1) when a caller has the raw <c>ah_abi_version</c> int and needs to hand the major here.
/// </summary>
internal static class AbiCompatibility
{
    /// <summary>Decide whether a plugin declaring <paramref name="pluginMajor"/> can be driven by
    /// <paramref name="registry"/>. A null registry is a programming error and throws; the decision itself never
    /// throws — an unsupported major is a returned <see cref="AbiCompatibilityReason.UnsupportedMajor"/>, because
    /// it is an expected answer the caller branches on (§3.8.4 gate 2).</summary>
    public static AbiCompatibilityResult Decide(PluginAbiRegistry registry, int pluginMajor)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var adapter = registry.ForMajor(pluginMajor);
        if (adapter is null)
            return new AbiCompatibilityResult(IsCompatible: false, IsDeprecated: false,
                                              AbiCompatibilityReason.UnsupportedMajor);

        return new AbiCompatibilityResult(IsCompatible: true, IsDeprecated: adapter.Deprecated,
                                    adapter.Deprecated
                                        ? AbiCompatibilityReason.SupportedDeprecated
                                        : AbiCompatibilityReason.SupportedCurrent);
    }

    /// <summary>Decide from the raw <c>ah_abi_version()</c> value <c>(major &lt;&lt; 16) | minor</c> (§3.2) — the
    /// form the loader actually reads from the export. The minor is not passed on (see the type's note): the
    /// decision is a function of the major alone.</summary>
    public static AbiCompatibilityResult DecideFromEncoded(PluginAbiRegistry registry, int encodedAbiVersion) =>
        Decide(registry, MajorOf(encodedAbiVersion));

    /// <summary>The high 16 bits of <c>(major &lt;&lt; 16) | minor</c> — the major (§3.2). Masked and shifted so a
    /// malformed/negative value cannot bleed into the major.</summary>
    public static int MajorOf(int encodedAbiVersion) => (encodedAbiVersion >> 16) & 0xFFFF;

    /// <summary>The low 16 bits of <c>(major &lt;&lt; 16) | minor</c> — the minor (§3.2). Informational only
    /// (§3.8.5); exposed so a caller can log "built for 1.3" without re-deriving the shift.</summary>
    public static int MinorOf(int encodedAbiVersion) => encodedAbiVersion & 0xFFFF;
}
