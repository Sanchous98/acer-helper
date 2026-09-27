namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// The host's INTERNAL call model over a loaded plugin — the abstraction the loader creates and the capability
/// adapters consume (docs/vendor-plugins.md §4.2/§4.3).
///
/// IT KNOWS NOTHING ABOUT <c>NativeLibrary</c>, and that is the point: the binding to the native exports (the
/// <c>delegate* unmanaged[Cdecl]</c> fields of <c>VendorAbiExports</c>, the <c>ah_free</c>/<c>ah_dispose</c>
/// lifetime, the handle) is task T4's implementation of this interface, and the adapters of task T5 are written
/// against this interface alone. That split is what makes the whole adapter layer testable without a real
/// plugin: a fake session returns canned <see cref="PluginCallResult"/>s exactly as the in-tree fake ports
/// stand in for hardware.
///
/// <see cref="Invoke"/> takes and returns JSON (the §3.3 payload decision) and returns the ABI status
/// (<c>AbiStatus</c>) plus the body — NOT an exception, because no managed exception may cross the boundary
/// (§2.1, §3.2). A <see cref="AbiStatus.Refused"/> result carries its reason in the body; a non-<c>Ok</c>
/// result is still returned, never thrown, so the adapter can map it to the port's <c>LastError</c> shape.
///
/// The session is created once and lives for the process (AOT plugins cannot be unloaded, §2.3); disposing it
/// calls <c>ah_dispose</c> and releases any outstanding buffer (§4.3).
/// </summary>
internal interface IPluginSession : IDisposable
{
    /// <summary>The probe manifest <c>ah_create</c> returned (§3.4), decoded through
    /// <c>PluginJsonContext</c>. Immutable after construction: adapters read <c>All</c>/<c>Traits</c>/the
    /// capability presence flags from here without calling the plugin.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Run one capability operation. <paramref name="capability"/> and <paramref name="op"/> are the
    /// codes of <c>Capability</c>/<c>Operation</c>; <paramref name="requestJson"/> is the request body (the
    /// empty JSON object <c>{}</c> when the op takes none). Returns the plugin's status and its response body
    /// — <see cref="PluginCallResult.Body"/> is null when the plugin returned no body, and is a
    /// <c>{"error":"..."}</c> body on a refusal.</summary>
    PluginCallResult Invoke(uint capability, uint op, string requestJson);
}

/// <summary>The result of one <see cref="IPluginSession.Invoke"/>: the ABI status code
/// (<c>AbiStatus</c>) and the plugin's response body, or null when it returned none.
///
/// A RECORD STRUCT because it is a small, transient return value with no identity — the same shape the tree
/// already uses for <c>GpuMuxChange</c> (Domain/GpuMux.cs:43) and <c>FanSettings</c> (Domain/Fan.cs:27). The
/// status is a plain <c>int</c> rather than the <c>AbiStatus</c> enum, matching the C ABI where the code
/// is an <c>int32</c>: a plugin compiled against a newer minor may return a code this build does not name, and
/// an <c>int</c> keeps that value visible instead of making it undefined.</summary>
internal readonly record struct PluginCallResult(int Status, string? Body);
