using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Vendor.Proof;

/// <summary>
/// THE PHASE 0 PROOF PLUGIN'S EXPORTS (docs/vendor-plugins.md §3.2, §6 Phase 0 item 4) — the eight <c>ah_*</c>
/// C entry points a loadable Native AOT plugin must carry, and the trivial pure logic they wrap.
///
/// WHY THE THUNKS LIVE HERE AND NOT IN THE SHARED ABI FILE. Native AOT exports only <c>[UnmanagedCallersOnly]</c>
/// methods in the PUBLISHED assembly — "methods in project references or NuGet packages won't be exported"
/// (§2.1). The shared <c>VendorAbi.cs</c> is the contract (names, status/capability/op vocabulary, host-side
/// function-pointer struct) and is source-included; the implementations must be in this project. That is the
/// whole reason for the source-include-not-reference split.
///
/// THE THUNKS ARE NOT TESTABLE FROM MANAGED CODE. An <c>[UnmanagedCallersOnly]</c> method throws if called
/// directly from managed C#, so every thunk is a thin marshalling shell over an ordinary <c>internal static</c>
/// helper (<see cref="ProofManifest.Build"/>, <see cref="DecideMatch"/>, …) that CAN be reviewed and reasoned about.
/// The source-text guards in <c>tests/.../ProofPluginSourceTests.cs</c> pin the export contract and the
/// no-exception-escaping rule because the runtime path cannot be exercised in a unit test.
///
/// NO MANAGED TYPE CROSSES THE BOUNDARY and NO EXCEPTION ESCAPES: every argument is a blittable primitive or a
/// raw pointer, and every thunk body is wrapped in try/catch returning <see cref="AbiStatus.Internal"/> (void
/// thunks simply swallow) — the §3.2 hard requirement. A managed exception crossing into the host would be
/// undefined behaviour, so it is converted to a status code at the boundary.
/// </summary>
internal static unsafe class ProofPlugin
{
    /// <summary>The fixed plugin id (§3.2, §5.1): the proof plugin is a MODEL LINE called "proof", not a vendor.</summary>
    internal const string PluginId = "proof";

    /// <summary>The fixed handle <c>ah_create</c> hands back. Non-zero so the host can tell "a session exists"
    /// from "no session", and constant because the proof plugin owns no real resources (§3.2).</summary>
    internal const ulong SessionHandle = 1;

    // ---- ah_abi_version ------------------------------------------------------------------------------------

    /// <summary><c>uint32 ah_abi_version(void)</c> — <c>(major &lt;&lt; 16) | minor</c> from the generated
    /// <see cref="ProofApi"/> const (§3.8.1). The host trusts THIS, not the manifest (§3.8.4 gate 2); a fixture
    /// build pinned to another major is how the refusal path is exercised (§6 Phase 0 item 4).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion()
    {
        try
        {
            return (uint)ProofApi.Encoded;
        }
        catch
        {
            return unchecked((uint)AbiStatus.Internal);
        }
    }

    // ---- ah_plugin_id --------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_plugin_id(uint8* outBuf, int32 cap)</c> — writes "proof" as UTF-8 and returns the
    /// number of bytes that WOULD be written; nothing is written when it exceeds <paramref name="cap"/> (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_plugin_id", CallConvs = [typeof(CallConvCdecl)])]
    public static int PluginIdExport(byte* outBuf, int cap)
    {
        try
        {
            return WriteUtf8(outBuf, cap, System.Text.Encoding.UTF8.GetBytes(PluginId));
        }
        catch
        {
            return AbiStatus.Internal;
        }
    }

