using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AcerHelper.Infrastructure.Plugins.Abi;

namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// THE ONE UNSAFE FILE IN THE PLUGIN FOUNDATION (docs/vendor-plugins.md §3.2, §4.1) — the only implementation of
/// <see cref="INativePluginBinding"/>, and the only place that touches <c>byte*</c>, <c>NativeLibrary</c> and the
/// T1 <see cref="VendorAbiExports"/> struct of function pointers.
///
/// WHY IT IS ONE FILE AND WHY THAT MATTERS. Local Native AOT cannot be built or tested here (no MSVC/clang), so
/// every line that needs a real plugin loaded is unprovable in this environment. The design answer is to make
/// that unprovable surface as small as possible and keep everything else behind a managed interface: the loader
/// and the session (the code with the selection/tie/threshold logic and the invoke mapping) talk only to
/// <see cref="INativePluginBinding"/>, which has NO pointers in its signature, so a fake drives them fully. This
/// class is the residue that a real plugin is required to exercise.
///
/// WHAT IT DOES. <c>TryLoad</c> loads the module by ABSOLUTE PATH (§2.2: .NET 10 no longer adds the app dir to the
/// native search path, and AOT rpaths are gone, so a relative load would silently fail) and resolves every export
/// in <see cref="VendorAbi"/> into a <see cref="VendorAbiExports"/>; a missing export is a failed load, never a
/// half-filled struct. Each wrapper then marshals the handful of managed-friendly shapes the interface exposes:
///
///   * inputs are UTF-8 pinned with <c>fixed</c> for the duration of the synchronous call — no input buffer is
///     ever owned by the plugin, so no input leaks;
///   * outputs are either a caller buffer (the id / the match decision) or a plugin-allocated buffer that MUST be
///     released with <c>ah_free</c> (§3.2) — and that release happens INSIDE the wrapper, so <c>ah_free</c> has a
///     single call site and cannot be forgotten or double-called by a caller;
///   * the opaque session handle returned by <c>ah_create</c> is remembered here so <c>Dispose</c> can call
///     <c>ah_dispose</c> exactly once (idempotent by the ABI, §3.2, and by the <c>_disposed</c> guard below).
///
/// NO EXCEPTION CROSSES THE BOUNDARY (§3.2: "No exception is allowed to escape: every export body is wrapped in
/// try/catch and returns -1"). The mirror rule on the host side is that no exception escapes INTO the loader
/// either — every wrapper catches, parks the exception in <see cref="LastError"/> for diagnostics, and returns a
/// safe "no result" that the loader treats as a discard (§4.3 "never a crash").
///
/// <c>ah_dispose</c> RELEASES THE VENDOR'S OWN HANDLES, NOT THE MODULE. AOT shared libraries can never be
/// unloaded (§2.3): <c>NativeLibrary.Free</c> here releases the OS handle for bookkeeping only, and the module's
/// code and runtime stay mapped until process exit. That is documented, not defended against.
///
/// FOLLOWS THE TREE'S EXISTING UNSAFE P/INVOKE STYLE: <c>delegate* unmanaged[Cdecl]</c> over blittable values,
/// like <c>NvidiaGpu.Windows.cs:50-55</c> and the raw-pointer marshalling of
/// <c>WbemInterop.Windows.cs</c> — no <c>Marshal.GetDelegateForFunctionPointer</c> (it is
/// <c>RequiresDynamicCode</c>, §2.5).
/// </summary>
internal sealed unsafe class NativePluginBinding : INativePluginBinding
{
    /// <summary>Size of the stack buffer used to read <c>ah_plugin_id</c>. A model-line id is short
    /// ("acer-predator"); 256 bytes is far above any real id and the export reports the would-be length, so an
    /// over-long id is detected and refused rather than truncated.</summary>
    private const int PluginIdBufferBytes = 256;

    /// <summary>Size of the stack buffer used to read the <c>ah_matches</c> decision JSON. The decision is
    /// <c>{"match":…,"confidence":…,"reason":"…"}</c> (§3.2); a reason string is a human phrase, so 4 KiB is
    /// deliberately generous. An oversized decision is refused, not truncated (a truncated JSON is a decode
    /// error, which is worse than a clean discard).</summary>
    private const int MatchBufferBytes = 4096;

    private nint _library;
    private readonly VendorAbiExports _exports;
    private ulong _handle;      // the ah_create handle, 0 until Create succeeds; ah_dispose is called on it.
    private bool _disposed;

