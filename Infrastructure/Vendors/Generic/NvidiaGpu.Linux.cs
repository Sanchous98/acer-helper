using System.Runtime.InteropServices;
using System.Text;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE LINUX TRANSPORT FOR THE GPU CLOCK-OFFSET AXIS, AND NOTHING ELSE: this file owns the library, the P/Invoke
// declarations, the device handle and the probe. Every rule — the availability gate's decision, the safety caps,
// the clamping, the write/confirm ORDER and the NVML return-code vocabulary — lives in NvidiaGpuPolicy.cs (an
// UN-SUFFIXED file) because this one cannot be compiled by the test project: AcerHelper.csproj's <Compile Remove>
// keeps **/*.Linux.cs out of the test TFM. If a rule is worth proving, it does not belong here.
//
// WHY NVML AND NOT NVAPI. The Windows port reaches the same axis through nvapi64.dll, whose single export
// (nvapi_QueryInterface) resolves every function by a stable hex ID and needs hand-built blittable structs to
// match NVIDIA's. On Linux there is no NvAPI: NVIDIA ships libnvidia-ml.so.1 ("NVML") with the driver, and its
// entry points are PLAIN C EXPORTS with ordinary signatures. So this half is an order of magnitude simpler than
// its Windows twin — no ID table, no struct-layout contract, no version word — and the axis is the same one:
// nvmlDeviceGet/SetGpcClkVfOffset and …MemClkVfOffset write the GPC/memory VF-curve delta, which is the parameter
// NvAPI's P0 freqDelta_kHz writes (docs/nvidia-gpu-oc.md).
//
// THE SYMBOLS HERE WERE VERIFIED AGAINST THIS MACHINE'S LIBRARY, NOT ASSUMED FROM DOCUMENTATION. All eleven of
// them exist in /usr/lib64/libnvidia-ml.so.615.71.09 (SONAME libnvidia-ml.so.1, driver 615.71.09, NVML
// 13.615.71.09) and the read half was actually CALLED — see docs/nvidia-gpu-oc-linux.md for the exact return
// codes, the ranges read off the device, and the two NVML families that were checked and REJECTED.
//
// A NOTE ON UNITS, because it is the one place the two OS ports genuinely differ: NVML's offsets are in MHz on
// both sides — the getters, the setters and the range getters all take and return a plain int MHz. NvAPI's P0
// delta is in kHz and the Windows port divides by 1000 on the way out. There is deliberately no such arithmetic
// here; adding it would make the port ask for a thousandth of what it displays.

/// <summary>
/// NVIDIA dGPU clock overclocking on Linux through NVML (<c>libnvidia-ml.so.1</c>) — the Linux half of the axis
/// the Windows port drives through NvAPI. The mechanism, its volatility and the app's re-apply duty are identical
/// (docs/nvidia-gpu-oc.md); only the transport differs.
///
/// This type is the transport and the probe: it loads the library, initialises NVML, takes device 0's handle, and
/// hands the three call shapes to <see cref="NvidiaGpuPolicy"/>, which owns every rule and every number. It is a
/// thin forwarder to that policy for the same reason the Windows port is a fat one: what must be testable is in
/// the policy, and what must not be in a test project is here.
///
/// It keeps the SAME SURFACE as its Windows twin — <c>Name</c>, <c>CoreRange</c>, <c>MemRange</c>, <c>LastError</c>,
/// <c>Set</c>, <c>TryCreate</c>, <c>Dispose</c>, <see cref="IGpuOverclock"/> — so the wiring site and the UI are
/// identical on both OSes and neither needed a change to accommodate this port.
/// </summary>
internal sealed unsafe partial class NvidiaGpu : IGpuOverclock, IDisposable
{
    /// <summary>The SONAME the driver's userspace package installs, resolved through ldconfig. Versioned on
    /// purpose: <c>libnvidia-ml.so</c> (unversioned) is a development symlink that a stock driver package does not
    /// have to ship, while <c>.so.1</c> is what the driver's own tools link against.</summary>
    private const string Library = "libnvidia-ml.so.1";

    /// <summary>NVML_DEVICE_NAME_V2_BUFFER_SIZE — the buffer <c>nvmlDeviceGetName</c> writes into. 96 is NVML's own
    /// constant, and the name is truncated to fit rather than grown.</summary>
    private const int DeviceNameBufferSize = 96;

