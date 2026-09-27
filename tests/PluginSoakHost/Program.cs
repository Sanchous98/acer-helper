using System.Runtime.InteropServices;
using AcerHelper.Infrastructure.Plugins.Abi;

// =============================================================================================================
// PluginSoakHost — the Phase 0 smoke + soak host for the two-AOT-runtimes measurement.
// =============================================================================================================
//
// WHY THIS CONSOLE EXISTS (docs/vendor-plugins.md §2.4 "Two AOT runtimes in one process — the largest unknown",
// §7.1 risk 1, and the Phase 0 verification in §6). The shipping app cannot be used for this measurement: it
// requires administrator (app.manifest `requireAdministrator`; Bootstrap/Program.cs starts Avalonia) and so
// cannot be driven on a CI runner. This is a SEPARATE, dependency-free AOT console executable that
// `NativeLibrary.Load`s the AOT proof plugin and calls its exports. Loading an AOT executable that then loads an
// AOT SHARED LIBRARY is exactly the condition the design is betting on: a SECOND copy of the AOT runtime (its own
// GC, its own signal handlers) enters the process. dotnet/runtime#121345 reports a real hang when this happens.
//
// WHAT IT MEASURES. A SMOKE pass (always run) proves the plugin loads and every one of the eight §3.2 entry
// points resolves and is callable over the C boundary, with the documented status codes and payloads. A SOAK
// pass (`--soak-minutes N`) then holds the session and, for N minutes, calls ah_invoke/ah_free on a loop while
// churning managed garbage on the HOST runtime and forcing GCs — the stress that makes the signal-handler/GC
// interaction (§2.4) manifest. A hang means the CI job times out; a crash means a non-zero exit; a clean exit 0
// after N minutes is the pass.
//
// AOT IS REQUIRED FOR THE MEASUREMENT TO MEAN ANYTHING. Under a JIT run there is no second AOT runtime and no
// AOT signal handler to collide with, so the soak would pass trivially and prove nothing. The authority is the
// `dotnet publish -p:PublishAot=true` + run in .github/workflows/plugin-soak.yml, not a local `dotnet run`.
//
// DEPENDENCY-FREE BY CONSTRUCTION. No Avalonia, no Domain/UI, no System.Text.Json. The four ABI files of the
// contract are SOURCE-INCLUDED (see the csproj), not referenced, so this host compiles against the real §3.2
// vocabulary while dragging in nothing from the app. The raw `NativeLibrary.Load`/`GetExport` marshalling below
// IS the smoke: the point is to prove the exports exist and are callable, not to reuse the app's loader.

