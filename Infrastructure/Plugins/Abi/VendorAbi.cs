using System.Runtime.InteropServices;

namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// THE SHARED ABI CONTRACT — the one file compiled into BOTH the host and every plugin (docs/vendor-plugins.md
/// §3.1). It holds the entry-point NAMES, the status/capability/op vocabulary
/// (<see cref="AbiStatus"/>, <see cref="Capability"/>, <see cref="Operation"/>) and the host-side function
/// signatures, so host and plugin can never disagree about the wire form.
///
/// WHY THIS FILE IS SOURCE-INCLUDED AND NOT A REFERENCED LIBRARY. Native AOT exports only methods marked
/// <c>[UnmanagedCallersOnly]</c> in the PUBLISHED assembly — "methods in project references or NuGet packages
/// won't be exported" (§2.1). So the plugin's own thunk bodies must live in the plugin project (task T6), and
/// this file deliberately carries NONE OF THEM: it is the contract, not an implementation. The plugin csproj
/// compiles it by <c>&lt;Compile Include="...Abi\*.cs" /&gt;</c>; the host compiles it normally. A source-shared
/// file keeps one source of truth while still landing in each binary, which is exactly the §4.4 SDK pattern.
///
/// WHAT THE HOST COPY USES: the export NAMES and the <see cref="VendorAbiExports"/> struct below. The plugin
/// copy uses the same names for its <c>[UnmanagedCallersOnly(EntryPoint = ...)]</c> attributes and the same
/// status/capability/op constants, so the two sides cannot drift.
///
/// EVERY EXPORT IS <c>delegate* unmanaged[Cdecl]</c> (§2.5): x64 is the only target (one Windows convention,
/// one SysV convention), the plugin declares <c>CallConvs = [typeof(CallConvCdecl)]</c>, and the host resolves
/// each name with <c>NativeLibrary.GetExport</c>. Function pointers — not
/// <c>Marshal.GetDelegateForFunctionPointer</c> — are the AOT-friendly path (§2.5, dotnet/runtime#85699).
/// All buffers are raw UTF-8; <c>nint</c> is pointer-sized; no managed type crosses the boundary (§3.2).
/// </summary>
internal static class VendorAbi
{
    // ---- The eight entry-point names (§3.2). These are the ABI's literal linker contract: a rename here or
    //      in a plugin's EntryPoint silently breaks every plugin, which is why the foundation tests pin them. --

    /// <summary><c>uint32 ah_abi_version(void)</c> — the plugin API version it was built for,
    /// <c>(major &lt;&lt; 16) | minor</c>. Authoritative: the host trusts this, NOT the manifest (§3.8.4 gate 2).</summary>
    public const string AbiVersion = "ah_abi_version";

    /// <summary><c>int32 ah_plugin_id(uint8* outBuf, int32 cap)</c> — the MODEL-LINE id (e.g. "acer-nitro"),
    /// what the plugin claims to be (§1.4). Cheap; touches no hardware.</summary>
    public const string PluginId = "ah_plugin_id";

    /// <summary><c>int32 ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap,
    /// int32* outLen)</c> — the DMI gate; writes <c>{"match":bool,"confidence":0..100,"reason":"..."}</c>.
    /// Cheap; touches no hardware (§3.2).</summary>
    public const string Matches = "ah_matches";

    /// <summary><c>int32 ah_create(const uint8* descJson, int32 descLen, uint8** outManifest,
    /// int32* outManifestLen, uint64* outHandle)</c> — probe the hardware, build a session, return the probe
    /// manifest (§3.4) and an opaque handle. Also the handshake gate (§3.8.4 gate 3).</summary>
    public const string Create = "ah_create";

    /// <summary><c>int32 ah_invoke(uint64 handle, uint32 capability, uint32 op, const uint8* request,
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> — one capability operation; the
    /// response buffer is the plugin's and MUST be released with <see cref="Free"/> (§3.2).</summary>
    public const string Invoke = "ah_invoke";

    /// <summary><c>void ah_free(uint8* ptr)</c> — release a buffer the plugin returned (manifest or response).
    /// Null is a no-op (§3.2).</summary>
    public const string Free = "ah_free";

    /// <summary><c>void ah_dispose(uint64 handle)</c> — release the session; stop worker threads, close
    /// HID/WMI handles. Idempotent (§3.2). Does NOT unload the module — AOT libraries cannot be unloaded (§2.3).</summary>
    public const string Dispose = "ah_dispose";