    private readonly NvidiaGpuPolicy _policy;

    private NvidiaGpu(NvidiaGpuPolicy policy) => _policy = policy;

    /// <summary>
    /// Probe for a controllable NVIDIA dGPU. Returns null — the feature is hidden and the UI drops the GPU section
    /// — when the library is not installed at all (no NVIDIA driver: the common AMD/Intel-only laptop), when NVML
    /// will not initialise, when NO DEVICE IS VISIBLE, or when any of the three call shapes is missing. Never
    /// throws; every failure degrades to "no GPU OC here".
    ///
    /// "NO DEVICE IS VISIBLE" IS THE CASE THAT MATTERS ON THIS MACHINE, and it is deliberately not treated as "this
    /// computer has no GPU". This laptop's dGPU is handed out by an access broker, so a composition that runs
    /// without the grant sees <c>nvmlDeviceGetCount_v2</c> answer zero — the same answer a machine with no dGPU at
    /// all gives. The port cannot tell the two apart and must not try: it returns null either way, and because the
    /// probe holds NO state between runs, the section appears on the next composition the moment the device becomes
    /// visible. That is the whole reason the gate lives in the policy as a per-run decision rather than as a
    /// once-and-for-all "is there a GPU" flag resolved at startup.
    ///
    /// The probe never writes. The only NVML calls it makes are init, count, handle, name and the two range reads
    /// the policy asks for — no offset, no clock and no persistence mode is ever set here.
    /// </summary>
    public static NvidiaGpu? TryCreate()
    {
        try
        {
            // The first NVML call doubles as the library probe: on a machine with no NVIDIA driver there is no
            // libnvidia-ml.so.1 to load, and the generated P/Invoke throws DllNotFoundException before
            // nvmlInit_v2 is entered. One call answers "is the library here" and "will it initialise".
            var initialized = NvmlInit() == NvidiaGpuPolicy.NvmlSuccess;

            var visibleDevices = 0;
            string? name = null;
            nint device = 0;

            if (initialized
                && NvmlDeviceGetCount(out var count) == NvidiaGpuPolicy.NvmlSuccess
                && count > 0
                && NvmlDeviceGetHandleByIndex(0, out device) == NvidiaGpuPolicy.NvmlSuccess)
            {
                visibleDevices = (int)Math.Min(count, int.MaxValue);
                name = ReadName(device);
            }

            var policy = NvidiaGpuPolicy.TryCreate(
                libraryLoaded: true,
                initialized: initialized,
                visibleDevices: visibleDevices,
                name: name,
                readOffset: domain => ReadOffset(device, domain),
                readRange: domain => ReadRange(device, domain),
                writeOffset: (domain, mhz) => WriteOffset(device, domain, mhz),
                // Its int return is NVML's own verdict, and there is nothing useful to do with a library that
                // refuses to unload while the process is exiting — the policy's Dispose swallows it by design.
                release: () => _ = NvmlShutdown());

            return policy == null ? null : new NvidiaGpu(policy);
        }
        catch (DllNotFoundException) { return null; }       // no NVIDIA driver — the common non-NVIDIA machine
        catch (EntryPointNotFoundException) { return null; } // an older library without the VfOffset family
        catch { return null; }                              // never let a probe take the UI thread down
    }

    // ---- IGpuOverclock: every member is the policy's ----

    public string Name => _policy.Name;
    public (int Min, int Max) CoreRange => _policy.CoreRange;
    public (int Min, int Max) MemRange => _policy.MemRange;
    public string? LastError => _policy.LastError;
    public bool Set(int coreMhz, int memMhz) => _policy.Set(coreMhz, memMhz);

    /// <summary>Shut NVML down. Best effort, and it releases only the library's own state — no offset, clock or
    /// power setting is touched on the way out, so a disposed port leaves the driver exactly as it found it.
    /// (Windows' NvAPI_Unload is the analogous call.)</summary>
    public void Dispose() => _policy.Dispose();

    // ---- the three call shapes, bound to NVML entry points ----
    // NVML splits the axis across four symbols rather than taking a clock-type argument, so the dispatch on the
    // policy's domain enum happens exactly here and nowhere else.

