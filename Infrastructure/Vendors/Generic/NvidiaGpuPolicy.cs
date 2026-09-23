using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

// THE GPU CLOCK-OFFSET POLICY, IN AN UN-SUFFIXED FILE, for the reason AcerFanPort.cs and CurveOptimizerPolicy.cs
// state at length: the test project targets net10.0-windows while AcerHelper.csproj's <Compile Remove> keeps
// **/*.Linux.cs out of that TFM, so policy left in NvidiaGpu.Linux.cs cannot be reached by the suite at all. What
// stays in the OS files is I/O: NvAPI function pointers on Windows, NVML entry points on Linux.
//
// IT IS THE AXIS'S SHARED POLICY, NOT THE LINUX ONE'S, and the Windows port consumes the two rules below
// (<see cref="Cap"/> and <see cref="ClampOffset"/>) rather than carrying a private copy. That is a decision: the
// caps are a SAFETY rule whose whole purpose is that a slider drag cannot reach an extreme offset (NVIDIA XID 62),
// and two OS-local copies of a safety rule are exactly the thing that drifts while both look correct.
//
// UNITS: NVML speaks MHz on BOTH sides of this axis — nvmlDeviceGet/SetGpcClkVfOffset and …MemClkVfOffset take and
// return a plain `int` in MHz. There is therefore NO kHz conversion here, unlike the Windows port, whose NvAPI
// NV_GPU_PERF_PSTATES20_PARAM_DELTA is in kHz and has to divide by 1000 on the way out of the driver; the tests
// pin the literal MHz values that reach the transport.
//
// See docs/nvidia-gpu-oc.md (the Windows mechanism, measured on this machine) and docs/nvidia-gpu-oc-linux.md
// (this port: the symbols verified against libnvidia-ml 615.71.09, the ranges read off the device, and what
// remains unverifiable).

/// <summary>
/// The NVIDIA dGPU clock-offset axis, expressed as rules a test can drive through injected I/O. The axis is the
/// same one the Windows port drives: a signed core- and memory-clock offset layered on the P0 max-performance
/// state's frequency delta. On Linux the same numbers are reached through NVML (<c>libnvidia-ml.so.1</c>) instead
/// of NvAPI.
///
/// VOLATILE, exactly as on Windows: the offset is a runtime RM/driver parameter and not firmware, so a driver
/// reload, a module reload or the dGPU dropping to D3-cold zeroes it. The app is therefore the source of truth —
/// LaptopService persists the pair per performance mode and re-applies it at startup, on resume and on every mode
/// switch, all through <see cref="IGpuOverclock.Set"/>.
///
/// PRESENT ONLY WHERE IT CAN ACT. <see cref="TryCreate"/> returns null — the port stays null and the UI hides the
/// GPU section — unless the library loaded, NVML initialised, at least one device is VISIBLE, and the offset APIs
/// resolved. Probe, never require: no failure throws.
/// </summary>
internal sealed class NvidiaGpuPolicy : IGpuOverclock
{
    /// <summary>Shown when the driver will not name the part. The UI puts <see cref="Name"/> in the section
    /// header, so it can never be empty.</summary>
    internal const string DefaultName = "NVIDIA GPU";

    // ---- safety caps on the exposed offset range (MHz) ----
    // Applied EVEN WHERE THE DRIVER OFFERS MORE: one slider drag to an extreme offset can hang or corrupt the GPU
    // (NVIDIA XID 62). It is not theoretical on this side — the memory range this machine's driver reports is
    // -2000..+6000 MHz, four times the cap.
    //
    // The memory value is the RAW memory-clock offset, matching G-Helper's convention (the number as written, with
    // no GDDR6/GDDR7 doubling); an Afterburner "effective" figure is ~2× this. Same convention as Windows, so the
    // two OSes show the same number for the same physical offset.
    internal const int CoreCap = 300;
    internal const int MemCap = 1500;

