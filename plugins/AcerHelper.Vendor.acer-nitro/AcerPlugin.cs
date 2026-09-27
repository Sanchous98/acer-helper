using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Vendor.AcerNitro;

/// <summary>
/// THE ACER-NITRO PLUGIN'S EXPORTS (docs/vendor-plugins.md §3.2, §6 Phase 1) — the eight <c>ah_*</c> C entry
/// points a loadable Native AOT plugin must carry. The thunks here are THIN MARSHALLING SHELLS over ordinary
/// managed logic (<see cref="AcerMatch"/>, <see cref="AcerSession"/>), which is what makes the backend reviewable
/// and unit-testable even though an <c>[UnmanagedCallersOnly]</c> method cannot be called from managed C#.
///
/// WHY THE THUNKS LIVE HERE AND NOT IN THE SHARED ABI FILE. Native AOT exports only <c>[UnmanagedCallersOnly]</c>
/// methods in the PUBLISHED assembly — "methods in project references or NuGet packages won't be exported"
/// (§2.1). The shared <c>VendorAbi.cs</c> is the contract and is source-included; the implementations must be in
/// this project. That is the whole reason for the source-include-not-reference split (§3.1, §4.4).
///
/// NO MANAGED TYPE CROSSES THE BOUNDARY and NO EXCEPTION ESCAPES: every argument is a blittable primitive or a raw
/// pointer, and every thunk body is wrapped in try/catch returning <see cref="AbiStatus.Internal"/> (void thunks
/// simply swallow) — the §3.2 hard requirement. A managed exception crossing into the host would be undefined
/// behaviour, so it is converted to a status code at the boundary.
///
/// SESSIONS ARE HANDLES, NOT MEMORY. <c>ah_create</c> builds an <see cref="AcerSession"/>, stores it in a static
/// registry under a non-zero <c>ulong</c> handle and returns the handle; <c>ah_invoke</c> and <c>ah_dispose</c>
/// look it up. The registry is lock-guarded because the host calls <c>ah_invoke</c> from the UI thread and its
/// background pass concurrently — though <c>PluginSession</c> also serialises, a plugin must not assume its host
/// does. The manifest/response BYTES are the only native memory the plugin owns, exactly as §3.2 requires.
/// </summary>
internal static unsafe class AcerPlugin
{
    /// <summary>The model-line id (§3.2, §5.1): the plugin claims to be the <c>acer-nitro</c> LINE, not the
    /// vendor, and this string must equal the csproj's <c>&lt;PluginId&gt;</c> (§5.1).</summary>
    internal const string PluginId = "acer-nitro";

    /// <summary>The live sessions, keyed by handle. A real process has at most one (the loader keeps the winner);
    /// the map exists so the handle contract of §3.2 is honoured literally rather than via a single global.</summary>
    private static readonly Dictionary<ulong, AcerSession> Sessions = [];
    private static readonly object SessionsGate = new();
    private static ulong _nextHandle;

    // ---- ah_abi_version ------------------------------------------------------------------------------------

    /// <summary><c>uint32 ah_abi_version(void)</c> — <c>(major &lt;&lt; 16) | minor</c> from the generated
    /// <see cref="AcerPluginApi"/> const (§3.8.1). The host trusts THIS, not the manifest (§3.8.4 gate 2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion()
    {
        try
        {
            return unchecked((uint)AcerPluginApi.Encoded);
        }
        catch
        {
            return unchecked((uint)AbiStatus.Internal);
        }
    }

    // ---- ah_plugin_id --------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_plugin_id(uint8* outBuf, int32 cap)</c> — writes "acer-nitro" as UTF-8 and returns
    /// the number of bytes that WOULD be written; nothing is written when it exceeds <paramref name="cap"/>
    /// (§3.2). Cheap; touches no hardware.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_plugin_id", CallConvs = [typeof(CallConvCdecl)])]
    public static int PluginIdExport(byte* outBuf, int cap)
    {
        try
        {
            return WriteUtf8(outBuf, cap, Encoding.UTF8.GetBytes(PluginId));
        }
        catch
        {
            return AbiStatus.Internal;
        }
    }

