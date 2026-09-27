namespace AcerHelper.Infrastructure.Plugins.Distribution;

/// <summary>
/// The machine descriptor <c>ah_matches</c> is fed (§3.2) — the four cheapest DMI fields, read once by the host
/// and passed to every candidate plugin. It is reproduced here as a small record rather than reaching into
/// <c>Infrastructure/Vendors/Generic/MachineInfo</c> (which only exposes manufacturer + product) so this
/// selection layer stays a pure, testable function with no I/O and no dependency on a vendor-named type. The
/// field names are the wire names the descriptor JSON uses (§3.2).
/// </summary>
internal sealed record MachineDescriptor(
    string? Manufacturer = null,
    string? Product = null,
    string? Board = null,
    string? BoardProduct = null);

/// <summary>
/// The MANIFEST GATE — gate 1 of §3.8.4 — as a pure function: given the catalog, this machine's OS/arch, the
/// host's API + app versions and the DMI descriptor, return the entries worth downloading/loading, in a
/// deterministic order.
///
/// WHAT IT DECIDES, AND WHAT IT DOES NOT. It applies only the CHEAP, pre-download checks: OS/arch match, the
/// entry's <c>api.major</c> is supported, <c>minHost</c> ≤ the host app version, and the advisory
/// <c>matchHint</c> is consistent with this machine. It NEVER picks a winner: the design's authority is the
/// plugin's own <c>ah_matches</c> §3.2, which the host calls after loading. This selector narrows "what is
/// worth downloading and loading" — it is an optimisation and a UX guard, not the trust anchor (§3.8.4 gate 2
/// makes the plugin's code authoritative for the version, and §3.8.4 gate 1's own text says the manifest "can be
/// stale, hand-edited or copied").
///
/// THE <c>matchHint</c> IS ADVISORY ONLY (§3.2, §5.2). It may EXCLUDE an entry on a definite DMI mismatch and
/// may never CONFIRM one. That asymmetry is load-bearing: loading an unrelated plugin costs a resident AOT
/// runtime that cannot be unloaded (§2.3, §7.1 risk 1), but wrongly excluding the machine's own plugin leaves it
/// with no line backend — a worse failure. So a missing hint, an empty hint group and a null descriptor field
/// all mean "no constraint", and only a present group whose substrings all miss the matching field excludes.
///
/// PURITY AND DETERMINISM. No I/O, no clock, no ambient state, and the result is sorted by a documented tiebreak
/// (see <see cref="Compare"/>) so two runs over the same catalog produce the same sequence and tests can pin it.
/// </summary>
internal static class PluginCatalogSelector
{
    /// <summary>Select the candidate entries for this machine (§3.8.4 gate 1).
    ///
    /// <paramref name="supportedMajors"/> is the API-major set the host can adapt. It is an explicit parameter
    /// because the current-major-plus-deprecated concept belongs to the adapter registry (task T3, §3.8.2): this
    /// layer must not know which major is "current", only which are acceptable. Defaulting to the host's own
    /// major keeps the Phase 0 call site honest (a single shipped major, §3.8.2) while T3 widens it to
    /// <c>{ current, deprecated? }</c> without touching this file. A null or empty set means "no major is
    /// supported" and yields no candidates — fail closed, never "accept anything".</summary>
    public static IReadOnlyList<PluginCatalogEntry> Select(
        PluginCatalog catalog,
        string os,
        string arch,
        int hostApiMajor,
        int hostApiMinor,
        string hostAppVersion,
        MachineDescriptor machine,
        IReadOnlySet<int>? supportedMajors = null)
    {
        _ = hostApiMinor; // Part of the gate signature (§3.8) but not part of gate 1: §3.8.5 makes the major
                          // the gate and the minor informational, so selection must not branch on it. Named with
                          // a discard rather than omitted so a future caller cannot "helpfully" filter by minor.

        var majors = supportedMajors ?? new HashSet<int> { hostApiMajor };

        // The host version is parsed ONCE. If it cannot be parsed the host cannot prove any minHost is
        // satisfied, so the gate fails closed (no candidates) rather than downloading something it might not
        // be able to load. AppInfo.Version is a compile-time const (§3.8.1), so in practice this never fires.
        if (!TryParseAppVersion(hostAppVersion, out var hostVersion))
            return [];

        var selected = new List<PluginCatalogEntry>();
        foreach (var entry in catalog.Plugins ?? [])
        {
            if (!OsArchMatches(entry, os, arch)) continue;
            if (!MajorSupported(entry, majors)) continue;
            if (MinHostSatisfied(entry, hostVersion)) continue; // true = minHost ABOVE host => excluded
            if (!HintConsistent(entry, machine)) continue;
            selected.Add(entry);
        }

        // Deterministic order, independent of the manifest's own order (a JSON array is an ordered thing but a
        // re-serialised catalog must not reorder the candidates a caller sees). See Compare for the tiebreak.
        selected.Sort(Compare);
        return selected;
    }