    // ---- ah_matches ----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap, int32* outLen)</c>
    /// — the cheap DMI gate (§3.2). Matches only when the descriptor's <c>manufacturer</c> contains "Proof"; writes
    /// <c>{"match":bool,"confidence":NN,"reason":"..."}</c> and reports its length through <paramref name="outLen"/>.
    /// Touches no hardware.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_matches", CallConvs = [typeof(CallConvCdecl)])]
    public static int MatchesExport(byte* descJson, int descLen, byte* outBuf, int cap, int* outLen)
    {
        try
        {
            var decision = DecideMatch(ReadUtf8(descJson, descLen));
            var body = System.Text.Encoding.UTF8.GetBytes(BuildMatchResponse(decision));
            WriteUtf8(outBuf, cap, body);
            if (outLen != null) *outLen = body.Length;
            return AbiStatus.Ok;
        }
        catch
        {
            if (outLen != null) *outLen = 0;
            return AbiStatus.Internal;
        }
    }

    // ---- ah_create -----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_create(const uint8* descJson, int32 descLen, uint8** outManifest, int32*
    /// outManifestLen, uint64* outHandle)</c> — returns the fixed probe manifest (§3.4) and a fixed handle. Also
    /// the handshake gate (§3.8.4 gate 3); the proof plugin accepts whatever the host sends because it declares
    /// only the current ABI's shape.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateExport(byte* descJson, int descLen, byte** outManifest, int* outManifestLen, ulong* outHandle)
    {
        try
        {
            var manifest = ProofManifest.Build();
            if (outManifest != null) *outManifest = AllocCopy(manifest);
            if (outManifestLen != null) *outManifestLen = manifest.Length;
            if (outHandle != null) *outHandle = SessionHandle;
            return AbiStatus.Ok;
        }
        catch
        {
            if (outManifest != null) *outManifest = null;
            if (outManifestLen != null) *outManifestLen = 0;
            if (outHandle != null) *outHandle = 0;
            return AbiStatus.Internal;
        }
    }

    // ---- ah_invoke -----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_invoke(uint64 handle, uint32 capability, uint32 op, const uint8* request,
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> — one capability operation. The proof
    /// plugin echoes: <see cref="Capability.Power"/> / <see cref="Operation.Power.Current"/> returns
    /// <c>{"id":"proof"}</c>, anything else returns the request body unchanged (or <c>{}</c> when empty). The
    /// response buffer is released by the host through <c>ah_free</c> (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_invoke", CallConvs = [typeof(CallConvCdecl)])]
    public static int InvokeExport(ulong handle, uint capability, uint op, byte* request, int requestLen,
                                   byte** outResponse, int* outResponseLen)
    {
        try
        {
            var response = DecideInvoke(capability, op, ReadUtf8Bytes(request, requestLen));
            if (outResponse != null) *outResponse = AllocCopy(response);
            if (outResponseLen != null) *outResponseLen = response.Length;
            return AbiStatus.Ok;
        }
        catch
        {
            if (outResponse != null) *outResponse = null;
            if (outResponseLen != null) *outResponseLen = 0;
            return AbiStatus.Internal;
        }
    }

    // ---- ah_free -------------------------------------------------------------------------------------------

    /// <summary><c>void ah_free(uint8* ptr)</c> — releases a buffer this plugin allocated. Null is a no-op (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void FreeExport(byte* ptr)
    {
        try
        {
            if (ptr != null) NativeMemory.Free(ptr);
        }
        catch
        {
            // A free failure has no status channel; swallow it rather than let it cross as an exception (§3.2).
        }
    }

    // ---- ah_dispose ----------------------------------------------------------------------------------------

