using System.Text.Json.Nodes;
using AcerHelper.Infrastructure.Plugins.Abi;
using AcerHelper.Infrastructure.Plugins.Distribution;

namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// THE LOADER (docs/vendor-plugins.md §4.1) — given candidate plugin files, it loads each behind an injected
/// binding factory, applies the version gate, scores the machine match, picks ONE winner and creates a session.
///
/// IT NEVER THROWS ON A BAD PLUGIN. A missing module, an unsupported API major, a below-threshold match, a tie,
/// a refusal from <c>ah_create</c> — each is a discard + a <see cref="PluginLoadReason"/>, because a plugin the
/// user downloaded must degrade to the in-host <c>GenericDevice</c> rather than crash the process (§4.3 "Never a
/// crash"). The caller decides the two documented fallbacks from the reason (silent vs
/// <c>status.plugin_incompatible</c>, §4.1.1).
///
/// WHY THE BINDING FACTORY IS INJECTED. Production passes <c>NativePluginBinding.TryLoad</c>, which needs a real
/// Native AOT module and cannot run here (no MSVC/clang). Tests pass a fake that hands back a
/// <see cref="FakeNativePluginBinding"/> per path, so EVERY rule below — the major gate, the threshold, the tie
/// refusal, the create-status mapping — is exercised without a `.dll`. The loader itself never touches
/// <c>NativeLibrary</c> or a pointer; that is the whole reason <see cref="INativePluginBinding"/> exists.
///
/// THE CANDIDATE LIST IS THE CALLER'S, AND THE PRE-FILTER IS THE CALLER'S TOO. §3.2 requires a cheap host-side
/// <c>matchHint</c> pass before anything is loaded, because a loaded AOT module cannot be unloaded and each one
/// is a second runtime (§2.3, §7.1 risk 1). That pre-filter lives in <c>PluginCatalogSelector</c> (T2) and is
/// NOT repeated here: the loader accepts an already-filtered list and does not read the filesystem. Enumerating
/// the §4.1.1 search paths is a separate, pure helper (<see cref="EnumerateSearchPaths"/>) the caller composes
/// with the selector.
///
/// DETERMINISM AND THE SELECTION RULE (§3.2, implemented below):
///   1. a candidate counts only if <c>ah_matches</c> says <c>match:true</c> AND its confidence is AT OR ABOVE the
///      threshold;
///   2. the winner is the candidate with the UNIQUE MAXIMUM confidence;
///   3. a TIE at the maximum is REFUSED (<see cref="PluginLoadReason.AmbiguousMatch"/>) rather than broken by
///      filesystem order — "two plugins claiming the same line is a bug the host surfaces instead of hiding"
///      (§3.2);
///   4. nothing above the threshold is <see cref="PluginLoadReason.NoMatch"/>.
/// The candidate list is walked in its given order and the maximum is computed with a strict <c>&gt;</c>, so the
/// first-seen candidate survives a tie only to be counted — the tie itself still refuses. The result therefore
/// depends on the SET of candidates, not on their order, except that the reported tie winner's id is the first
/// one seen (a diagnostic only).
///
/// THE ADAPTER IS CHOSEN FROM <c>ah_abi_version</c>, NOT FROM THE MANIFEST. §3.8.4 gate 2 is authoritative: a
/// plugin whose declared major is not in <see cref="PluginAbiRegistry.SupportedMajors"/> is discarded before its
/// manifest is ever consulted, and the winning adapter decodes the manifest the plugin returns (§3.8.2). The
/// manifest-gate (gate 1) and the <c>minHost</c> check are the CALLER's, already applied by
/// <c>PluginCatalogSelector</c>.
/// </summary>
internal sealed class VendorPluginLoader
{
    private readonly IReadOnlyList<string> _candidatePaths;
    private readonly Func<string, INativePluginBinding?> _bindingFactory;
    private readonly PluginAbiRegistry _registry;
    private readonly MachineDescriptor _machine;
    private readonly string _hostAppVersion;
    private readonly int _confidenceThreshold;

