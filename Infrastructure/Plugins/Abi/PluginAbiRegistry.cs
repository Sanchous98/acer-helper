namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// The registry of per-major adapters the HOST can drive (docs/vendor-plugins.md §3.8.2, §3.8.3) — the object
/// that makes "hold the current major AND a deprecated one at once" real without any branch outside <c>Abi/</c>.
///
/// IT IS BUILT FROM AN EXPLICIT ADAPTER LIST, which is the whole point of the design: the loader asks
/// <see cref="ForMajor"/> for the adapter matching a plugin's <c>ah_abi_version().major</c> (§3.8.4 gate 2) and
/// either gets an adapter or a null refusal. Nothing is inferred from the folder name or from a name convention —
/// adapters are constructor data, so a test can build a synthetic two-major registry and prove BOTH paths
/// (<c>PluginAbiVersioningTests</c> does exactly that).
///
/// THE DEFAULT REGISTRY IS SINGLE-MAJOR TODAY. §3.8.2 "Status today": the first shipped API is 1.0, so
/// <see cref="CreateDefault"/> returns a registry holding just <c>AbiV1Adapter</c> and
/// <see cref="SupportedMajors"/> is <c>{ 1 }</c> — the deprecated slot is EMPTY and the mechanism costs nothing
/// until the first breaking change. The SHAPE, however, already holds <c>{ N current, N-1 deprecated }</c>, which
/// is what the first bump fills in.
///
/// THE BUMP LIFECYCLE (§3.8.3), written here because this class is where it is enforced:
///   * at API 1: <c>{1}</c> (current);
///   * at API 2: <c>{2 current, 1 deprecated}</c> — major 1's adapter gains <c>Deprecated = true</c> and stops
///     being the default; the internal model does NOT move, so the new major's adapter is the identity and the old
///     one translates (<see cref="AbiV1Adapter"/>'s note);
///   * at API 3: <c>{3 current, 2 deprecated}</c> and major 1 is gone — no adapter, so a plugin still declaring it
///     is refused at gate 2. Removal is the explicit trigger of §3.8.3, not drift.
///
/// The registry is IMMUTABLE once built: adapters are stateless per-major translators (the session holds the
/// state), so lookups need no lock and the loader may share one instance for the process.
/// </summary>
internal sealed class PluginAbiRegistry
{
    /// <summary>Major → adapter. Every supported major has exactly one entry by construction.</summary>
    private readonly Dictionary<int, IPluginAbiAdapter> _byMajor;

    /// <summary>The supported majors, ASCENDING, so enumeration is deterministic for tests and logs. An array
    /// (not the dictionary's key view) is what gives a stable order to a structure that is otherwise a hash set.
    /// </summary>
    private readonly int[] _supportedMajors;

    /// <summary>
    /// Build a registry from the adapters the caller ships. Validation is fail-fast because a malformed set is a
    /// build-time configuration bug, not a runtime condition:
    ///   * at least one adapter, and every <see cref="IPluginAbiAdapter.Major"/> &gt; 0 (major 0 is not an API);
    ///   * majors are unique — two adapters for one major would make <see cref="ForMajor"/> non-deterministic;
    ///   * EXACTLY ONE non-deprecated (current) adapter — the internal call model is the current major's model
    ///     (§3.8.2), so "which major is the model" must have one answer;
    ///   * AT MOST ONE deprecated adapter — the §3.8.2 window is current + previous, no wider.
    /// </summary>
    /// <exception cref="ArgumentException">The set violates one of the rules above.</exception>
    public PluginAbiRegistry(IEnumerable<IPluginAbiAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _byMajor = [];
        IPluginAbiAdapter? current = null;
        var deprecatedCount = 0;

        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (adapter.Major <= 0)
                throw new ArgumentException($"Adapter major must be > 0 (got {adapter.Major}).", nameof(adapters));
            if (!_byMajor.TryAdd(adapter.Major, adapter))
                throw new ArgumentException($"Duplicate adapter for major {adapter.Major}.", nameof(adapters));

            if (adapter.Deprecated)
            {
                deprecatedCount++;
                if (deprecatedCount > 1)
                    throw new ArgumentException(
                        "At most one deprecated adapter is supported — the §3.8.2 window is current + previous.",
                        nameof(adapters));
            }
            else if (current is not null)
            {
                throw new ArgumentException(
                    $"Two non-deprecated adapters ({current.Major} and {adapter.Major}); exactly one current major "
                    + "is required because the internal model IS the current major's model (§3.8.2).",
                    nameof(adapters));
            }
            else
            {
                current = adapter;
            }
        }

