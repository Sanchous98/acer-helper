using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// NVIDIA discrete-GPU clock overclocking via NvAPI (nvapi64.dll) — the same mechanism G-Helper and MSI
/// Afterburner use: a signed core- and memory-clock offset written into the P0 (max-performance) state's
/// frequency delta, which shifts the whole voltage/frequency boost curve. NvAPI exposes exactly one real DLL
/// export, <c>nvapi_QueryInterface</c>, which resolves each function's pointer by a stable hex ID; those
/// pointers are then invoked through unmanaged function pointers (<c>delegate* unmanaged</c>) over blittable
/// structs — no runtime-generated marshalling and no COM, so it is Native-AOT-safe (unlike System.Management,
/// which is why this app hand-rolls all its native interop).
///
/// The offset is VOLATILE — the driver zeroes it on every reboot / driver reload / dGPU power-cycle (an
/// Optimus laptop dGPU going D3-cold), so the app is the source of truth: LaptopService persists the user's
/// choice per performance mode and re-applies it at startup, on resume, and on each mode switch. Writing the
/// offset needs the process elevated (the app already runs as admin for the EC/WMI controls).
///
/// Windows-only: nvapi64.dll ships with the NVIDIA driver and is simply absent on AMD/Intel-only laptops,
/// where <see cref="TryCreate"/> returns null and the UI hides the GPU section.
/// </summary>
internal sealed unsafe partial class NvidiaGpu : IGpuOverclock, IDisposable
{
    // ---- NvAPI function IDs (passed to nvapi_QueryInterface to resolve each real entry point) ----
    private const uint ID_Initialize       = 0x0150E828;
    private const uint ID_Unload           = 0xD22BDD7E;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GetFullName      = 0xCEEE8E9F;
    private const uint ID_GetPstates20     = 0x6FF81213;
    private const uint ID_SetPstates20     = 0x0F4DAE6B;

    // NV_GPU_PUBLIC_CLOCK_ID domains + the performance state we tune.
    private const uint CLOCK_GRAPHICS = 0;   // core
    private const uint CLOCK_MEMORY   = 4;
    private const uint PSTATE_P0      = 0;   // NVAPI_GPU_PERF_PSTATE_P0 (the only editable/meaningful state)

    // Safety caps on the exposed offset range (MHz), applied even if the driver reports more headroom — a
    // single slider drag to an extreme offset can hang or corrupt the GPU (NVIDIA XID 62). The memory value is
    // the RAW memory-clock offset, matching G-Helper's convention (it writes the number as-is, no GDDR6
    // doubling); an Afterburner "effective" figure is ~2× this.
    private const int CoreCap = 300;
    private const int MemCap  = 1500;