internal static class Program
{
    private static int Main(string[] args)
    {
        // argv[0] is the plugin path; --soak-minutes N defaults to 0 (smoke only); --abi major.minor defaults to
        // the current shipped 1.0. Parsed by hand because the host is deliberately dependency-free (no
        // System.CommandLine), and the surface is two flags.
        string? pluginPath = null;
        var soakMinutes = 0;
        var expectedMajor = 1;
        var expectedMinor = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--soak-minutes":
                    if (++i >= args.Length || !int.TryParse(args[i], out soakMinutes) || soakMinutes < 0)
                    {
                        Console.Error.WriteLine("SOAK-HOST FAIL: --soak-minutes needs a non-negative integer.");
                        return 1;
                    }
                    break;
                case "--abi":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("SOAK-HOST FAIL: --abi needs a major.minor value.");
                        return 1;
                    }
                    var parts = args[i].Split('.');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out expectedMajor) || !int.TryParse(parts[1], out expectedMinor))
                    {
                        Console.Error.WriteLine($"SOAK-HOST FAIL: --abi '{args[i]}' is not major.minor.");
                        return 1;
                    }
                    break;
                default:
                    pluginPath ??= args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(pluginPath))
        {
            Console.Error.WriteLine("usage: PluginSoakHost <plugin-path> [--abi major.minor] [--soak-minutes N]");
            return 1;
        }

        // §2.2/.NET 10: the host must load by ABSOLUTE path (.NET 10 removed the app directory from the native
        // search path and dropped the implicit AOT rpath), so resolve it once and fail loudly if it is absent.
        var absolute = Path.GetFullPath(pluginPath);
        if (!File.Exists(absolute))
        {
            Console.Error.WriteLine($"SOAK-HOST FAIL: plugin not found at '{absolute}'.");
            return 1;
        }

        var expected = (uint)((expectedMajor << 16) | expectedMinor);
        Console.WriteLine($"SOAK-HOST: plugin = {absolute}");
        Console.WriteLine($"SOAK-HOST: expected ABI = {expectedMajor}.{expectedMinor} (0x{expected:X8}), soak minutes = {soakMinutes}");

        try
        {
            // The whole AOT run happens under an unsafe block: every ABI call is a delegate* unmanaged[Cdecl]
            // invoked directly (§2.5), never Marshal.GetDelegateForFunctionPointer (which is RequiresDynamicCode
            // and not AOT-friendly).
            unsafe
            {
                Run(absolute, expected, soakMinutes);
            }
        }
        catch (SmokeFailure failure)
        {
            Console.Error.WriteLine($"SOAK-HOST FAIL: {failure.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // A DllNotFound/BadImageFormat/EntryPointNotFound here means the plugin is not a loadable AOT library
            // or the runtime could not enter a second copy — the load itself failing is a finding, not a flake.
            Console.Error.WriteLine($"SOAK-HOST FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine("SOAK-HOST OK.");
        return 0;
    }

    private static unsafe void Run(string pluginPath, uint expectedAbi, int soakMinutes)
    {
        // 1) LOAD. This is the moment the second AOT runtime enters the process (§2.4). NativeLibrary.Load by
        //    absolute path; the handle is never freed because AOT libraries cannot be unloaded (§2.3) and the
        //    process exits shortly after.
        var library = NativeLibrary.Load(pluginPath);
        Step("load", $"NativeLibrary.Load -> 0x{library:X}");

        // 2) RESOLVE ALL EIGHT EXPORTS. A single missing name (a typo, a rename, a trimmed export) would be a
        //    silent load-time miss in production; here it is a hard failure. NativeLibrary.GetExport throws
        //    EntryPointNotFoundException on a miss, so the assertion and the resolution are the same call.
        var exports = new VendorAbiExports
        {
            AbiVersion = (delegate* unmanaged[Cdecl]<uint>)Export(library, VendorAbi.AbiVersion),
            PluginId = (delegate* unmanaged[Cdecl]<byte*, int, int>)Export(library, VendorAbi.PluginId),
            Matches = (delegate* unmanaged[Cdecl]<byte*, int, byte*, int, int*, int>)Export(library, VendorAbi.Matches),
            Create = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, ulong*, int>)Export(library, VendorAbi.Create),
            Invoke = (delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, int, byte**, int*, int>)Export(library, VendorAbi.Invoke),
            Free = (delegate* unmanaged[Cdecl]<byte*, void>)Export(library, VendorAbi.Free),
            Dispose = (delegate* unmanaged[Cdecl]<ulong, void>)Export(library, VendorAbi.Dispose),
            SetEventSink = (delegate* unmanaged[Cdecl]<ulong, nint, nint, void>)Export(library, VendorAbi.SetEventSink),
        };
        Step("resolve", "all eight ah_* exports resolved");

        // 3) ah_abi_version() — the authoritative built version (§3.8.4 gate 2), (major << 16) | minor.
        var reported = exports.AbiVersion();
        Require(reported == expectedAbi,
            $"ah_abi_version returned 0x{reported:X8}, expected 0x{expectedAbi:X8}");
        Step("ah_abi_version", $"0x{reported:X8} ({reported >> 16}.{reported & 0xFFFF})");

        // 4) ah_plugin_id() — expects "proof" (§1.4, §5.1). WriteUtf8 semantics: returns the number of bytes that
        //    WOULD be written; we pass a generous buffer so it actually writes and then decode exactly that many.
        {
            var id = ReadString(exports.PluginId);
            Require(id == "proof", $"ah_plugin_id returned '{id}', expected 'proof'");
            Step("ah_plugin_id", id);
        }

        // 5) ah_matches() — the cheap DMI gate (§3.2). A "Proof" manufacturer must match; anything else must not.
        {
            var matched = CallMatches(exports, """{"manufacturer":"Proof","product":"Proof Book","board":"","boardProduct":""}""");
            Require(matched.Contains("\"match\":true", StringComparison.Ordinal),
                $"ah_matches did not match a Proof descriptor: {matched}");
            Step("ah_matches(proof)", matched);

            var unmatched = CallMatches(exports, """{"manufacturer":"Dell","product":"XPS 15","board":"","boardProduct":""}""");
            Require(unmatched.Contains("\"match\":false", StringComparison.Ordinal),
                $"ah_matches matched a non-Proof descriptor: {unmatched}");
            Step("ah_matches(non-proof)", unmatched);
        }

        // 6) ah_create() — probe/build a session, return the manifest + opaque handle (§3.2, §3.4). The manifest
        //    is a plugin-allocated buffer and MUST be released with ah_free.
        ulong handle;
        {
            var desc = Utf8("""{"manufacturer":"Proof","product":"Proof Book","board":"","boardProduct":""}""");
            fixed (byte* pDesc = desc)
            {
                byte* manifest = null;
                var manifestLen = 0;
                var status = exports.Create(pDesc, desc.Length, &manifest, &manifestLen, &handle);
                Require(status == AbiStatus.Ok, $"ah_create returned status {status}, expected {AbiStatus.Ok}");
                Require(manifest != null && manifestLen > 0, "ah_create returned an empty manifest");
                var manifestText = Marshal.PtrToStringUTF8((nint)manifest, manifestLen) ?? string.Empty;
                Require(manifestText.Contains("\"pluginId\":\"proof\"", StringComparison.Ordinal),
                    $"manifest does not declare pluginId proof: {manifestText}");
                // The plugin allocated this buffer; release it through the plugin's own ah_free (§3.2).
                exports.Free(manifest);
                Step("ah_create", $"{manifestLen}-byte manifest, handle {handle}");
            }
        }

        // 7) ah_invoke(Power, Current) — the worked-example read (§3.6): the proof plugin answers {"id":"proof"}.
        //    The response buffer is the plugin's and MUST be released with ah_free.
        {
            var response = CallInvoke(exports, handle, Capability.Power, Operation.Power.Current, "{}");
            Require(response == """{"id":"proof"}""", $"Power.Current returned '{response}', expected {{\"id\":\"proof\"}}");
            Step("ah_invoke(Power.Current)", response);
        }

        // 8) ah_dispose() — idempotent session teardown (§3.2). It does NOT unload the module (§2.3); the soak
        //    below needs the session, so this dispose is the smoke's own (a fresh session is created for the soak).
        exports.Dispose(handle);
        Step("ah_dispose", "smoke session disposed");

        if (soakMinutes > 0)
        {
            Soak(exports, soakMinutes);
        }
    }

    /// <summary>
    /// THE SOAK (§2.4): hold a live session and, for <paramref name="minutes"/> minutes, call
    /// <c>ah_invoke</c>/<c>ah_free</c> while allocating managed garbage on the HOST runtime and forcing GCs.
    /// This is the stress that surfaces the dotnet/runtime#121345 signal-handler-vs-GC deadlock: a hang fails
    /// the CI job by timeout, a crash fails it by exit code, and a clean completion is the pass. A progress line
    /// is printed every minute with elapsed time and iteration count.
    /// </summary>
    private static unsafe void Soak(VendorAbiExports exports, int minutes)
    {
        // A fresh session for the soak, so the long-lived handle is not one the smoke already disposed.
        ulong handle;
        {
            var desc = Utf8("""{"manufacturer":"Proof","product":"Proof Book","board":"","boardProduct":""}""");
            fixed (byte* pDesc = desc)
            {
                byte* manifest = null;
                var manifestLen = 0;
                var status = exports.Create(pDesc, desc.Length, &manifest, &manifestLen, &handle);
                Require(status == AbiStatus.Ok, $"soak ah_create returned status {status}, expected {AbiStatus.Ok}");
                if (manifest != null) exports.Free(manifest);
            }
        }

        var started = DateTime.UtcNow;
        var deadline = started.AddMinutes(minutes);
        var nextReport = started.AddMinutes(1);
        long iterations = 0;

        // A bounded churn list grows and trims to provoke Gen0/Gen1 collections on the host heap — the managed
        // half of the interaction. Each entry is 64 KiB so a few hundred MB/s flows through the GC.
        var churn = new List<byte[]>(capacity: 512);
        var request = Utf8("{}");

        Console.WriteLine($"SOAK: starting {minutes}-minute soak (invoke + GC churn every iteration).");
        fixed (byte* pReq = request)
        {
            while (DateTime.UtcNow < deadline)
            {
                byte* response = null;
                var responseLen = 0;
                var status = exports.Invoke(handle, Capability.Power, Operation.Power.Current, pReq, request.Length,
                    &response, &responseLen);
                Require(status == AbiStatus.Ok, $"soak ah_invoke returned status {status} (iteration {iterations})");
                if (response != null) exports.Free(response);

                churn.Add(new byte[64 * 1024]);
                if (churn.Count > 512) churn.RemoveAt(0);
                iterations++;

                // Force a collection periodically so the host GC actually suspends: the deadlock needs the host's
                // GC suspension to race the plugin runtime's signal handler, so we must not let the GC idle.
                if ((iterations & 0x3FFF) == 0) GC.Collect();

                if (DateTime.UtcNow >= nextReport)
                {
                    Console.WriteLine($"SOAK: elapsed {DateTime.UtcNow - started:hh\\:mm\\:ss}, iterations {iterations}, " +
                                      $"managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                    nextReport = nextReport.AddMinutes(1);
                }
            }
        }

        exports.Dispose(handle);
        Console.WriteLine($"SOAK: clean finish — {iterations} iterations in {DateTime.UtcNow - started:hh\\:mm\\:ss}.");
        Step("soak", "no hang, no crash");
    }

    // ---- ABI call helpers ----------------------------------------------------------------------------------

    private static unsafe string CallMatches(VendorAbiExports exports, string descriptorJson)
    {
        var desc = Utf8(descriptorJson);
        fixed (byte* pDesc = desc)
        {
            Span<byte> buffer = stackalloc byte[256];
            fixed (byte* pBuffer = buffer)
            {
                var outLen = 0;
                var status = exports.Matches(pDesc, desc.Length, pBuffer, buffer.Length, &outLen);
                Require(status == AbiStatus.Ok, $"ah_matches returned status {status}");
                Require(outLen is > 0 and <= 256, $"ah_matches reported an out-of-range length {outLen}");
                return System.Text.Encoding.UTF8.GetString(buffer[..outLen]);
            }
        }
    }

    private static unsafe string CallInvoke(VendorAbiExports exports, ulong handle, uint capability, uint op, string requestJson)
    {
        var request = Utf8(requestJson);
        fixed (byte* pRequest = request)
        {
            byte* response = null;
            var responseLen = 0;
            var status = exports.Invoke(handle, capability, op, pRequest, request.Length, &response, &responseLen);
            Require(status == AbiStatus.Ok, $"ah_invoke({capability},{op}) returned status {status}");
            Require(response != null && responseLen > 0, $"ah_invoke({capability},{op}) returned an empty response");
            var text = Marshal.PtrToStringUTF8((nint)response, responseLen) ?? string.Empty;
            exports.Free(response);
            return text;
        }
    }

    /// <summary>Calls the two-argument <c>ah_plugin_id</c> and decodes exactly the number of bytes it reports.
    /// The buffer is stack-allocated; no managed allocation crosses the boundary. The buffer size is a compile
    /// constant because stackalloc requires one.</summary>
    private static unsafe string ReadString(delegate* unmanaged[Cdecl]<byte*, int, int> pluginId)
    {
        const int cap = 64;
        Span<byte> buffer = stackalloc byte[cap];
        fixed (byte* pBuffer = buffer)
        {
            var wouldWrite = pluginId(pBuffer, cap);
            Require(wouldWrite is > 0 and <= cap, $"ah_plugin_id reported an out-of-range length {wouldWrite}");
            return System.Text.Encoding.UTF8.GetString(buffer[..wouldWrite]);
        }
    }

    private static unsafe nint Export(nint library, string name)
    {
        if (!NativeLibrary.TryGetExport(library, name, out var address) || address == 0)
            throw new SmokeFailure($"export '{name}' did not resolve — the plugin is missing part of the ABI contract");
        return address;
    }

    private static byte[] Utf8(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    // ---- Step / assertion plumbing -------------------------------------------------------------------------

    private static void Step(string name, string detail) => Console.WriteLine($"  [ok] {name}: {detail}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SmokeFailure(message);
    }

    /// <summary>A failed smoke assertion. Thrown rather than returned so the checks read linearly; the top-level
    /// catch turns it into a non-zero exit, which is what fails the CI job.</summary>
    private sealed class SmokeFailure(string message) : Exception(message);
}