    /// <summary>The documented total order for the result: <c>id</c>, then <c>os</c>, then <c>arch</c>, then
    /// <c>version</c>, all ordinal (the tokens are exact, not user text). An entry id is unique per machine
    /// because the release carries at most one build per id/os/arch/api.major during the deprecation window, so
    /// the first two keys already break real ties; the rest exist so the order is total even for malformed input
    /// (a duplicate id) rather than depending on the sort's stability.</summary>
    public static int Compare(PluginCatalogEntry a, PluginCatalogEntry b)
    {
        var c = string.CompareOrdinal(a.Id, b.Id);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Os, b.Os);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Arch, b.Arch);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Version, b.Version);
    }

    /// <summary>OS and arch must match the host's tokens exactly (§5.2). Case-insensitive so a manifest written
    /// "Win" / "X64" still works (the tokens are canonical lowercase, but tolerating case cannot admit a wrong
    /// machine — there is no machine whose arch is the case-variant of another). A null entry token is a
    /// malformed entry and matches nothing.</summary>
    private static bool OsArchMatches(PluginCatalogEntry entry, string os, string arch) =>
        string.Equals(entry.Os, os, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Arch, arch, StringComparison.OrdinalIgnoreCase);

    /// <summary>The entry's API major must be in the host's supported set (§3.8.4 gate 1). A null <c>api</c>
    /// object or <c>major</c> defaults to 0 and is therefore accepted only if 0 is somehow supported — which it
    /// never is — so a malformed entry fails closed.</summary>
    private static bool MajorSupported(PluginCatalogEntry entry, IReadOnlySet<int> majors) =>
        entry.Api is not null && majors.Contains(entry.Api.Major);

    /// <summary>True when the host is TOO OLD for the entry — i.e. <c>minHost</c> is above the host version,
    /// so the entry must be excluded (§3.8.4 gate 1). Semantics are inverted (returns "excluded") so the caller's
    /// <c>if (MinHostSatisfied(...)) continue;</c> reads as a gate rather than a double negative.
    ///
    /// A null/empty <c>minHost</c> is "no minimum" and never excludes. An UNPARSEABLE <c>minHost</c> excludes:
    /// the host cannot prove it satisfies the requirement, and this gate's whole purpose is to avoid downloading
    /// an asset it cannot load, so the safe reading of "unknown requirement" is "skip it" (fail closed).</summary>
    private static bool MinHostSatisfied(PluginCatalogEntry entry, Version hostVersion)
    {
        if (string.IsNullOrWhiteSpace(entry.MinHost)) return false;
        if (!TryParseAppVersion(entry.MinHost, out var minHost)) return true;
        return hostVersion < minHost;
    }

    /// <summary>The advisory hint check (§3.2). Every hint group that carries entries must match its descriptor
    /// field, case-insensitively, as a substring; a group that is null/empty imposes no constraint, and a null
    /// <see cref="PluginCatalogEntry.MatchHint"/> imposes none at all — so an entry with no hint is always
    /// returned, exactly as the design requires.
    ///
    /// Each group is tested against its SAME-NAMED field (manufacturer hints against manufacturer, etc.), which
    /// is what makes the hint coarse rather than a second, weaker <c>ah_matches</c>: it cannot match a product
    /// substring against the manufacturer field. A null descriptor field cannot satisfy a present group, so an
    /// entry hinting on a field this machine does not expose is excluded — correct, because the host could not
    /// have fed that field to <c>ah_matches</c> either.</summary>
    private static bool HintConsistent(PluginCatalogEntry entry, MachineDescriptor machine)
    {
        var hint = entry.MatchHint;
        if (hint is null) return true;

        return GroupMatches(hint.Manufacturer, machine.Manufacturer)
            && GroupMatches(hint.Product, machine.Product)
            && GroupMatches(hint.Board, machine.Board)
            && GroupMatches(hint.BoardProduct, machine.BoardProduct);
    }

    /// <summary>One hint group: no entries => no constraint (true); otherwise the descriptor field must be
    /// non-null and contain at least one hint substring (case-insensitive). An empty-string hint is skipped for
    /// the same reason <c>AcerModels.Detect</c> guards against it (<c>AcerModel.cs:56-58</c>): an empty
    /// substring matches everything and would silently turn a group into a no-op.</summary>
    private static bool GroupMatches(List<string>? hints, string? field)
    {
        if (hints is null || hints.Count == 0) return true;
        if (string.IsNullOrEmpty(field)) return false;
        foreach (var h in hints)
            if (!string.IsNullOrEmpty(h) && field.Contains(h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Parse a "v0.38.0"-ish app version to a comparable <see cref="Version"/>. This mirrors
    /// <c>UpdateChecker.TryParseVersion</c> (<c>UpdateChecker.cs:61-73</c>) deliberately: the leading numeric
    /// part is taken, a trailing prerelease suffix is discarded (the design compares on the numeric version,
    /// §3.8.4), and the result is normalised to four components so "0.38" and "0.38.0.0" compare equal rather
    /// than as <c>-1</c> versus <c>0</c>. It is a copy rather than a call because that method is private to
    /// <c>UpdateChecker</c> and this task owns only <c>Infrastructure/Plugins/Distribution/</c>.</summary>
    internal static bool TryParseAppVersion(string? text, out Version version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        if (!Version.TryParse(s[..end], out var v)) return false;
        version = new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);
        return true;
    }
}