    // ---- NVML return codes this axis actually meets ----
    // Named rather than inlined because the NUMBER is what the transport hands back and the MEANING is what the
    // user reads: NVML_ERROR_NO_PERMISSION in particular decides whether the feature can work from an
    // unprivileged app at all (see docs/nvidia-gpu-oc-linux.md).
    internal const int NvmlSuccess = 0;
    internal const int NvmlUninitialized = 1;
    internal const int NvmlInvalidArgument = 2;
    internal const int NvmlNotSupported = 3;
    internal const int NvmlNoPermission = 4;
    internal const int NvmlNotFound = 6;
    internal const int NvmlDriverNotLoaded = 9;
    internal const int NvmlFreqNotSupported = 24;
    internal const int NvmlArgumentVersionMismatch = 25;

    /// <summary>Which of the axis's two offsets a call is about. NVML splits them across four entry points
    /// (<c>…GpcClkVfOffset</c> / <c>…MemClkVfOffset</c>) rather than taking a clock-type argument, so the transport
    /// is the side that maps this onto the right symbol.</summary>
    internal enum OffsetDomain
    {
        /// <summary>The graphics/core clock — NvAPI's <c>CLOCK_GRAPHICS</c>, NVML's GPC clock.</summary>
        Core = 0,
        /// <summary>The memory clock — NvAPI's <c>CLOCK_MEMORY</c>.</summary>
        Memory = 1,
    }

    /// <summary>One NVML call's verdict: the return code, plus whatever the call's out-parameter carried when it
    /// succeeded. Both halves travel together because a transport that dropped the code could not tell a refusal
    /// from a zero.</summary>
    internal readonly record struct NvmlResult(int Code, int Value)
    {
        internal bool Ok => Code == NvmlSuccess;
        internal static NvmlResult Failure(int code) => new(code, 0);
        internal static NvmlResult Value_(int value) => new(NvmlSuccess, value);
    }

    /// <summary>The verdict of a min/max range read — the two out-parameters the <c>…ClkMinMaxVfOffset</c> pair
    /// carries, plus the code, in one value.</summary>
    internal readonly record struct RangeResult(int Code, int Min, int Max)
    {
        internal bool Ok => Code == NvmlSuccess;
        internal static RangeResult Failure(int code) => new(code, 0, 0);
    }

    /// <summary>The axis's two allowed ranges, after the caps above have been applied. Held as one value so the
    /// pair cannot be assembled from two reads taken a probe apart.</summary>
    internal readonly record struct OffsetRanges((int Min, int Max) Core, (int Min, int Max) Mem);

    // ---- the three calls the policy makes, injected ----
    // Each is the caller's: the Linux transport binds them to NVML entry points, a test binds them to a recording
    // fake. They mirror the three SHAPES NVML offers rather than one generic accessor, because a read and a write
    // are different permissions.

    /// <summary>Read one offset (MHz). Backed by <c>nvmlDeviceGetGpcClkVfOffset</c> /
    /// <c>nvmlDeviceGetMemClkVfOffset</c>.</summary>
    internal delegate NvmlResult ReadOffsetDelegate(OffsetDomain domain);

    /// <summary>Read one offset's allowed range (MHz). Backed by <c>nvmlDeviceGetGpcClkMinMaxVfOffset</c> /
    /// <c>nvmlDeviceGetMemClkMinMaxVfOffset</c>.</summary>
    internal delegate RangeResult ReadRangeDelegate(OffsetDomain domain);

    /// <summary>Write one offset (MHz), returning NVML's raw code. Backed by
    /// <c>nvmlDeviceSetGpcClkVfOffset</c> / <c>nvmlDeviceSetMemClkVfOffset</c>.</summary>
    internal delegate int WriteOffsetDelegate(OffsetDomain domain, int mhz);

    private readonly ReadOffsetDelegate _read;
    private readonly WriteOffsetDelegate _write;
    private readonly Action? _release;

    public string Name { get; }
    public (int Min, int Max) CoreRange { get; }
    public (int Min, int Max) MemRange { get; }
    public string? LastError { get; private set; }

    private NvidiaGpuPolicy(string? name, OffsetRanges ranges, ReadOffsetDelegate read, WriteOffsetDelegate write,
        Action? release)
    {
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name!;
        CoreRange = ranges.Core;
        MemRange = ranges.Mem;
        _read = read;
        _write = write;
        _release = release;
    }

