using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// THE MANAGED SESSION over a loaded plugin (docs/vendor-plugins.md §4.2, §4.3) — task T4's implementation of
/// <see cref="IPluginSession"/>, wrapping the three things a live plugin needs: the
/// <see cref="INativePluginBinding"/> (which owns the raw exports and the <c>ah_dispose</c>/<c>ah_free</c>
/// lifetime), the opaque handle <c>ah_create</c> returned, and the <see cref="IPluginAbiAdapter"/> chosen for the
/// plugin's major (§3.8.2). The decoded <see cref="PluginManifest"/> is held here too, because the adapters of
/// T5 read <c>All</c>/<c>Traits</c>/the capability-presence flags from the manifest without ever calling the
/// plugin.
///
/// IT KNOWS NO POINTERS AND NO MAJOR. It calls <see cref="INativePluginBinding.Invoke"/> through the managed
/// interface and lets the adapter translate the (capability, op) pair and the request/response bodies, so this
/// class is exerciseable with a fake binding and a fake adapter — no native library, no AOT. That split is the
/// same one <c>IPluginSession</c>'s own note describes; this is the concrete side of it.
///
/// REQUESTS ARE ADAPTED IN, RESPONSES ARE ADAPTED OUT, ON <c>Ok</c> ONLY. <see cref="Invoke"/> encodes the
/// internal request through <see cref="IPluginAbiAdapter.EncodeRequest"/> before crossing the boundary, and
/// decodes the response through <see cref="IPluginAbiAdapter.DecodeResponse"/> only when the plugin returned
/// <see cref="AbiStatus.Ok"/> — a non-<c>Ok</c> body is the <c>{"error":"…"}</c> refusal shape
/// (<c>AbiStatus.Refused</c>) and is passed through unchanged, because it is already major-neutral and the reason
/// must survive verbatim to become the adapter's <c>LastError</c> (§4.2). The STATUS itself is never touched:
/// it is a plain <c>int32</c> and the adapter is explicitly not its concern (<c>IPluginAbiAdapter</c>'s note).
///
/// THREAD-SAFETY. The host calls <see cref="Invoke"/> from the UI thread and from the background pass
/// concurrently (<c>AppController.cs:863-958</c> reads sensors/profiles on a pool thread while the UI reads the
/// current profile). The ABI promises no reentrancy of a plugin session, and the §4.3 rule is that adapters are
/// thread-safe because <c>Invoke</c> is synchronous — so this session SERIALISES the synchronous calls with a
/// lock: any thread may call it, and each call completes before the next begins. That is the conservative reading
/// of "the host never calls into the plugin from a plugin thread" and it costs nothing at these call rates
/// (§3.3: the boundary is not hot). The plugin's OWN writer threads stay inside the plugin and never re-enter
/// here.
///
/// DISPOSE calls <see cref="INativePluginBinding.Dispose"/>, which runs <c>ah_dispose</c> on the handle and
/// releases the library handle (§4.3). The module itself stays mapped — AOT libraries cannot be unloaded (§2.3)
/// — so disposing retires the vendor's OS handles, not the code. Idempotent via the binding's own guard.
/// </summary>
internal sealed class PluginSession : IPluginSession
{
    private readonly INativePluginBinding _binding;
    private readonly ulong _handle;
    private readonly IPluginAbiAdapter _adapter;

    /// <summary>Serialises the synchronous calls; see the class note on threading. Recursive calls would
    /// deadlock, which is correct — a plugin must not call back into its own session.</summary>
    private readonly object _callLock = new();

    private bool _disposed;

    /// <summary>
    /// Build a live session. Called by <see cref="VendorPluginLoader"/> ONLY after it has won the match and
    /// <c>ah_create</c> returned <see cref="AbiStatus.Ok"/>, so the handle is valid and the manifest is the one
    /// the winning adapter already decoded. The constructor does no I/O and cannot fail.
    /// </summary>
    /// <param name="binding">The loaded plugin; owns the exports and the dispose lifetime.</param>
    /// <param name="handle">The opaque <c>ah_create</c> handle, passed back on every <c>ah_invoke</c>.</param>
    /// <param name="adapter">The adapter for the plugin's major (§3.8.2), used to translate request/response.</param>
    /// <param name="manifest">The decoded probe manifest (§3.4), immutable after construction.</param>
    public PluginSession(INativePluginBinding binding, ulong handle, IPluginAbiAdapter adapter,
                         PluginManifest manifest)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _handle = handle;
    }

    /// <inheritdoc />
    public PluginManifest Manifest { get; }

    /// <summary>The reason from the last non-<c>Ok</c> invoke, decoded from its <c>{"error":"…"}</c> body, or null
    /// when the last call succeeded or carried no error text. This is the session-level analogue of the port
    /// <c>LastError</c> the adapters of T5 expose (§4.2); it is a read-only convenience for a log and is never
    /// consulted for a decision.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public PluginCallResult Invoke(uint capability, uint op, string requestJson)
    {
        ArgumentNullException.ThrowIfNull(requestJson);

        lock (_callLock)
        {
            // Adapt the internal request into THIS major's wire form before it crosses the boundary. V1 is the
            // identity (§3.8.2), a future major's adapter may renumber the (capability, op) pair or rename fields.
            var wireRequest = _adapter.EncodeRequest(capability, op, requestJson);
            var raw = _binding.Invoke(_handle, capability, op, wireRequest);

            if (raw.Status != AbiStatus.Ok)
            {
                // A refusal's body is the (ok, error) shape and must reach the caller verbatim as the reason; it
                // is not run through the adapter because it is already major-neutral (§4.2). Record it for a log.
                LastError = ExtractError(raw.Body);
                return new PluginCallResult(raw.Status, raw.Body);
            }

            LastError = null;
            var body = _adapter.DecodeResponse(capability, op, raw.Body);
            return new PluginCallResult(raw.Status, body);
        }
    }

    /// <summary>Pull <c>error</c> out of a <c>{"error":"…"}</c> body. A body that is absent, empty or not that
    /// shape yields null rather than throwing — <see cref="LastError"/> is a diagnostic, not a control path. Uses
    /// <see cref="JsonDocument"/> (AOT-safe, no reflection, §2.2) rather than a generated DTO because the error
    /// body is one scalar and adding it to <c>PluginJsonContext</c> (T1's file) buys nothing.</summary>
    private static string? ExtractError(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Dispose the binding (which calls <c>ah_dispose</c> and releases the library handle, §4.3).
    /// Idempotent: the binding guards the native call, and this session guards the reference.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binding.Dispose();
    }
}