    private NativePluginBinding(nint library, VendorAbiExports exports)
    {
        _library = library;
        _exports = exports;
    }

    /// <inheritdoc />
    public Exception? LastError { get; private set; }

    /// <summary>Load <paramref name="path"/> as a plugin and resolve its exports. Returns false — never throws —
    /// when the module is absent, is not a valid native image, or lacks any required export; the loader's
    /// contract is to discard such a candidate and continue (§4.1). <paramref name="binding"/> is null on every
    /// false return.
    ///
    /// A PARTIAL RESOLUTION IS A FAILURE: if any export in <see cref="VendorAbi"/> is missing the library is
    /// freed and the load fails, because a plugin that answers some calls would fail later on an unrelated path —
    /// the §3.2 contract is all-or-nothing.
    ///
    /// This is the production <c>Func&lt;string, INativePluginBinding?&gt;</c>: the loader is given a lambda over
    /// it, while tests inject their own fake and never reach this method (which is why it can be unsafe and
    /// untested locally without weakening any loader test).</summary>
    public static bool TryLoad(string path, out INativePluginBinding? binding)
    {
        binding = null;
        nint library = 0;
        try
        {
            library = NativeLibrary.Load(path);

            var exports = new VendorAbiExports
            {
                AbiVersion = (delegate* unmanaged[Cdecl]<uint>)
                    NativeLibrary.GetExport(library, VendorAbi.AbiVersion),
                PluginId = (delegate* unmanaged[Cdecl]<byte*, int, int>)
                    NativeLibrary.GetExport(library, VendorAbi.PluginId),
                Matches = (delegate* unmanaged[Cdecl]<byte*, int, byte*, int, int*, int>)
                    NativeLibrary.GetExport(library, VendorAbi.Matches),
                Create = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, ulong*, int>)
                    NativeLibrary.GetExport(library, VendorAbi.Create),
                Invoke = (delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, int, byte**, int*, int>)
                    NativeLibrary.GetExport(library, VendorAbi.Invoke),
                Free = (delegate* unmanaged[Cdecl]<byte*, void>)
                    NativeLibrary.GetExport(library, VendorAbi.Free),
                Dispose = (delegate* unmanaged[Cdecl]<ulong, void>)
                    NativeLibrary.GetExport(library, VendorAbi.Dispose),
                SetEventSink = (delegate* unmanaged[Cdecl]<ulong, nint, nint, void>)
                    NativeLibrary.GetExport(library, VendorAbi.SetEventSink),
            };

            binding = new NativePluginBinding(library, exports);
            return true;
        }
        catch (DllNotFoundException) { Free(library); return false; }        // not a native library / absent
        catch (BadImageFormatException) { Free(library); return false; }     // present but wrong arch/format
        catch (EntryPointNotFoundException) { Free(library); return false; } // resolved, but an export is missing
        catch (Exception) { Free(library); return false; }                   // GetExport can surface others; never throw
    }

    /// <summary>Release a library handle held only long enough to reject a load. <c>NativeLibrary.Free(0)</c> is a
    /// no-op, so the pre-load failure path can call this unconditionally.</summary>
    private static void Free(nint library)
    {
        if (library != 0) NativeLibrary.Free(library);
    }

    /// <inheritdoc />
    public bool TryGetAbiVersion(out uint encodedVersion)
    {
        encodedVersion = 0;
        try
        {
            encodedVersion = _exports.AbiVersion();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex;
            return false;
        }
    }

    /// <inheritdoc />
    public string? PluginId()
    {
        try
        {
            int wouldBe = _exports.PluginId(null, 0);
            if (wouldBe <= 0 || wouldBe > PluginIdBufferBytes) return null;

            byte* buf = stackalloc byte[wouldBe];
            int written = _exports.PluginId(buf, wouldBe);
            if (written <= 0 || written > wouldBe) return null;
            return Encoding.UTF8.GetString(buf, written);
        }
        catch (Exception ex)
        {
            LastError = ex;
            return null;
        }
    }

    /// <inheritdoc />
    public PluginMatchResult Matches(string descriptorJson)
    {
        try
        {
            byte[] desc = Encoding.UTF8.GetBytes(descriptorJson);
            byte* buf = stackalloc byte[MatchBufferBytes];
            int length;
            int status;
            fixed (byte* descPtr = desc)
            {
                status = _exports.Matches(descPtr, desc.Length, buf, MatchBufferBytes, &length);
            }

            // A non-zero status is a failed call, not a "does not match" verdict: the decision lives in the
            // body on Ok only. Either way the loader's reading is "not a candidate".
            if (status != AbiStatus.Ok || length <= 0 || length > MatchBufferBytes)
                return new PluginMatchResult(Matched: false, Confidence: 0, Reason: $"status {status}");

            return DecodeMatch(buf, length);
        }
        catch (Exception ex)
        {
            LastError = ex;
            return new PluginMatchResult(Matched: false, Confidence: 0, Reason: "host exception");
        }
    }

    /// <summary>Decode the <c>ah_matches</c> decision JSON (§3.2). Uses <see cref="JsonDocument"/> rather than a
    /// generated context because this is the raw layer and the shape is two scalars: a source-generated DTO would
    /// have to be added to <c>PluginJsonContext</c> (T1's file, off-limits here) for no benefit, and
    /// <c>JsonDocument.Parse</c> is AOT-safe (no reflection, §2.2). A body that is not the documented object
    /// degrades to <c>Matched=false</c> with a reason — a malformed decision is a discard, never a crash.
    /// A missing <c>confidence</c> reads as 0 and is therefore below any sane threshold, which is the safe
    /// reading of an underspecified decision (§3.2: default is "not this machine").</summary>
    private static PluginMatchResult DecodeMatch(byte* buf, int length)
    {
        using var doc = JsonDocument.Parse(new ReadOnlySpan<byte>(buf, length).ToArray());
        var root = doc.RootElement;
        bool matched = root.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.True;
        int confidence = root.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var v) ? v : 0;
        string? reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
        return new PluginMatchResult(matched, confidence, reason);
    }

    /// <inheritdoc />
    public PluginCreateResult Create(string descriptorJson)
    {
        try
        {
            byte[] desc = Encoding.UTF8.GetBytes(descriptorJson);
            byte* manifestPtr = null;
            int manifestLen = 0;
            ulong handle = 0;
            int status;
            fixed (byte* descPtr = desc)
            {
                status = _exports.Create(descPtr, desc.Length, &manifestPtr, &manifestLen, &handle);
            }

            // The manifest buffer belongs to the plugin and must be released with ah_free (§3.2) whether or not
            // we keep its bytes — hence the copy-then-free in finally rather than a bare return.
            byte[] manifest = [];
            if (manifestPtr != null && manifestLen > 0)
            {
                try { manifest = new ReadOnlySpan<byte>(manifestPtr, manifestLen).ToArray(); }
                finally { _exports.Free(manifestPtr); }
            }

            if (status == AbiStatus.Ok) _handle = handle; // remember so Dispose can ah_dispose exactly once
            return new PluginCreateResult(status, manifest, handle);
        }
        catch (Exception ex)
        {
            LastError = ex;
            return new PluginCreateResult(AbiStatus.Internal, [], 0);
        }
    }

    /// <inheritdoc />
    public PluginInvokeResult Invoke(ulong handle, uint capability, uint op, string requestJson)
    {
        try
        {
            byte[] request = Encoding.UTF8.GetBytes(requestJson);
            byte* responsePtr = null;
            int responseLen = 0;
            int status;
            fixed (byte* requestPtr = request)
            {
                status = _exports.Invoke(handle, capability, op, requestPtr, request.Length,
                                         &responsePtr, &responseLen);
            }

            // ah_free is the host's duty on every plugin-returned buffer (§3.2). It is released here, once, so
            // the managed result carries no pointer and no caller can leak or double-free.
            if (responsePtr == null) return new PluginInvokeResult(status, null);
            try
            {
                if (responseLen <= 0) return new PluginInvokeResult(status, null);
                return new PluginInvokeResult(status, Encoding.UTF8.GetString(responsePtr, responseLen));
            }
            finally { _exports.Free(responsePtr); }
        }
        catch (Exception ex)
        {
            LastError = ex;
            return new PluginInvokeResult(AbiStatus.Internal, null);
        }
    }

    /// <summary>Call <c>ah_dispose</c> on a created session, then release the library handle. Idempotent: a second
    /// call is a no-op, matching the ABI's own "<c>ah_dispose</c> … Idempotent" (§3.2) and the §4.3 lifetime rule
    /// that the session is disposed once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != 0)
        {
            try { _exports.Dispose(_handle); }
            catch (Exception ex) { LastError = ex; }
            _handle = 0;
        }

        // Bookkeeping only — the module stays mapped (§2.3). Clearing the handle makes a double Free impossible.
        Free(_library);
        _library = 0;
    }
}