    // nvapi64.dll's single export. Blittable (uint -> nint), so the source-generated marshalling is AOT-safe.
    [LibraryImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    private static partial nint NvApiQueryInterface(uint id);

    // Resolved unmanaged entry points (process-global; resolved once).
    private static delegate* unmanaged[Cdecl]<int> _init;
    private static delegate* unmanaged[Cdecl]<int> _unload;
    private static delegate* unmanaged[Cdecl]<nint*, int*, int> _enum;
    private static delegate* unmanaged[Cdecl]<nint, byte*, int> _fullName;
    private static delegate* unmanaged[Cdecl]<nint, void*, int> _getPstates;
    private static delegate* unmanaged[Cdecl]<nint, void*, int> _setPstates;
    private static bool _resolved;   // true once ALL essential entry points resolved (see Resolve)

    private readonly nint _gpu;

    public string Name { get; }
    public (int Min, int Max) CoreRange { get; }
    public (int Min, int Max) MemRange { get; }
    public string? LastError { get; private set; }

    private NvidiaGpu(nint gpu, string name, (int, int) core, (int, int) mem)
    {
        _gpu = gpu;
        Name = name;
        CoreRange = core;
        MemRange = mem;
    }

    /// <summary>Probe for a controllable NVIDIA GPU. Returns null (feature hidden) when nvapi64.dll is absent
    /// (no NVIDIA driver — the common AMD/Intel-only case), NvAPI init fails, or no NVIDIA GPU is enumerated.
    /// Never throws — any failure degrades to "no GPU OC available".</summary>
    public static NvidiaGpu? TryCreate()
    {
        try
        {
            if (!Resolve() || _init() != 0) return null;

            nint* handles = stackalloc nint[64];   // NVAPI_MAX_PHYSICAL_GPUS
            int count = 0;
            if (_enum(handles, &count) != 0 || count <= 0)
            {
                try { if (_unload != null) _unload(); } catch { /* best effort */ }
                return null;
            }

            var gpu = handles[0];
            var name = ReadName(gpu);
            var (core, mem) = ReadRanges(gpu);
            return new NvidiaGpu(gpu, name, core, mem);
        }
        catch { return null; }   // DllNotFound (no NVIDIA driver), bad entry point, etc. -> feature hidden
    }

    /// <summary>Apply a core + memory clock offset (MHz) to P0. Each value is clamped to its allowed range,
    /// converted to kHz, and written in one SetPstates20 call. The memory value is the raw memory-clock offset
    /// (G-Helper convention), not the doubled "effective" figure.</summary>
    public bool Set(int coreMhz, int memMhz)
    {
        LastError = null;
        if (_setPstates == null) { LastError = "NvAPI unavailable"; return false; }

        coreMhz = Math.Clamp(coreMhz, CoreRange.Min, CoreRange.Max);
        memMhz = Math.Clamp(memMhz, MemRange.Min, MemRange.Max);

        NV_GPU_PERF_PSTATES20_INFO_V1 set = default;
        set.version = Ver1;
        set.numPstates = 1;
        set.numClocks = 2;
        set.numBaseVoltages = 0;
        set.pstates[0].pstateId = PSTATE_P0;
        set.pstates[0].clocks[0].domainId = CLOCK_GRAPHICS;
        set.pstates[0].clocks[0].typeId = 0;                       // single frequency
        set.pstates[0].clocks[0].freqDelta_kHz.value = coreMhz * 1000;
        set.pstates[0].clocks[1].domainId = CLOCK_MEMORY;
        set.pstates[0].clocks[1].typeId = 0;
        set.pstates[0].clocks[1].freqDelta_kHz.value = memMhz * 1000;

        int st = _setPstates(_gpu, &set);
        if (st != 0) { LastError = $"NvAPI error {st}"; return false; }
        return true;
    }

    public void Dispose()
    {
        try { if (_unload != null) _unload(); } catch { /* best effort */ }
    }

    // ---- helpers ----

    // MAKE_NVAPI_VERSION(NV_GPU_PERF_PSTATES20_INFO_V1, 1) = sizeof(struct) | (versionNumber << 16). Computed
    // from our own struct size so it can't drift from the layout — the driver validates version == its own
    // sizeof|ver, so the layout below MUST be byte-identical to NVIDIA's V1 (it is; total 0x1C94).
    private static uint Ver1 => (uint)Unsafe.SizeOf<NV_GPU_PERF_PSTATES20_INFO_V1>() | (1u << 16);

    private static bool Resolve()
    {
        if (_resolved) return true;
        var init = (delegate* unmanaged[Cdecl]<int>)NvApiQueryInterface(ID_Initialize);
        if (init == null) return false;
        _init = init;
        _unload     = (delegate* unmanaged[Cdecl]<int>)NvApiQueryInterface(ID_Unload);
        _enum       = (delegate* unmanaged[Cdecl]<nint*, int*, int>)NvApiQueryInterface(ID_EnumPhysicalGPUs);
        _fullName   = (delegate* unmanaged[Cdecl]<nint, byte*, int>)NvApiQueryInterface(ID_GetFullName);
        _getPstates = (delegate* unmanaged[Cdecl]<nint, void*, int>)NvApiQueryInterface(ID_GetPstates20);
        _setPstates = (delegate* unmanaged[Cdecl]<nint, void*, int>)NvApiQueryInterface(ID_SetPstates20);
        // Cache "fully resolved" only when ALL essential entry points came back — never key the short-circuit
        // off a single pointer (SetPstates20 can resolve while an older one didn't), which would let a later
        // call return true with a null _enum/_getPstates and fault on the next invoke.
        _resolved = _enum != null && _getPstates != null && _setPstates != null;
        return _resolved;
    }

    private static string ReadName(nint gpu)
    {
        if (_fullName == null) return "NVIDIA GPU";
        byte* buf = stackalloc byte[64];   // NvAPI_ShortString (ASCII, 64 bytes)
        if (_fullName(gpu, buf) != 0) return "NVIDIA GPU";
        var s = Marshal.PtrToStringAnsi((nint)buf);
        return string.IsNullOrWhiteSpace(s) ? "NVIDIA GPU" : s!;
    }

    private static ((int, int) core, (int, int) mem) ReadRanges(nint gpu)
    {
        var (cMin, cMax, mMin, mMax) = ReadDeltaRange(gpu);
        return (Cap(cMin, cMax, CoreCap), Cap(mMin, mMax, MemCap));
    }

    // Intersect the driver-reported range with our safety cap. A degenerate read (0..0 — e.g. the dGPU was
    // powered off / D3-cold at probe time, common on Optimus laptops) falls back to the full ±cap envelope so
    // the feature still appears with sane bounds instead of a dead 0..0 slider.
    private static (int Min, int Max) Cap(int dmin, int dmax, int cap)
        => dmax <= 0 && dmin >= 0 ? (-cap, cap) : (Math.Max(dmin, -cap), Math.Min(dmax, cap));

    private static (int cMin, int cMax, int mMin, int mMax) ReadDeltaRange(nint gpu)
    {
        if (_getPstates == null) return (0, 0, 0, 0);
        NV_GPU_PERF_PSTATES20_INFO_V1 info = default;
        info.version = Ver1;
        if (_getPstates(gpu, &info) != 0) return (0, 0, 0, 0);

        int cMin = 0, cMax = 0, mMin = 0, mMax = 0;
        uint np = Math.Min(info.numPstates, 16u);
        for (uint p = 0; p < np; p++)
        {
            if (info.pstates[(int)p].pstateId != PSTATE_P0) continue;
            uint nc = Math.Min(info.numClocks, 8u);
            for (uint c = 0; c < nc; c++)
            {
                var clk = info.pstates[(int)p].clocks[(int)c];
                int mn = clk.freqDelta_kHz.mindelta / 1000;   // kHz -> MHz
                int mx = clk.freqDelta_kHz.maxdelta / 1000;
                if (clk.domainId == CLOCK_GRAPHICS) { cMin = mn; cMax = mx; }
                else if (clk.domainId == CLOCK_MEMORY) { mMin = mn; mMax = mx; }
            }
            break;
        }
        return (cMin, cMax, mMin, mMax);
    }

    // ---- NvAPI structs (nvapi.h NV_GPU_PERF_PSTATES20_INFO_V1 and nested; must be byte-identical) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_PERF_PSTATES20_PARAM_DELTA
    {
        public int value;      // the delta itself, in kHz (freq) / uV (volt)
        public int mindelta;   // valueRange.min — driver-reported allowed low (kHz/uV)
        public int maxdelta;   // valueRange.max — driver-reported allowed high
    }

    // The clock entry's trailing 20-byte union (single-freq vs range/curve). We only touch freqDelta_kHz, but
    // the driver writes the full union on a read, so the region MUST be present or GetPstates20 overruns the
    // buffer. Kept opaque via an explicit size.
    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct ClockDataUnion { }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_PSTATE20_CLOCK_ENTRY_V1
    {
        public uint domainId;                                 // NV_GPU_PUBLIC_CLOCK_ID
        public uint typeId;                                   // 0 = single freq, 1 = range
        public uint bIsEditableBits;                          // bIsEditable:1 + reserved:31
        public NV_GPU_PERF_PSTATES20_PARAM_DELTA freqDelta_kHz;
        private ClockDataUnion _data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_PSTATE20_BASE_VOLTAGE_ENTRY_V1
    {
        public uint domainId;
        public uint bIsEditableBits;
        public uint volt_uV;
        public NV_GPU_PERF_PSTATES20_PARAM_DELTA voltDelta_uV;
    }

    [InlineArray(8)]   // NVAPI_MAX_GPU_PSTATE20_CLOCKS
    private struct ClockArray { private NV_GPU_PSTATE20_CLOCK_ENTRY_V1 _e; }

    [InlineArray(4)]   // NVAPI_MAX_GPU_PSTATE20_BASE_VOLTAGES
    private struct BaseVoltArray { private NV_GPU_PSTATE20_BASE_VOLTAGE_ENTRY_V1 _e; }

    [InlineArray(16)]  // NVAPI_MAX_GPU_PSTATE20_PSTATES
    private struct PstateArray { private NV_GPU_PERF_PSTATES20_PSTATE _e; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_PERF_PSTATES20_PSTATE
    {
        public uint pstateId;                 // NV_GPU_PERF_PSTATE_ID (P0 = 0)
        public uint reserved;
        public ClockArray clocks;
        public BaseVoltArray baseVoltages;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_PERF_PSTATES20_INFO_V1
    {
        public uint version;
        public uint bIsEditableBits;          // bIsEditable:1 + reserved:31
        public uint numPstates;
        public uint numClocks;
        public uint numBaseVoltages;
        public PstateArray pstates;
    }
}