    /// <summary><c>void ah_set_event_sink(uint64 handle, nint sink, nint ctx)</c> — the one two-way path: give
    /// the plugin a sink to raise events into the host (§3.2). v1 needs it for hotkeys only.</summary>
    public const string SetEventSink = "ah_set_event_sink";

    /// <summary>The kinds the plugin passes to the event sink. v1 has exactly the two §3.5 names; the sink is a
    /// <c>delegate* unmanaged[Cdecl]&lt;nint ctx, uint32 kind, const uint8* payload, int32 len, void&gt;</c>
    /// shaped by the host's <c>PluginHotkeys</c> adapter (T5).</summary>
    public static class EventKind
    {
        /// <summary>A mapped hotkey was pressed; payload is <c>{"action":"TogglePerformance"}</c>.</summary>
        public const uint Pressed = 1;

        /// <summary>Any special-key/raw input was observed (not just a mapped hotkey); empty payload.</summary>
        public const uint InputActivity = 2;
    }
}

/// <summary>
/// THE HOST-SIDE VIEW of a loaded plugin's exports — one <c>delegate* unmanaged[Cdecl]</c> field per entry
/// point, with the signatures of §3.2. The loader (task T4) loads the library, then fills this struct with
/// <c>NativeLibrary.GetExport(handle, VendorAbi.&lt;Name&gt;)</c> cast to the matching field type; the session
/// and adapters call through the fields thereafter. A plain struct of function pointers is the shape that makes
/// that a straight per-field assignment (no per-export wrapper type, no delegate allocation, no reflection).
///
/// IT IS <c>unsafe</c> AND THE FIELDS ARE PUBLIC, deliberately: T4 needs to write each field and the whole ABI
/// is a low-level contract. Nothing else in the host should reach in; adapters go through
/// <c>IPluginSession</c>, which is the managed, testable facade over this.
///
/// The signatures are transcribed from the C declarations in §3.2. Where the doc's C and the task's C# sketch
/// agree they are used as written; no detail was ambiguous, so no interpretation was needed. <c>byte*</c> is
/// <c>uint8*</c>, and <c>nint</c> is pointer-sized (§3.2).
/// </summary>
internal unsafe struct VendorAbiExports
{
    /// <summary><c>uint32 (*)(void)</c> — the plugin's built API version, <c>(major &lt;&lt; 16) | minor</c>.</summary>
    public delegate* unmanaged[Cdecl]<uint> AbiVersion;

    /// <summary><c>int32 (*)(uint8* outBuf, int32 cap)</c> — writes the model-line id; returns the number of
    /// bytes that WOULD be written (nothing written when it exceeds <paramref name="cap"/>).</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> PluginId;

    /// <summary><c>int32 (*)(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap, int32* outLen)</c>
    /// — the DMI match decision.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, byte*, int, int*, int> Matches;

    /// <summary><c>int32 (*)(const uint8* descJson, int32 descLen, uint8** outManifest, int32* outManifestLen,
    /// uint64* outHandle)</c> — probe and create the session; outManifest is freed with <see cref="Free"/>.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, ulong*, int> Create;

    /// <summary><c>int32 (*)(uint64 handle, uint32 capability, uint32 op, const uint8* request,
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> — one capability operation.</summary>
    public delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, int, byte**, int*, int> Invoke;

    /// <summary><c>void (*)(uint8* ptr)</c> — release a plugin-allocated buffer; null is a no-op.</summary>
    public delegate* unmanaged[Cdecl]<byte*, void> Free;

    /// <summary><c>void (*)(uint64 handle)</c> — release the session; idempotent.</summary>
    public delegate* unmanaged[Cdecl]<ulong, void> Dispose;

    /// <summary><c>void (*)(uint64 handle, nint sink, nint ctx)</c> — install the plugin→host event sink. The
    /// <c>sink</c> is itself a <c>delegate* unmanaged[Cdecl]&lt;nint ctx, uint32 kind, const uint8* payload,
    /// int32 len, void&gt;</c>, passed as a raw pointer because it crosses back over the same C boundary.</summary>
    public delegate* unmanaged[Cdecl]<ulong, nint, nint, void> SetEventSink;
}