    /// <summary>
    /// Build a loader. All dependencies are explicit so a test constructs one with a fake factory and a synthetic
    /// registry and drives the whole path.
    /// </summary>
    /// <param name="candidatePaths">The plugin files to consider, ALREADY pre-filtered by the caller's
    /// <c>matchHint</c> pass and already in a deterministic order (§4.1.1). Empty means no candidates.</param>
    /// <param name="bindingFactory">Injects the binding for one path — production <c>NativePluginBinding.TryLoad</c>,
    /// tests a fake. Returning null means "this path is not a usable module" and is a skip.</param>
    /// <param name="registry">The per-major adapters (T3); its <c>SupportedMajors</c> is §3.8.4 gate 2.</param>
    /// <param name="machine">The four-field DMI descriptor (§3.2) fed to every <c>ah_matches</c> and
    /// <c>ah_create</c>.</param>
    /// <param name="hostAppVersion">The host's app <c>&lt;Version&gt;</c> (§3.8.4). It is carried for the
    /// caller's gate-1 status line / future <c>minHost</c> cross-check; it is NOT used for selection, because the
    /// <c>minHost</c> check is gate 1 and belongs to <c>PluginCatalogSelector</c>. Validated non-null so a caller
    /// cannot smuggle in "no version".</param>
    /// <param name="confidenceThreshold">The minimum <c>ah_matches</c> confidence (0..100) that counts. The
    /// calibration is §3.2's: 50 manufacturer-only, 80 product-substring, 100 product+board. The exact value is
    /// the caller's policy; the rule is "at or above", so a candidate AT the threshold counts.</param>
    public VendorPluginLoader(
        IReadOnlyList<string> candidatePaths,
        Func<string, INativePluginBinding?> bindingFactory,
        PluginAbiRegistry registry,
        MachineDescriptor machine,
        string hostAppVersion,
        int confidenceThreshold)
    {
        _candidatePaths = candidatePaths ?? throw new ArgumentNullException(nameof(candidatePaths));
        _bindingFactory = bindingFactory ?? throw new ArgumentNullException(nameof(bindingFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _hostAppVersion = hostAppVersion ?? throw new ArgumentNullException(nameof(hostAppVersion));
        _confidenceThreshold = confidenceThreshold;
    }

    /// <summary>Run the scan and return the verdict (§4.1). Never throws for a plugin-shaped failure; an
    /// exception from the injected factory itself (a test bug, or a truly unexpected host fault) is caught per
    /// candidate and treated as a skip so one bad path cannot abort the scan of the others (§4.3).</summary>
    public PluginLoadResult Load()
    {
        if (_candidatePaths.Count == 0)
            return PluginLoadResult.Failed(PluginLoadReason.NoCandidates);

        var descriptorJson = BuildDescriptorJson(_machine);
        var createJson = BuildCreateJson(_machine);

        // The evaluated candidates: only those that passed gate 2 AND matched at/above threshold.
        var matches = new List<Candidate>(_candidatePaths.Count);

        // Did any candidate at least get far enough to declare an unsupported major? If nothing matches, that
        // distinguishes the "matched but incompatible" §4.1.1 status-line case from the ordinary no-match one.
        var sawUnsupportedAbi = false;
        string? unsupportedDetail = null;

        foreach (var path in _candidatePaths)
        {
            INativePluginBinding? binding;
            try { binding = _bindingFactory(path); }
            catch (Exception ex) { binding = null; _ = ex; } // a factory fault is a skip, never a crash
            if (binding is null) continue; // absent / not a native image / missing export — §4.1 skip

            try
            {
                if (!binding.TryGetAbiVersion(out var encoded))
                    continue; // the module cannot even answer its version: a broken module, not a candidate

                // §3.8.4 gate 2 — the authoritative version check. The major is the gate; the minor is ignored
                // (§3.8.5). A plugin at the deprecated major still loads through its adapter (§3.8.3).
                var compatibility = AbiCompatibility.DecideFromEncoded(_registry, unchecked((int)encoded));
                if (compatibility.IsIncompatible)
                {
                    sawUnsupportedAbi = true;
                    unsupportedDetail ??=
                        $"'{path}' declares API {AbiCompatibility.MajorOf(unchecked((int)encoded))}, "
                        + "not in SupportedMajors";
                    continue;
                }

                var adapter = _registry.ForMajor(AbiCompatibility.MajorOf(unchecked((int)encoded)));
                if (adapter is null) continue; // Defensive: Decide said compatible, so this cannot happen.

                var pluginId = binding.PluginId(); // diagnostics/telemetry only (§3.2)

                // §3.2's DMI gate. A decision only counts when it says match AND clears the threshold.
                var decision = binding.Matches(descriptorJson);
                if (!decision.Matched || decision.Confidence < _confidenceThreshold)
                    continue;

                matches.Add(new Candidate(path, binding, adapter, pluginId, decision.Confidence, decision.Reason));
            }
            catch (Exception)
            {
                // The ABI forbids a managed exception crossing the boundary (§3.2); if one still reached us, the
                // binding is unusable — discard it. The binding is disposed by the caller-owned list below if it
                // was added; otherwise dispose here.
                binding.Dispose();
            }
        }

        if (matches.Count == 0)
        {
            // No candidate won. The unsupported-major case is surfaced as itself so the caller can raise the
            // §4.1.1 status line; everything else is the ordinary no-match/silent fallback.
            return sawUnsupportedAbi
                ? PluginLoadResult.Failed(PluginLoadReason.UnsupportedAbi, unsupportedDetail)
                : PluginLoadResult.Failed(PluginLoadReason.NoMatch);
        }

        // Pick the unique maximum (strict '>'), counting how many share it. A tie above threshold refuses.
        var winner = matches[0];
        var tied = false;
        for (var i = 1; i < matches.Count; i++)
        {
            var c = matches[i];
            if (c.Confidence > winner.Confidence) { winner = c; tied = false; }
            else if (c.Confidence == winner.Confidence) { tied = true; }
        }

        if (tied)
        {
            foreach (var c in matches) c.Binding.Dispose();
            return PluginLoadResult.Failed(
                PluginLoadReason.AmbiguousMatch,
                $"multiple plugins tied at confidence {winner.Confidence}",
                pluginId: winner.PluginId, confidence: winner.Confidence);
        }

        // Dispose every non-winner (their modules stay mapped, but their managed handles and any vendor handles
        // are released, §4.3). The winner is disposed explicitly on every failure path below.
        foreach (var c in matches)
            if (!ReferenceEquals(c, winner)) c.Binding.Dispose();

        try
        {
            // §3.8.4 gate 3 — the handshake. ah_create receives the descriptor PLUS the host's PluginApiVersion
            // (§3.4), so a plugin needing a newer API can refuse with -2 AbiMismatch.
            var created = winner.Binding.Create(createJson);

            if (created.Status == AbiStatus.AbiMismatch)
            {
                winner.Binding.Dispose();
                return PluginLoadResult.Failed(PluginLoadReason.AbiMismatch,
                    "ah_create refused the handshake (-2)", winner.PluginId, winner.Confidence);
            }

            if (created.Status != AbiStatus.Ok)
            {
                winner.Binding.Dispose();
                return PluginLoadResult.Failed(PluginLoadReason.CreateRefused,
                    $"ah_create status {created.Status}", winner.PluginId, winner.Confidence);
            }

            PluginManifest manifest;
            try
            {
                // The manifest is decoded through the WINNING adapter (§3.8.2) — never here — so the major's wire
                // shape stays inside Abi/. A malformed manifest is the plugin's own boundary breaking.
                manifest = winner.Adapter.DecodeManifest(created.ManifestUtf8);
            }
            catch (Exception ex)
            {
                winner.Binding.Dispose();
                return PluginLoadResult.Failed(PluginLoadReason.ManifestInvalid, ex.Message,
                    winner.PluginId, winner.Confidence);
            }

            var session = new PluginSession(winner.Binding, created.Handle, winner.Adapter, manifest);
            return PluginLoadResult.Loaded(session, winner.PluginId, winner.Confidence);
        }
        catch (Exception ex)
        {
            // The binding's own wrappers already swallow native faults; this catch is the belt to their braces so
            // the loader's "never throws" contract holds even if a binding implementation misbehaves.
            winner.Binding.Dispose();
            return PluginLoadResult.Failed(PluginLoadReason.CreateRefused, ex.Message,
                winner.PluginId, winner.Confidence);
        }
    }

    /// <summary>One evaluated candidate: its binding (owned until disposed), the winning adapter for its major,
    /// its claimed id and its match decision.</summary>
    private sealed record Candidate(string Path, INativePluginBinding Binding, IPluginAbiAdapter Adapter,
                                    string? PluginId, int Confidence, string? MatchReason);

    /// <summary>The §3.2 four-field machine descriptor as JSON. Built with <see cref="JsonObject"/> rather than a
    /// source-generated DTO because it is a handful of scalars and adding a <c>[JsonSerializable]</c> type to
    /// <c>PluginJsonContext</c> would touch T1's file; <c>JsonObject.ToJsonString</c> is AOT-safe (a node tree,
    /// no reflection, §2.2). camelCase is spelled out literally so the wire names cannot drift from §3.2.
    /// All four fields are always emitted (null included): the plugin reads a null as "unknown", which is exactly
    /// the absent case.</summary>
    private static string BuildDescriptorJson(MachineDescriptor m) =>
        new JsonObject
        {
            ["manufacturer"] = m.Manufacturer,
            ["product"] = m.Product,
            ["board"] = m.Board,
            ["boardProduct"] = m.BoardProduct,
        }.ToJsonString();

    /// <summary>The <c>ah_create</c> request (§3.4): the descriptor PLUS the host's plugin API version, which is
    /// what makes §3.8.4 gate 3 possible. The <c>abi</c> block is emitted from the generated
    /// <see cref="PluginApi"/> consts (§3.8.1) so host and plugin cannot drift on the encoding.</summary>
    private static string BuildCreateJson(MachineDescriptor m) =>
        new JsonObject
        {
            ["manufacturer"] = m.Manufacturer,
            ["product"] = m.Product,
            ["board"] = m.Board,
            ["boardProduct"] = m.BoardProduct,
            ["abi"] = new JsonObject
            {
                ["major"] = PluginApi.Major,
                ["minor"] = PluginApi.Minor,
            },
        }.ToJsonString();

    /// <summary>
    /// The §4.1.1 search paths, in order, as a PURE function of the environment it is given (so a test can pin
    /// it without touching the real machine): the dev override directory first, then the per-user cache. The
    /// caller pairs each directory with its candidate files (and the T2 <c>matchHint</c> pre-filter).
    ///
    /// This is deliberately SEPARATE from <see cref="Load"/> and takes its inputs as arguments rather than
    /// reading <c>Environment</c> itself: enumeration is the only part that would touch the filesystem, and
    /// keeping it out of the load path is what lets the load path stay a pure function of the candidate list.
    /// There is NO <c>AppContext.BaseDirectory/plugins</c> in production — the install is read-only, §4.1.1.
    ///
    /// Note the asymmetry against the load path: this method DOES read ambient state, but it is a documented,
    /// testable helper the production caller uses to build the candidate list, not something <see cref="Load"/>
    /// depends on.
    /// </summary>
    /// <param name="overrideDir">The <c>%ACERHELPER_PLUGIN_DIR%</c> value, or null/empty when unset.</param>
    /// <param name="perUserCacheDir">The per-user cache root (<c>%AppData%\AcerHelper\plugins</c> on Windows,
    /// <c>~/.local/share/acer-helper/plugins</c> on Linux, §5.3.1), or null/empty when unknown.</param>
    /// <returns>The directories to scan, in priority order; the override first so a dev build shadows a cached
    /// plugin. Empty entries are dropped so the result has no "empty path" candidate.</returns>
    public static IReadOnlyList<string> EnumerateSearchPaths(string? overrideDir, string? perUserCacheDir)
    {
        var paths = new List<string>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(overrideDir)) paths.Add(overrideDir);
        if (!string.IsNullOrWhiteSpace(perUserCacheDir)) paths.Add(perUserCacheDir);
        return paths;
    }
}