    /// <summary>
    /// Probe for a tunable NVIDIA dGPU on this machine. Returns null — the feature is hidden — unless the library
    /// loaded, NVML initialised, a device is visible, all three call shapes resolved, and BOTH range reads
    /// succeeded.
    ///
    /// NEVER THROWS AND NEVER WRITES: composition runs on the UI thread and a probe that touched the hardware would
    /// be a side effect nobody asked for, so the only calls this method makes are the two range reads.
    ///
    /// "NO DEVICE" IS NOT "NO GPU": a hidden device makes <paramref name="visibleDevices"/> zero and this returns
    /// null, and the gate is re-evaluated on the next composition.
    /// </summary>
    internal static NvidiaGpuPolicy? TryCreate(
        bool libraryLoaded,
        bool initialized,
        int visibleDevices,
        string? name,
        ReadOffsetDelegate? readOffset,
        ReadRangeDelegate? readRange,
        WriteOffsetDelegate? writeOffset,
        Action? release = null)
    {
        if (!Available(libraryLoaded, initialized, visibleDevices,
                       readOffset != null, readRange != null, writeOffset != null))
            return null;

        var core = readRange!(OffsetDomain.Core);
        var mem = readRange(OffsetDomain.Memory);
        if (!core.Ok || !mem.Ok) return null;   // no readable range -> no honest slider -> no axis

        return new NvidiaGpuPolicy(name, RangesFor(core.Min, core.Max, mem.Min, mem.Max),
            readOffset!, writeOffset!, release);
    }

    /// <summary>The availability gate's whole decision, as one predicate: each term is a different machine (no
    /// driver at all / a library that will not initialise / a hidden or absent dGPU / a driver too old to know the
    /// VfOffset family). Separate from <see cref="TryCreate"/> so the decision table is testable on its own.</summary>
    internal static bool Available(
        bool libraryLoaded, bool initialized, int visibleDevices,
        bool readOffsetResolved, bool readRangeResolved, bool writeOffsetResolved)
        => libraryLoaded
        && initialized
        && visibleDevices > 0
        && readOffsetResolved
        && readRangeResolved
        && writeOffsetResolved;

    /// <summary>Apply a core + memory offset (MHz). Both are clamped to the capped ranges, written, and then READ
    /// BACK; a true result means the driver reported holding the values this call asked for, not merely that it
    /// accepted the writes.
    ///
    /// THE ORDER IS CORE, THEN MEMORY, THEN BOTH READ-BACKS, and the write half fails FAST: if the core write is
    /// refused, the memory write is never attempted, because the first refusal is the informative one and nothing
    /// is gained by leaving the pair half applied for a driver that already said no.
    ///
    /// A READ-BACK THAT FAILS IS A FAILURE: both getters resolved at probe time, so a getter that stops answering
    /// means the transport broke between the write and the confirmation, and reporting success there would hand
    /// the user a number the app cannot vouch for.
    /// </summary>
    public bool Set(int coreMhz, int memMhz)
    {
        LastError = null;

        var core = ClampOffset(coreMhz, CoreRange);
        var mem = ClampOffset(memMhz, MemRange);

        var rc = _write(OffsetDomain.Core, core);
        if (rc != NvmlSuccess) { LastError = Describe(rc, "core"); return false; }

        rc = _write(OffsetDomain.Memory, mem);
        if (rc != NvmlSuccess) { LastError = Describe(rc, "memory"); return false; }

        // Both are confirmed even if the first disagrees: the memory write has already happened, so its
        // verification is not optional.
        var coreHeld = Confirm(OffsetDomain.Core, core, "core");
        var memHeld = Confirm(OffsetDomain.Memory, mem, "memory");
        return coreHeld && memHeld;
    }

    /// <summary>Release the library handle. Best effort and never throwing: the process is on its way out and a
    /// driver that has already gone away must not turn disposal into an exception.</summary>
    public void Dispose()
    {
        try { _release?.Invoke(); } catch { /* best effort */ }
    }

    // ---- the rules ----