        if (current is null)
            throw new ArgumentException(
                "No non-deprecated (current) adapter; the internal model must have exactly one current major "
                + "(§3.8.2).",
                nameof(adapters));

        CurrentMajor = current.Major;
        _supportedMajors = [.. _byMajor.Keys];
        Array.Sort(_supportedMajors);
    }

    /// <summary>The host's CURRENT API major — the adapter whose model the host speaks internally (§3.8.2). By
    /// construction this equals the major of the single non-deprecated adapter.</summary>
    public int CurrentMajor { get; }

    /// <summary>The majors this host can drive: <c>{ current }</c> today, or <c>{ current, deprecated }</c> once a
    /// bump has happened (§3.8.2). Ascending and immutable; the set is exactly what §3.8.4 gate 1 (manifest) and
    /// gate 2 (load) test a plugin's declared major against.</summary>
    public IReadOnlyCollection<int> SupportedMajors => _supportedMajors;

    /// <summary>The adapter for a plugin's declared major, or <c>null</c> when the host cannot adapt it. A null is
    /// the §3.8.4 gate-2 refusal — the loader discards the session and falls back with
    /// <c>status.plugin_incompatible</c> (§4.1.1); <see cref="AbiCompatibility.Decide"/> is the named decision
    /// built on this lookup.
    ///
    /// NOT AN EXCEPTION, deliberately: an unsupported major is a plugin the user may legitimately have from an
    /// older/newer release, so it is an expected "no" the caller branches on, not a fault.</summary>
    public IPluginAbiAdapter? ForMajor(int major) =>
        _byMajor.TryGetValue(major, out var adapter) ? adapter : null;

    /// <summary>
    /// The registry the shipped host builds: today a single current <see cref="AbiV1Adapter"/> because the host
    /// runs API <c>PluginApi.Major + "." + PluginApi.Minor</c> = 1.0 (§3.8.2). When the host's major bumps, THIS is
    /// the one method to repoint: construct the new major's adapter as current and pass the old
    /// <see cref="AbiV1Adapter"/> with <c>Deprecated = true</c> — no other file changes (§3.8.3).
    ///
    /// THE BUMP GUARD. It also asserts the adapters still cover <see cref="PluginApi.Major"/>, so bumping
    /// <c>&lt;PluginApiVersion&gt;</c> and forgetting to add/repoint an adapter fails loudly HERE rather than
    /// silently shipping a host that refuses its own plugins. This is the same fail-fast spirit as
    /// <c>GeneratePluginApiVersion</c> being a generated const (§3.8.1): the two cannot drift unnoticed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The default adapters do not cover the generated
    /// <see cref="PluginApi.Major"/> — i.e. the version was bumped without the matching adapter change.</exception>
    public static PluginAbiRegistry CreateDefault()
    {
        var registry = new PluginAbiRegistry([new V1.AbiV1Adapter()]);
        if (registry.CurrentMajor != PluginApi.Major)
            throw new InvalidOperationException(
                $"PluginApiVersion is {PluginApi.Major}.{PluginApi.Minor} but CreateDefault ships a current adapter "
                + $"for major {registry.CurrentMajor}. A major bump must add/repoint the current-major adapter "
                + "(§3.8.2/§3.8.3) — the deprecated slot is filled in the same change.");
        return registry;
    }
}