    /// <summary><c>void ah_dispose(uint64 handle)</c> — the proof plugin owns no threads or OS handles, so this is
    /// a no-op. Idempotent by construction (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_dispose", CallConvs = [typeof(CallConvCdecl)])]
    public static void DisposeExport(ulong handle)
    {
        try
        {
            // Nothing to release: the proof session holds no resources.
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- ah_set_event_sink ---------------------------------------------------------------------------------

    /// <summary><c>void ah_set_event_sink(uint64 handle, nint sink, nint ctx)</c> — the plugin→host event path.
    /// The proof plugin raises no events, so it stores nothing (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_set_event_sink", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetEventSinkExport(ulong handle, nint sink, nint ctx)
    {
        try
        {
            // No events are raised by the proof plugin.
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- Pure logic (testable/reviewable; the thunks only marshal) -----------------------------------------

    /// <summary>The DMI decision (§3.2): match ONLY when the descriptor's <c>manufacturer</c> contains "Proof".
    /// Confidence is 100 on the match and 0 otherwise. Parsing is minimal and defensive: a missing, non-object or
    /// malformed descriptor is simply "not this machine" rather than an exception.</summary>
    internal static MatchDecision DecideMatch(string descriptorJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(descriptorJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("manufacturer", out var manufacturer)
                && manufacturer.ValueKind == JsonValueKind.String
                && manufacturer.GetString() is { } value
                && value.Contains("Proof", StringComparison.OrdinalIgnoreCase))
            {
                return new MatchDecision(true, 100, "manufacturer contains 'Proof'");
            }
        }
        catch (JsonException)
        {
            // A malformed descriptor is not a match; fall through to the default refusal.
        }

        return new MatchDecision(false, 0, "manufacturer does not contain 'Proof'");
    }

    /// <summary>The JSON body <c>ah_matches</c> writes (§3.2): the match flag, confidence and a short reason.
    /// Hand-built because it is two fields and keeps the thunk free of a second serializer path; the reason
    /// strings are fixed literals with no characters that need escaping.</summary>
    internal static string BuildMatchResponse(MatchDecision decision)
        => $"{{\"match\":{(decision.Match ? "true" : "false")},\"confidence\":{decision.Confidence},\"reason\":\"{decision.Reason}\"}}";

    /// <summary>The echo semantics of <c>ah_invoke</c> (§6 Phase 0 item 4): Power.Current answers with a fixed
    /// id, every other (capability, op) pair returns the request body unchanged, or <c>{}</c> when there is no
    /// body. Returns the response as UTF-8 bytes. Never null — an empty request still yields <c>{}</c>.</summary>
    internal static byte[] DecideInvoke(uint capability, uint op, byte[] request)
    {
        if (capability == Capability.Power && op == Operation.Power.Current)
            return System.Text.Encoding.UTF8.GetBytes("""{"id":"proof"}""");

        return request.Length > 0 ? request : System.Text.Encoding.UTF8.GetBytes("{}");
    }

    // ---- Marshalling helpers -------------------------------------------------------------------------------

    /// <summary>Copies <paramref name="data"/> into the caller's buffer when it fits and returns the number of
    /// bytes that WOULD be written; writes nothing when it does not (§3.2's <c>ah_plugin_id</c> contract).</summary>
    private static int WriteUtf8(byte* destination, int cap, byte[] data)
    {
        if (destination != null && cap >= data.Length && data.Length > 0)
            data.AsSpan().CopyTo(new Span<byte>(destination, data.Length));
        return data.Length;
    }

    /// <summary>Reads UTF-8 from a raw pointer/length pair into a managed string (empty when null/length 0).</summary>
    private static string ReadUtf8(byte* ptr, int len)
        => ptr == null || len <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(ptr, len);

    /// <summary>Copies UTF-8 from a raw pointer/length pair into a byte array (empty when null/length 0), so the
    /// echo path can return the request bytes unchanged without a decode/encode round-trip.</summary>
    private static byte[] ReadUtf8Bytes(byte* ptr, int len)
        => ptr == null || len <= 0 ? [] : new Span<byte>(ptr, len).ToArray();

    /// <summary>Allocates a native buffer (at least one byte so the returned pointer is never null) and copies
    /// <paramref name="data"/> into it. The host releases it with <c>ah_free</c> (§3.2).</summary>
    private static byte* AllocCopy(byte[] data)
    {
        var length = data.Length > 0 ? data.Length : 1;
        var buffer = (byte*)NativeMemory.Alloc((nuint)length);
        if (data.Length > 0)
            data.AsSpan().CopyTo(new Span<byte>(buffer, data.Length));
        return buffer;
    }

    /// <summary>The <see cref="DecideMatch"/> result: the match flag, the 0..100 confidence, and a short reason.</summary>
    internal readonly record struct MatchDecision(bool Match, int Confidence, string Reason);
}