    // ---- ah_matches ----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap,
    /// int32* outLen)</c> — the cheap DMI gate (§3.2). Delegates the decision to the pure
    /// <see cref="AcerMatch.DecideFromJson"/> (the <c>acer-models.json</c> <c>Match</c> substring set); this body
    /// only reads the input, writes the decision and reports its length. Touches no hardware.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_matches", CallConvs = [typeof(CallConvCdecl)])]
    public static int MatchesExport(byte* descJson, int descLen, byte* outBuf, int cap, int* outLen)
    {
        try
        {
            var (match, confidence, reason) = AcerMatch.DecideFromJson(ReadUtf8(descJson, descLen));
            var body = Encoding.UTF8.GetBytes(AcerMatch.BuildResponse(match, confidence, reason));
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
    /// outManifestLen, uint64* outHandle)</c> — probe the hardware, build the manifest (§3.4) and register a
    /// session. Also the handshake gate (§3.8.4 gate 3): the descriptor carries the host's
    /// <c>{"abi":{"major":…}}</c>, and a host major NEWER than this plugin's is refused with
    /// <see cref="AbiStatus.AbiMismatch"/>; an older-or-equal major proceeds. A machine with no Acer hardware still
    /// gets a session — its manifest simply declares the little that is answerable (§3.4, slice constraint 4).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateExport(byte* descJson, int descLen, byte** outManifest, int* outManifestLen, ulong* outHandle)
    {
        try
        {
            if (outManifest != null) *outManifest = null;
            if (outManifestLen != null) *outManifestLen = 0;
            if (outHandle != null) *outHandle = 0;

            var descriptor = ReadUtf8(descJson, descLen);
            if (AcerMatch.HostMajor(descriptor) is { } hostMajor && hostMajor > AcerPluginApi.Major)
                return AbiStatus.AbiMismatch;

            var session = AcerSession.Connect(AcerMatch.Product(descriptor));
            var manifest = session.BuildManifest();
            var handle = Register(session);

            if (outManifest != null) *outManifest = AllocCopy(manifest);
            if (outManifestLen != null) *outManifestLen = manifest.Length;
            if (outHandle != null) *outHandle = handle;
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
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> — one capability operation (§3.5). The
    /// session answers with a status + JSON body; the body is copied into a native buffer the host releases with
    /// <c>ah_free</c>. An unknown handle is <see cref="AbiStatus.NotSupported"/> (a host bug, §3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_invoke", CallConvs = [typeof(CallConvCdecl)])]
    public static int InvokeExport(ulong handle, uint capability, uint op, byte* request, int requestLen,
                                   byte** outResponse, int* outResponseLen)
    {
        try
        {
            if (outResponse != null) *outResponse = null;
            if (outResponseLen != null) *outResponseLen = 0;

            var session = Lookup(handle);
            if (session is null) return AbiStatus.NotSupported;

            var (status, body) = session.Invoke(capability, op, ReadUtf8(request, requestLen));
            var bytes = Encoding.UTF8.GetBytes(body);
            if (outResponse != null) *outResponse = AllocCopy(bytes);
            if (outResponseLen != null) *outResponseLen = bytes.Length;
            return status;
        }
        catch
        {
            if (outResponse != null) *outResponse = null;
            if (outResponseLen != null) *outResponseLen = 0;
            return AbiStatus.Internal;
        }
    }

    // ---- ah_free -------------------------------------------------------------------------------------------

    /// <summary><c>void ah_free(uint8* ptr)</c> — releases a buffer this plugin allocated. Null is a no-op
    /// (§3.2).</summary>
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

    /// <summary><c>void ah_dispose(uint64 handle)</c> — release the session: stop the plugin's worker threads,
    /// close HID/WMI handles. Idempotent (§3.2): a second call for the same handle finds no session and does
    /// nothing.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_dispose", CallConvs = [typeof(CallConvCdecl)])]
    public static void DisposeExport(ulong handle)
    {
        try
        {
            AcerSession? session;
            lock (SessionsGate) { Sessions.Remove(handle, out session); }
            session?.Dispose();
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- ah_set_event_sink ---------------------------------------------------------------------------------

    /// <summary><c>void ah_set_event_sink(uint64 handle, nint sink, nint ctx)</c> — the plugin→host event path.
    /// This slice declares NO hotkeys (the raw-input/evdev readers need an owner and the sink is the riskiest part
    /// of the ABI, §7.1 risk 9), so the sink is stored nowhere; the export exists because the host resolves it at
    /// load and a missing one fails the whole load (§3.2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_set_event_sink", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetEventSinkExport(ulong handle, nint sink, nint ctx)
    {
        try
        {
            // No events are raised by this slice.
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- session registry ----------------------------------------------------------------------------------

    /// <summary>Store a session and return its handle (never 0, so the host can tell "a session exists" from
    /// "none"). Handles are monotonically increasing and never reused within a process.</summary>
    private static ulong Register(AcerSession session)
    {
        lock (SessionsGate)
        {
            var handle = ++_nextHandle;
            Sessions[handle] = session;
            return handle;
        }
    }

    private static AcerSession? Lookup(ulong handle)
    {
        lock (SessionsGate) { return Sessions.TryGetValue(handle, out var session) ? session : null; }
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
        => ptr == null || len <= 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);

    /// <summary>Allocates a native buffer (at least one byte so the returned pointer is never null) and copies
    /// <paramref name="data"/> into it. The host releases it with <c>ah_free</c> (§3.2).</summary>
    private static byte* AllocCopy(byte[] data)
    {
        var length = data.Length > 0 ? data.Length : 1;
        var buffer = (byte*)NativeMemory.Alloc((nuint)length);
        if (data.Length > 0) data.AsSpan().CopyTo(new Span<byte>(buffer, data.Length));
        return buffer;
    }
}
