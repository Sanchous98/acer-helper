using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Vendor.AcerNitro;

/// <summary>
/// THE ACER-NITRO PLUGIN'S EXPORTS (docs/vendor-plugins.md §3.2, §6 Phase 1) — the eight <c>ah_*</c> C entry
/// points a loadable Native AOT plugin must carry.
///
/// THIS SLICE IS A SCAFFOLD, NOT THE BACKEND. The plugin currently contains the Acer DATA/CODEC/PORT files only
/// (see the csproj's explicit Compile list): the wiring files (<c>AcerDevice.*</c>, <c>AcerBattery.Windows.cs</c>)
/// extend the host-only <c>GenericDevice</c> and cannot compile standalone, so P3 rewrites the backend to fill a
/// probe manifest and answer ops instead. Until then <see cref="CreateExport"/> and <see cref="InvokeExport"/>
/// return <see cref="AbiStatus.NotSupported"/> / <see cref="AbiStatus.Internal"/> and write nothing: the thunks
/// EXIST and are exportable (which is what the scaffold must prove), but carry no behaviour yet. P3 fills them
/// from <c>AcerProfiles</c> / <c>AcerFanPort</c> / <c>AcerGpuMux</c> / <c>AcerEcHidController</c> and the
/// <c>acer-models.json</c> match rules; <see cref="MatchesExport"/> is the same DMI gate that will run then.
///
/// WHY THE THUNKS LIVE HERE AND NOT IN THE SHARED ABI FILE. Native AOT exports only
/// <c>[UnmanagedCallersOnly]</c> methods in the PUBLISHED assembly — "methods in project references or NuGet
/// packages won't be exported" (§2.1). The shared <c>VendorAbi.cs</c> is the contract (names, status/
/// capability/op vocabulary, host-side function-pointer struct) and is source-included; the implementations
/// must be in this project. That is the whole reason for the source-include-not-reference split (§3.1, §4.4).
///
/// THE THUNKS ARE NOT TESTABLE FROM MANAGED CODE. An <c>[UnmanagedCallersOnly]</c> method throws if called
/// directly from managed C#, so the source-text guards in <c>tests/.../AcerPluginScaffoldTests.cs</c> pin the
/// export contract and the no-exception-escaping rule because the runtime path cannot be exercised in a unit
/// test (§3.2, and the same posture as the proof plugin's source tests).
///
/// NO MANAGED TYPE CROSSES THE BOUNDARY and NO EXCEPTION ESCAPES: every argument is a blittable primitive or a
/// raw pointer, and every thunk body is wrapped in try/catch returning <see cref="AbiStatus.Internal"/> (void
/// thunks simply swallow) — the §3.2 hard requirement. A managed exception crossing into the host would be
/// undefined behaviour, so it is converted to a status code at the boundary.
/// </summary>
internal static unsafe class AcerPlugin
{
    /// <summary>The model-line id (§3.2, §5.1): the plugin claims to be the <c>acer-nitro</c> LINE, not the
    /// vendor, and this string must equal the csproj's <c>&lt;PluginId&gt;</c> (§5.1).</summary>
    internal const string PluginId = "acer-nitro";

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
            return WriteUtf8(outBuf, cap, System.Text.Encoding.UTF8.GetBytes(PluginId));
        }
        catch
        {
            return AbiStatus.Internal;
        }
    }

    // ---- ah_matches ----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_matches(const uint8* descJson, int32 descLen, uint8* outBuf, int32 cap,
    /// int32* outLen)</c> — the cheap DMI gate (§3.2). NOT IMPLEMENTED IN THIS SLICE: the real discriminator is
    /// the <c>acer-models.json</c> <c>Match</c> substring set (§1.4), which P3 wires in. Returning
    /// <see cref="AbiStatus.NotSupported"/> with no body is the honest scaffold answer: "this build does not
    /// decide matches yet", never a fabricated confidence the host could act on.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_matches", CallConvs = [typeof(CallConvCdecl)])]
    public static int MatchesExport(byte* descJson, int descLen, byte* outBuf, int cap, int* outLen)
    {
        try
        {
            if (outLen != null) *outLen = 0;
            return AbiStatus.NotSupported;
        }
        catch
        {
            if (outLen != null) *outLen = 0;
            return AbiStatus.Internal;
        }
    }

    // ---- ah_create -----------------------------------------------------------------------------------------

    /// <summary><c>int32 ah_create(const uint8* descJson, int32 descLen, uint8** outManifest, int32*
    /// outManifestLen, uint64* outHandle)</c> — probe the hardware, build a session, return the probe manifest
    /// (§3.4) and an opaque handle. NOT IMPLEMENTED IN THIS SLICE: probing the EC/HID/WMI transports and filling
    /// the manifest is P3's rewrite of <c>AcerDevice.InitVendor</c> (§6 Phase 1 step 3). Writes nothing and
    /// returns <see cref="AbiStatus.NotSupported"/>; also the handshake gate (§3.8.4 gate 3) once it returns.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateExport(byte* descJson, int descLen, byte** outManifest, int* outManifestLen, ulong* outHandle)
    {
        try
        {
            if (outManifest != null) *outManifest = null;
            if (outManifestLen != null) *outManifestLen = 0;
            if (outHandle != null) *outHandle = 0;
            return AbiStatus.NotSupported;
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
    /// int32 requestLen, uint8** outResponse, int32* outResponseLen)</c> — one capability operation. NOT
    /// IMPLEMENTED IN THIS SLICE: each capability's op table (§3.5) maps onto <c>AcerProfiles</c>,
    /// <c>AcerFanPort</c>, <c>AcerGpuMux</c>, <c>AcerEcHidController</c> and the RGB codecs, which is P3's work.
    /// Writes no response and returns <see cref="AbiStatus.NotSupported"/>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_invoke", CallConvs = [typeof(CallConvCdecl)])]
    public static int InvokeExport(ulong handle, uint capability, uint op, byte* request, int requestLen,
                                   byte** outResponse, int* outResponseLen)
    {
        try
        {
            if (outResponse != null) *outResponse = null;
            if (outResponseLen != null) *outResponseLen = 0;
            return AbiStatus.NotSupported;
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
    /// (§3.2). Present and correct even though this slice allocates nothing: the export is part of the contract
    /// the host resolves at load, and the allocation path it will serve is P3's.</summary>
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
    /// close HID/WMI handles. Idempotent (§3.2). A no-op in this slice (nothing is created yet); P3 disposes the
    /// EC/ENE controllers and their writer threads here.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_dispose", CallConvs = [typeof(CallConvCdecl)])]
    public static void DisposeExport(ulong handle)
    {
        try
        {
            // Nothing to release: the scaffold session holds no resources.
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- ah_set_event_sink ---------------------------------------------------------------------------------

    /// <summary><c>void ah_set_event_sink(uint64 handle, nint sink, nint ctx)</c> — the plugin→host event path.
    /// The scaffold raises no events, so it stores nothing (§3.2); P3 registers the hotkey sink here and forwards
    /// <c>AcerHotkeyReports.Decode</c> results through it.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ah_set_event_sink", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetEventSinkExport(ulong handle, nint sink, nint ctx)
    {
        try
        {
            // No events are raised by the scaffold.
        }
        catch
        {
            // A void export has no status channel; swallow (§3.2).
        }
    }

    // ---- Marshalling helper --------------------------------------------------------------------------------

    /// <summary>Copies <paramref name="data"/> into the caller's buffer when it fits and returns the number of
    /// bytes that WOULD be written; writes nothing when it does not (§3.2's <c>ah_plugin_id</c> contract).</summary>
    private static int WriteUtf8(byte* destination, int cap, byte[] data)
    {
        if (destination != null && cap >= data.Length && data.Length > 0)
            data.AsSpan().CopyTo(new Span<byte>(destination, data.Length));
        return data.Length;
    }
}
