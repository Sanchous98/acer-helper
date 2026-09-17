using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- GPU overclock (per-mode) ----

    /// <summary>The stored GPU-OC preset for the current mode, created on first write (user is configuring it).
    /// Caller holds _state.</summary>
    private GpuOcPreset StoredGpuOc() => GetOrAdd(Settings.GpuOcPresets, CurrentModeKey());

    /// <summary>The GPU-OC preset for the current mode, or stock (0/0) if none is saved yet (not stored).
    /// A SNAPSHOT — the caller cannot reach the stored instance through it.</summary>
    public GpuOcPreset CurrentGpuOc()
    {
        lock (_state)
            return Settings.GpuOcPresets.TryGetValue(CurrentModeKey(), out var g) ? g.Snapshot() : new GpuOcPreset();
    }

    /// <summary>As <see cref="CurrentGpuOc()"/> but reusing an already-read current profile — the same pair, and
    /// for the same reason, as <see cref="CurrentModeKey(PerformanceProfile?)"/>. The key derivation and the
    /// preset lookup stay together under <c>_state</c>, exactly as in the parameterless form; the only thing
    /// removed is the <c>PowerProfiles</c> read that caller was paying for a profile it already had.</summary>
    public GpuOcPreset CurrentGpuOc(PerformanceProfile? cur)
    {
        lock (_state)
            return Settings.GpuOcPresets.TryGetValue(CurrentModeKey(cur), out var g) ? g.Snapshot() : new GpuOcPreset();
    }

    /// <summary>Set the GPU core+memory clock offsets (MHz) for the CURRENT mode, persist, and apply now.</summary>
    public (bool ok, string? error) SetGpuOc(int core, int mem)
    {
        lock (_state)
        {
            var g = StoredGpuOc();
            g.Core = core; g.Mem = mem;
            Save();
        }
        var oc = device.GpuOverclock;
        if (oc == null) return (false, null);
        return Attempt(() => oc.Set(core, mem), () => oc.LastError);

    }

    /// <summary>Apply the current mode's GPU offsets to the hardware. Defaults to stock (0/0) when the mode has
    /// no saved preset — the driver zeroes offsets on boot, so a never-configured mode is definitely stock and
    /// switching to it must clear whatever the previous mode applied. Returns the preset so the UI reflects it.
    /// Called on a mode change, at startup, and on resume.</summary>
    public GpuOcPreset ApplyModeGpuOc()
    {
        // The pair used to be copied out under _state and the hardware call made with the captured values, because
        // CurrentGpuOc() handed back a LIVE preset and dereferencing it after the lock was released raced
        // SetGpuOc's write of those same two fields (which cannot tear an int, but could leave the two read a beat
        // apart, so the driver would get core from one edit and mem from another). CurrentGpuOc() now returns a
        // SNAPSHOT, so the copy-out has nothing left to protect: the values cannot change under this method.
        GpuOcPreset g;
        lock (_state) g = CurrentGpuOc();
        device.GpuOverclock?.Set(g.Core, g.Mem);
        return g;
    }

    // ---- CPU power mode (per-mode) ----

    /// <summary>The CPU power-mode overlay id for the current mode: the stored choice if the user set one for
    /// this profile, otherwise the live effective overlay (so the UI reflects reality on an unconfigured mode).</summary>
    public string? CurrentCpuPower()
    {
        lock (_state)
            if (Settings.CpuPowerModes.TryGetValue(CurrentModeKey(), out var id)) return id;
        return device.CpuPower?.Current();
    }

    /// <summary>Set the CPU power-mode overlay for the CURRENT mode, persist, and apply now.</summary>
    public (bool ok, string? error) SetCpuPower(string id)
    {
        lock (_state)
        {
            Settings.CpuPowerModes[CurrentModeKey()] = id;
            Save();
        }
        var cp = device.CpuPower;
        if (cp == null) return (false, null);
        return Attempt(() => cp.Set(id), () => cp.LastError);

    }

    /// <summary>Apply the current mode's CPU power overlay IF the user configured one for this profile; a mode
    /// with no entry is left untouched (we don't force an OS power mode on unconfigured profiles). Returns the
    /// id the UI should reflect (stored, or the live effective overlay). Called on mode change, startup, resume.</summary>
    public string? ApplyModeCpuPower()
    {
        var cp = device.CpuPower;
        if (cp == null) return null;
        lock (_state)
            if (Settings.CpuPowerModes.TryGetValue(CurrentModeKey(), out var id)) { cp.Set(id); return id; }
        return cp.Current();
    }

    // ---- CPU curve optimizer (per-mode) ----

    /// <summary>The stored Curve-Optimizer preset for the current mode, created on first write (user is
    /// configuring it). Caller holds _state.</summary>
    private CoPreset StoredCo() => GetOrAdd(Settings.CoPresets, CurrentModeKey());

    /// <summary>The Curve-Optimizer preset for the current mode, or stock (0) if none is saved yet (not stored).
    /// A SNAPSHOT — the caller cannot reach the stored instance through it.</summary>
    public CoPreset CurrentCo()
    {
        lock (_state)
            return Settings.CoPresets.TryGetValue(CurrentModeKey(), out var c) ? c.Snapshot() : new CoPreset();
    }

    /// <summary>As <see cref="CurrentCo()"/> but reusing an already-read current profile — the same pair, and for
    /// the same reason, as <see cref="CurrentModeKey(PerformanceProfile?)"/>. It exists because
    /// <see cref="CurrentCoDomains(PerformanceProfile?)"/> renders its rows from this preset, and that caller is
    /// handed the profile: going through the parameterless form would read the port for a key it already holds.</summary>
    public CoPreset CurrentCo(PerformanceProfile? cur)
    {
        lock (_state)
            return Settings.CoPresets.TryGetValue(CurrentModeKey(cur), out var c) ? c.Snapshot() : new CoPreset();
    }

    /// <summary>Set the all-core Curve-Optimizer offset (AVFS counts, negative = undervolt) for the CURRENT mode,
    /// persist, and apply now. Call this OFF the UI thread: the SMU transaction waits on a machine-wide PCI lock
    /// that other tuning tools also take, so it can block for seconds.</summary>
    public (bool ok, string? error) SetCo(int allCore)
    {
        var co = device.CurveOptimizer;
        // Clamp against the port's own range BEFORE persisting. The port clamps what it sends to the SMU anyway, so
        // storing an out-of-range value would only make the app report an undervolt the hardware never got.
        // OffsetCounts also makes an OVERVOLT unrepresentable, which the port's interface declares but nothing
        // enforced: see Domain/Values.cs. The guard stays — with no port there is no range to clamp against, and
        // that behaviour is pinned by LaptopServiceCoTests.
        if (co != null) allCore = new CoAxis(co.Domains, co.Range).ClampAllCore(allCore);
        lock (_state)
        {
            var c = StoredCo();
            c.AllCore = allCore;
            Save();
        }
        if (co == null) return (false, null);
        return Attempt(() => co.Set(allCore), () => co.LastError);

    }

    /// <summary>The current mode's Curve-Optimizer offsets, index-aligned with the port's voltage domains — or a single
    /// all-core value on a CPU without domain control, so the UI can render rows without knowing which path is in play.
    /// Empty when the device has no Curve-Optimizer port.</summary>
    public int[] CurrentCoDomains()
    {
        var co = device.CurveOptimizer;
        if (co == null) return [];
        // The read happens under _state, but NOT for the reason this comment used to give. It said CurrentCo()
        // hands back a LIVE preset, which is what the copy-out protected against; CurrentCo() hands back a
        // SNAPSHOT now (see CurrentCo above), so there is nothing left for the lock to keep still — the array
        // below is built from a copy nothing else can reach. It is kept because this is a read of the guarded
        // Settings graph reached from the UI thread and from the refresh pass, and because dropping a lock is a
        // change in lock scope rather than the move of a rule this method is part of. It is NOT held across a
        // hardware call of its own: the one EC transaction reachable from here is the mode-key read inside the
        // parameterless CurrentCo(), which call sites hold _state for by decision (docs/open-decisions.md §3) —
        // co.Domains is an immutable descriptor list built in the port's constructor, and the index alignment and
        // the key lookup are the domain's (Domain/CoAxis.cs).
        lock (_state)
            return new CoAxis(co.Domains, co.Range).Rows(CurrentCo());
    }

    /// <summary>As <see cref="CurrentCoDomains()"/> but reusing an already-read current profile. The lock, the
    /// rows and the port guard are the parameterless form's, unchanged — the only difference is the key inside
    /// <see cref="CurrentCo(PerformanceProfile?)"/>: this form reads NO port at all, so a caller holding the
    /// profile (the UI build path does, see <c>AppController.BuildUi</c>) stops paying an EC round-trip for the
    /// rows it renders.</summary>
    public int[] CurrentCoDomains(PerformanceProfile? cur)
    {
        var co = device.CurveOptimizer;
        if (co == null) return [];
        lock (_state)
            return new CoAxis(co.Domains, co.Range).Rows(CurrentCo(cur));
    }

    /// <summary>Apply offsets the way this CPU takes them — per voltage domain where it has them, otherwise one
    /// all-core value — so the UI has a single entry point. Call OFF the UI thread.</summary>
    public (bool ok, string? error) SetCoValues(IReadOnlyList<int> counts)
    {
        var co = device.CurveOptimizer;
        if (co == null || counts.Count == 0) return (false, null);
        // Which of the two writes this CPU takes is the domain's rule (Domain/CoAxis.cs, UsesRails), asked here
        // rather than restated: the same fork decides what a mode change sends below, and two sites deciding it
        // separately is how they come to disagree.
        return new CoAxis(co.Domains, co.Range).UsesRails ? SetCoDomains(counts) : SetCo(counts[0]);
    }

    /// <summary>Set the per-domain Curve-Optimizer offsets (index-aligned with the port's domains) for the CURRENT mode,
    /// persist, and apply now. Call this OFF the UI thread — it is one SMU transaction per core slot.</summary>
    public (bool ok, string? error) SetCoDomains(IReadOnlyList<int> counts)
    {
        var co = device.CurveOptimizer;
        if (co == null || co.Domains.Count != counts.Count) return (false, null);

        // Per RAIL, not per port: the domains are different rails with different bounds (the iGPU carries its
        // own), so clamping them all against the port-wide range would silently widen or narrow one of them. The
        // upper bound is 0 either way — an overvolt is not representable (Domain/Values.cs).
        var axis = new CoAxis(co.Domains, co.Range);
        var clamped = axis.ClampEach(counts);

        lock (_state)
        {
            var c = StoredCo();
            axis.File(c, clamped);
            Save();
        }
        return Attempt(() => co.SetDomains(clamped), () => co.LastError);

    }

    /// <summary>Apply the current mode's Curve-Optimizer offset to the hardware. Defaults to stock (0) when the mode
    /// has no saved preset — the offset is SMU-resident and a power cycle clears it, so a never-configured mode is
    /// definitely stock and switching to it must clear whatever undervolt the previous mode applied. Returns the
    /// preset so the UI reflects it. Called on a mode change, at startup, and on resume — always off the UI thread,
    /// because the mailbox transaction can wait seconds on the shared PCI lock.</summary>
    public CoPreset ApplyModeCo()
    {
        var co = device.CurveOptimizer;
        var axis = co == null ? null : new CoAxis(co.Domains, co.Range);
        CoPreset c;
        int[]? write;
        lock (_state)
        {
            c = (Settings.CoPresets.TryGetValue(CurrentModeKey(), out var s) ? s : new CoPreset()).Snapshot();
            // c is a SNAPSHOT, not a live reference, so the values the SMU call needs cannot change under it even
            // though the transaction below runs outside _state — it can block for seconds on the machine-wide PCI
            // lock, and holding _state across it would stall the background pass. The snapshot also removes the
            // race the copy-out used to exist for: the dictionary read here races SetCoDomains' structural write
            // of that same dictionary, exactly as in CurrentCoDomains above.
            //
            // What reaches the SMU is the domain's answer (Domain/CoAxis.cs, Reapply), which is also where the
            // never-configured guard lives. On this axis that guard means DO NOT WRITE AT ALL, not "write stock":
            // an empty store says the user never opted into undervolting, so the mailbox message would be traffic
            // nobody asked for on an opcode this CPU does not confirm. The distinction is the whole point — an
            // empty store and a mode with no entry are the same word in the graph and mean the opposite here,
            // because a mode that merely lacks an entry IS stock, and stock is actively re-applied.
            write = axis?.Reapply(c, Settings.CoPresets.Count == 0);
        }
        if (co == null || write == null) return c;
        // Per-domain wins where the CPU has separate rails: one number for both clusters is pinned by whichever gives
        // out first, so the per-domain values are the real setting and AllCore is only the single-domain fallback.
        if (axis!.UsesRails)
        {
            co.SetDomains(write);   // silent on purpose: no caller of ApplyModeCo reads a failure
        }
        else co.Set(write[0]);
        return c;
    }
}
