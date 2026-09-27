using AcerHelper.Infrastructure.Plugins;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Tests.Fakes;

/// <summary>
/// A configurable <see cref="IPluginSession"/> returning canned bodies per <c>(capability, op)</c> — the stand-in
/// that lets the T5 capability adapters be driven end-to-end with no plugin, no pointer and no native library
/// (docs/vendor-plugins.md §4.2, and <see cref="IPluginSession"/>'s own note on why the interface exists: "a fake
/// session returns canned <see cref="PluginCallResult"/>s exactly as the in-tree fake ports stand in for
/// hardware").
///
/// It is the session-level sibling of <see cref="FakeNativePluginBinding"/>: that one fakes the ABI exports and
/// feeds the real <c>PluginSession</c> (T4); this one fakes the session the adapters consume. The adapters are the
/// only SUT of <c>PluginAdapterTests</c>, so faking at the session boundary is what keeps those tests about the
/// adapters' forwarding and mapping rather than about the loader or the binding.
///
/// <see cref="InvokeCalls"/> records every <c>(capability, op, request)</c> triple so a test can assert an adapter
/// forwarded the RIGHT pair (not merely "called something"), and <see cref="DisposeCount"/> lets the device
/// assembly test assert <c>ah_dispose</c> reaches the session exactly once (§4.3).
/// </summary>
internal sealed class FakePluginSession : IPluginSession
{
    private readonly Dictionary<(uint Capability, uint Op), PluginCallResult> _results = [];

    public FakePluginSession(PluginManifest manifest) => Manifest = manifest;

    /// <inheritdoc />
    public PluginManifest Manifest { get; }

    /// <summary>The default result when no per-(capability, op) entry is registered: Ok with the empty object.</summary>
    public PluginCallResult DefaultResult { get; set; } = new(AbiStatus.Ok, "{}");

    /// <summary>Every <c>ah_invoke</c> triple the adapters made, in order.</summary>
    public List<(uint Capability, uint Op, string RequestJson)> InvokeCalls { get; } = [];

    /// <summary>How many times <see cref="Dispose"/> was called — asserted to be exactly 1 (§4.3).</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Register a canned result for one (capability, op) pair.</summary>
    public void SetResult(uint capability, uint op, int status, string? body) =>
        _results[(capability, op)] = new PluginCallResult(status, body);

    /// <inheritdoc />
    public PluginCallResult Invoke(uint capability, uint op, string requestJson)
    {
        InvokeCalls.Add((capability, op, requestJson));
        return _results.TryGetValue((capability, op), out var result) ? result : DefaultResult;
    }

    /// <inheritdoc />
    public void Dispose() => DisposeCount++;
}
