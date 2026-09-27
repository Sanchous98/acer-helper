using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A configurable <see cref="INativePluginBinding"/> — the whole reason <see cref="INativePluginBinding"/> exists
/// with no pointers in its signature (docs/vendor-plugins.md §4.1). A real Native AOT plugin cannot be built here
/// (no MSVC/clang), so this fake stands in for one exactly as the in-tree fake ports stand in for hardware: the
/// loader's version gate / threshold / tie rules and the session's invoke mapping are driven end-to-end without a
/// `.dll`.
///
/// Every ABI surface is settable so one class covers every test:
///   * <see cref="AbiVersion"/> (+ <see cref="AbiVersionFails"/>) for §3.8.4 gate 2;
///   * <see cref="PluginIdValue"/> for the diagnostic id;
///   * <see cref="MatchResult"/> for the §3.2 DMI gate;
///   * <see cref="CreateStatus"/>/<see cref="CreateManifestUtf8"/>/<see cref="CreateHandle"/> for §3.8.4 gate 3
///     and the manifest handoff;
///   * a per-<c>(capability, op)</c> invoke map (with a default) for the session;
///   * <see cref="DisposeCount"/> so "ah_dispose exactly once" is an assertion (§6).
///
/// The calls also record what crossed the boundary — <see cref="MatchesDescriptors"/>,
/// <see cref="CreateDescriptors"/>, <see cref="InvokeCalls"/> — so a test can assert the loader built the
/// descriptor (§3.2) and forwarded <c>(capability, op, request)</c> unchanged.
/// </summary>
internal sealed class FakeNativePluginBinding : INativePluginBinding
{
    // ---- Configured ABI answers -------------------------------------------------------------------------

    /// <summary>The value <c>ah_abi_version</c> returns, <c>(major &lt;&lt; 16) | minor</c> (§3.2).</summary>
    public uint AbiVersion { get; set; } = ((uint)PluginApi.Major << 16) | (uint)PluginApi.Minor;

    /// <summary>When true <see cref="TryGetAbiVersion"/> returns false (a module that cannot answer), which the
    /// loader skips.</summary>
    public bool AbiVersionFails { get; set; }

    /// <summary>The value <c>ah_plugin_id</c> returns.</summary>
    public string? PluginIdValue { get; set; } = "fake-line";

    /// <summary>When true <see cref="PluginId"/> returns null (the export failed).</summary>
    public bool PluginIdFails { get; set; }

    /// <summary>The <c>ah_matches</c> decision. Defaults to no match, so a test must opt into a match.</summary>
    public PluginMatchResult MatchResult { get; set; } = new(Matched: false, Confidence: 0, Reason: null);

    /// <summary>The status <c>ah_create</c> returns (default <see cref="AbiStatus.Ok"/>).</summary>
    public int CreateStatus { get; set; }

    /// <summary>The manifest bytes <c>ah_create</c> hands back (UTF-8).</summary>
    public byte[] CreateManifestUtf8 { get; set; } = [];

    /// <summary>The opaque session handle <c>ah_create</c> returns; passed back on every <c>ah_invoke</c>.</summary>
    public ulong CreateHandle { get; set; } = 0x1234;

    /// <summary>The default <c>ah_invoke</c> result when no per-(capability, op) entry is registered.</summary>
    public PluginInvokeResult DefaultInvokeResult { get; set; } = new(AbiStatus.Ok, "{}");

    private readonly Dictionary<(uint Capability, uint Op), PluginInvokeResult> _invokeResults = [];

    // ---- Recorded crossings -----------------------------------------------------------------------------

    /// <summary>The descriptor JSON of every <c>ah_matches</c> call, in order.</summary>
    public List<string> MatchesDescriptors { get; } = [];

    /// <summary>The descriptor JSON of every <c>ah_create</c> call, in order.</summary>
    public List<string> CreateDescriptors { get; } = [];

    /// <summary>Every <c>ah_invoke</c> triple, in order.</summary>
    public List<(ulong Handle, uint Capability, uint Op, string RequestJson)> InvokeCalls { get; } = [];

    /// <summary>How many times <see cref="Dispose"/> was called — asserted to be exactly 1 (§6).</summary>
    public int DisposeCount { get; private set; }

    /// <summary>An optional exception parked by a test to simulate a diagnostic hook.</summary>
    public Exception? LastError { get; set; }

    // ---- Configuration helpers --------------------------------------------------------------------------

    /// <summary>Register a canned result for one (capability, op) pair.</summary>
    public void SetInvokeResult(uint capability, uint op, int status, string? body) =>
        _invokeResults[(capability, op)] = new PluginInvokeResult(status, body);

    // ---- INativePluginBinding ---------------------------------------------------------------------------

    public bool TryGetAbiVersion(out uint encodedVersion)
    {
        encodedVersion = AbiVersionFails ? 0 : AbiVersion;
        return !AbiVersionFails;
    }

    public string? PluginId() => PluginIdFails ? null : PluginIdValue;

    public PluginMatchResult Matches(string descriptorJson)
    {
        MatchesDescriptors.Add(descriptorJson);
        return MatchResult;
    }

    public PluginCreateResult Create(string descriptorJson)
    {
        CreateDescriptors.Add(descriptorJson);
        return new PluginCreateResult(CreateStatus, CreateManifestUtf8, CreateHandle);
    }

    public PluginInvokeResult Invoke(ulong handle, uint capability, uint op, string requestJson)
    {
        InvokeCalls.Add((handle, capability, op, requestJson));
        return _invokeResults.TryGetValue((capability, op), out var r) ? r : DefaultInvokeResult;
    }

    public void Dispose() => DisposeCount++;
}