    private static NvidiaGpuPolicy.NvmlResult ReadOffset(nint device, NvidiaGpuPolicy.OffsetDomain domain)
    {
        var rc = domain == NvidiaGpuPolicy.OffsetDomain.Memory
            ? NvmlGetMemClkVfOffset(device, out var value)
            : NvmlGetGpcClkVfOffset(device, out value);
        // A non-success code is reported AS the code, with no value: NVML is not obliged to write the
        // out-parameter on failure, so reading it would be reading garbage and calling it an offset.
        return rc == NvidiaGpuPolicy.NvmlSuccess
            ? new NvidiaGpuPolicy.NvmlResult(rc, value)
            : NvidiaGpuPolicy.NvmlResult.Failure(rc);
    }

    private static NvidiaGpuPolicy.RangeResult ReadRange(nint device, NvidiaGpuPolicy.OffsetDomain domain)
    {
        var rc = domain == NvidiaGpuPolicy.OffsetDomain.Memory
            ? NvmlGetMemClkMinMaxVfOffset(device, out var min, out var max)
            : NvmlGetGpcClkMinMaxVfOffset(device, out min, out max);
        return rc == NvidiaGpuPolicy.NvmlSuccess
            ? new NvidiaGpuPolicy.RangeResult(rc, min, max)
            : NvidiaGpuPolicy.RangeResult.Failure(rc);
    }

    private static int WriteOffset(nint device, NvidiaGpuPolicy.OffsetDomain domain, int mhz) =>
        domain == NvidiaGpuPolicy.OffsetDomain.Memory
            ? NvmlSetMemClkVfOffset(device, mhz)
            : NvmlSetGpcClkVfOffset(device, mhz);

    /// <summary>The device's name — what the UI puts in the section header. Null when the driver will not answer,
    /// which the policy renders as its own default rather than as an empty header.</summary>
    private static string? ReadName(nint device)
    {
        var buffer = new byte[DeviceNameBufferSize];
        fixed (byte* p = buffer)
        {
            if (NvmlDeviceGetName(device, p, DeviceNameBufferSize) != NvidiaGpuPolicy.NvmlSuccess) return null;
        }
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    // ---- NVML entry points ----
    // LibraryImport rather than DllImport, matching AcerEcHidController.Linux.cs: the signatures below are all
    // blittable (ints, an opaque handle, a byte buffer), so the source generator emits direct calls with no
    // runtime marshalling — which is what keeps the port Native-AOT-safe, the same constraint that forces the
    // Windows half's hand-rolled NvAPI interop and its WbemInterop.
    //
    // nvmlDevice_t is an opaque pointer; nint carries it without pretending to know its shape.

    [LibraryImport(Library, EntryPoint = "nvmlInit_v2")]
    private static partial int NvmlInit();

    [LibraryImport(Library, EntryPoint = "nvmlShutdown")]
    private static partial int NvmlShutdown();

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
    private static partial int NvmlDeviceGetCount(out uint deviceCount);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetName")]
    private static partial int NvmlDeviceGetName(nint device, byte* name, uint length);

    // The offset axis: one getter, one setter and one range getter per domain. These six are the whole feature —
    // and the reason the range getters are not optional is that they are where the slider's bounds come from.

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetGpcClkVfOffset")]
    private static partial int NvmlGetGpcClkVfOffset(nint device, out int offsetMhz);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceSetGpcClkVfOffset")]
    private static partial int NvmlSetGpcClkVfOffset(nint device, int offsetMhz);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetMemClkVfOffset")]
    private static partial int NvmlGetMemClkVfOffset(nint device, out int offsetMhz);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceSetMemClkVfOffset")]
    private static partial int NvmlSetMemClkVfOffset(nint device, int offsetMhz);

    // nvmlDeviceGetGpcClkMinMaxVfOffset(nvmlDevice_t device, int *minGpcClkVfOffset, int *maxGpcClkVfOffset) —
    // TWO separate int out-parameters, not a struct. (Getting this wrong is not academic: passed a struct pointer,
    // the second argument is written through an address the caller did not provide.)
    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetGpcClkMinMaxVfOffset")]
    private static partial int NvmlGetGpcClkMinMaxVfOffset(nint device, out int minMhz, out int maxMhz);

    [LibraryImport(Library, EntryPoint = "nvmlDeviceGetMemClkMinMaxVfOffset")]
    private static partial int NvmlGetMemClkMinMaxVfOffset(nint device, out int minMhz, out int maxMhz);
}