    /// <summary>Intersect a driver-reported range with a safety cap.
    ///
    /// A DEGENERATE READ FALLS BACK TO THE FULL ±cap ENVELOPE: a driver that answers 0..0 is not saying "no offset
    /// is allowed", it is not answering usefully — on an Optimus laptop that is what a dGPU which is powered off /
    /// D3-cold at probe time looks like — and a literal reading would leave the user a dead 0..0 slider.
    ///
    /// AN EMPTY INTERSECTION COLLAPSES TO 0..0 RATHER THAN THROWING. A driver range lying entirely above the cap
    /// (say +400..+1000 MHz against a 300 cap) has nothing safe to offer; returning a crossed pair instead would
    /// reach <see cref="Math.Clamp(int,int,int)"/>, which THROWS on Min &gt; Max — the difference between a hidden
    /// slider and an exception on the UI thread.
    /// </summary>
    internal static (int Min, int Max) Cap(int driverMin, int driverMax, int cap)
    {
        if (driverMax <= 0 && driverMin >= 0) return (-cap, cap);   // degenerate 0..0 read
        var min = Math.Max(driverMin, -cap);
        var max = Math.Min(driverMax, cap);
        return min > max ? (0, 0) : (min, max);                     // empty intersection -> stock only
    }

    /// <summary>Both ranges, capped. One entry point so the core and memory caps cannot be applied to the wrong
    /// pair of numbers at a call site.</summary>
    internal static OffsetRanges RangesFor(int coreMin, int coreMax, int memMin, int memMax)
        => new(Cap(coreMin, coreMax, CoreCap), Cap(memMin, memMax, MemCap));

    /// <summary>Bring one requested offset inside its allowed range. <see cref="Cap"/> guarantees Min ≤ Max, which
    /// <see cref="Math.Clamp(int,int,int)"/> requires.</summary>
    internal static int ClampOffset(int mhz, (int Min, int Max) range) => Math.Clamp(mhz, range.Min, range.Max);

    /// <summary>Whether a written offset is the one the driver reports. One place, so the confirmation rule cannot
    /// differ between the two domains.</summary>
    internal static bool Held(int asked, int reported) => asked == reported;

    /// <summary>NVML's verdict as something the UI can show. The permission case is spelled out because it is the
    /// one a user can act on, and because it is the case this axis actually meets on a stock desktop Linux
    /// install (see docs/nvidia-gpu-oc-linux.md).</summary>
    internal static string Describe(int code, string what) => code switch
    {
        NvmlNoPermission =>
            $"Setting the {what} clock offset needs hardware access NVML refused (NVML_ERROR_NO_PERMISSION, {code}) — "
            + "grant the app's hardware-access install, or run it as root.",
        NvmlNotSupported or NvmlFreqNotSupported =>
            $"The driver does not support the {what} clock offset on this GPU (NVML_ERROR_NOT_SUPPORTED, {code}).",
        NvmlNotFound =>
            $"No NVIDIA GPU is visible to NVML, so the {what} clock offset cannot be set "
            + $"(NVML_ERROR_NOT_FOUND, {code}).",
        NvmlDriverNotLoaded =>
            $"The NVIDIA driver is not loaded, so the {what} clock offset cannot be set "
            + $"(NVML_ERROR_DRIVER_NOT_LOADED, {code}).",
        NvmlUninitialized =>
            $"NVML is not initialised, so the {what} clock offset cannot be set "
            + $"(NVML_ERROR_UNINITIALIZED, {code}).",
        NvmlInvalidArgument =>
            $"The driver rejected the {what} clock offset as out of range (NVML_ERROR_INVALID_ARGUMENT, {code}).",
        NvmlArgumentVersionMismatch =>
            $"The driver rejected the {what} clock-offset request's structure version "
            + $"(NVML_ERROR_ARGUMENT_VERSION_MISMATCH, {code}).",
        _ => $"The driver refused the {what} clock offset (NVML error {code}).",
    };

    // ---- helpers ----

    private bool Confirm(OffsetDomain domain, int asked, string what)
    {
        var got = _read(domain);
        if (!got.Ok) { LastError = Describe(got.Code, what) + " (the read-back failed)"; return false; }
        if (!Held(asked, got.Value))
        {
            LastError = $"The driver holds a {what} offset of {got.Value} MHz, not the {asked} MHz it was asked for.";
            return false;
        }
        return true;
    }
}
