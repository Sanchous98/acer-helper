using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

public sealed partial class LaptopService
{
    // ---- GPU overclock (per-mode) ----

    /// <summary>The stored GPU-OC preset for the current mode, created on first write (user is configuring it).
    /// Caller holds _state.</summary>
    private GpuOcPreset StoredGpuOc() => GetOrAdd(Settings.GpuOcPresets, CurrentModeKey());

    /// <summary>The GPU-OC preset for the current mode, or stock (0/0) if none is saved yet (not stored).</summary>
    public GpuOcPreset CurrentGpuOc()
    {
        lock (_state)
            return Settings.GpuOcPresets.TryGetValue(CurrentModeKey(), out var g) ? g : new GpuOcPreset();
    }

    /// <summary>Set the GPU core+memory clock offsets (MHz) for the CURRENT mode, persist, and apply now.</summary>
    public bool SetGpuOc(int core, int mem)
    {
        lock (_state)
        {
            var g = StoredGpuOc();
            g.Core = core; g.Mem = mem;
            Save();
        }
        var oc = device.GpuOverclock;
        if (oc == null) return false;
        if (!oc.Set(core, mem)) { LastError = oc.LastError; return false; }
        return true;
    }

    /// <summary>Apply the current mode's GPU offsets to the hardware. Defaults to stock (0/0) when the mode has
    /// no saved preset — the driver zeroes offsets on boot, so a never-configured mode is definitely stock and
    /// switching to it must clear whatever the previous mode applied. Returns the preset so the UI reflects it.
    /// Called on a mode change, at startup, and on resume.</summary>
    public GpuOcPreset ApplyModeGpuOc()
    {
        // CurrentGpuOc() hands back a LIVE preset from the Settings graph, so the pair is copied out under _state
        // and the hardware call is made with the captured values, outside it — dereferencing the reference after
        // the lock is released races SetGpuOc's write of these same two fields. (That write cannot tear an int,
        // but it can leave the two read a beat apart, so the driver would get core from one edit and mem from
        // another.)
        int core, mem;
        GpuOcPreset g;
        lock (_state)
        {
            g = CurrentGpuOc();
            core = g.Core; mem = g.Mem;
        }
        device.GpuOverclock?.Set(core, mem);
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
    public bool SetCpuPower(string id)
    {
        lock (_state)
        {
            Settings.CpuPowerModes[CurrentModeKey()] = id;
            Save();
        }
        var cp = device.CpuPower;
        if (cp == null) return false;
        if (!cp.Set(id)) { LastError = cp.LastError; return false; }
        return true;
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

    /// <summary>The Curve-Optimizer preset for the current mode, or stock (0) if none is saved yet (not stored).</summary>
    public CoPreset CurrentCo()
    {
        lock (_state)
            return Settings.CoPresets.TryGetValue(CurrentModeKey(), out var c) ? c : new CoPreset();
    }

    /// <summary>Set the all-core Curve-Optimizer offset (AVFS counts, negative = undervolt) for the CURRENT mode,
    /// persist, and apply now. Call this OFF the UI thread: the SMU transaction waits on a machine-wide PCI lock
    /// that other tuning tools also take, so it can block for seconds.</summary>
    public bool SetCo(int allCore)
    {
        var co = device.CurveOptimizer;
        // Clamp against the port's own range BEFORE persisting. The port clamps what it sends to the SMU anyway, so
        // storing an out-of-range value would only make the app report an undervolt the hardware never got.
        if (co != null) allCore = Math.Clamp(allCore, co.Range.Min, co.Range.Max);
        lock (_state)
        {
            var c = StoredCo();
            c.AllCore = allCore;
            Save();
        }
        if (co == null) return false;
        if (!co.Set(allCore)) { LastError = co.LastError; return false; }
        return true;
    }

    /// <summary>The current mode's Curve-Optimizer offsets, index-aligned with the port's voltage domains — or a single
    /// all-core value on a CPU without domain control, so the UI can render rows without knowing which path is in play.
    /// Empty when the device has no Curve-Optimizer port.</summary>
    public int[] CurrentCoDomains()
    {
        var co = device.CurveOptimizer;
        if (co == null) return [];
        // The read happens under _state because CurrentCo() hands back a LIVE preset: the Dictionary read below
        // races SetCoDomains' STRUCTURAL write of that same dictionary, and a read concurrent with an insert can
        // throw or spin on a resize rather than merely read stale. Values, not the reference, cross the boundary.
        // co.Domains is an immutable descriptor list built in the port's constructor, so no I/O is held here.
        lock (_state)
        {
            var c = CurrentCo();
            if (co.Domains.Count == 0) return [c.AllCore];
            var counts = new int[co.Domains.Count];
            for (var i = 0; i < counts.Length; i++) c.Domains.TryGetValue(co.Domains[i].Key, out counts[i]);
            return counts;
        }
    }

    /// <summary>Apply offsets the way this CPU takes them — per voltage domain where it has them, otherwise one
    /// all-core value — so the UI has a single entry point. Call OFF the UI thread.</summary>
    public bool SetCoValues(IReadOnlyList<int> counts)
    {
        var co = device.CurveOptimizer;
        if (co == null || counts.Count == 0) return false;
        return co.Domains.Count > 0 ? SetCoDomains(counts) : SetCo(counts[0]);
    }

    /// <summary>Set the per-domain Curve-Optimizer offsets (index-aligned with the port's domains) for the CURRENT mode,
    /// persist, and apply now. Call this OFF the UI thread — it is one SMU transaction per core slot.</summary>
    public bool SetCoDomains(IReadOnlyList<int> counts)
    {
        var co = device.CurveOptimizer;
        if (co == null || co.Domains.Count != counts.Count) return false;

        // Per DOMAIN, not per port: the domains are different rails with different bounds (the iGPU carries its own),
        // so clamping them all against the port-wide range would silently widen or narrow one of them.
        var clamped = new int[counts.Count];
        for (var i = 0; i < counts.Count; i++)
        {
            var (min, max) = co.Domains[i].Range ?? co.Range;
            clamped[i] = Math.Clamp(counts[i], min, max);
        }

        lock (_state)
        {
            var c = StoredCo();
            for (var i = 0; i < clamped.Length; i++) c.Domains[co.Domains[i].Key] = clamped[i];
            Save();
        }
        if (!co.SetDomains(clamped)) { LastError = co.LastError; return false; }
        return true;
    }

    /// <summary>Apply the current mode's Curve-Optimizer offset to the hardware. Defaults to stock (0) when the mode
    /// has no saved preset — the offset is SMU-resident and a power cycle clears it, so a never-configured mode is
    /// definitely stock and switching to it must clear whatever undervolt the previous mode applied. Returns the
    /// preset so the UI reflects it. Called on a mode change, at startup, and on resume — always off the UI thread,
    /// because the mailbox transaction can wait seconds on the shared PCI lock.</summary>
    public CoPreset ApplyModeCo()
    {
        var co = device.CurveOptimizer;
        CoPreset c;
        int[]? counts = null;
        int allCore;
        lock (_state)
        {
            // Never configured on this install -> never talk to the SMU at all. An empty store means the user has
            // not opted into undervolting, and sending the mailbox message anyway would be traffic nobody asked for
            // on an opcode this CPU does not confirm. The first SetCo creates an entry, after which the
            // "unconfigured mode = stock, actively re-applied" contract below takes over unchanged — so switching
            // to a mode with no preset still clears the previous mode's undervolt.
            if (Settings.CoPresets.Count == 0) return new CoPreset();
            c = Settings.CoPresets.TryGetValue(CurrentModeKey(), out var s) ? s : new CoPreset();
            // c is a LIVE reference into the graph, so the values the SMU call needs are copied out HERE — the
            // dictionary read races SetCoDomains' structural write of the same dictionary exactly as in
            // CurrentCoDomains above. The transaction below stays outside _state on purpose: it can block for
            // seconds on the machine-wide PCI lock, and holding _state across it would stall the background pass.
            allCore = c.AllCore;
            if (co != null && co.Domains.Count > 0)
            {
                counts = new int[co.Domains.Count];
                for (var i = 0; i < counts.Length; i++) c.Domains.TryGetValue(co.Domains[i].Key, out counts[i]);
            }
        }
        if (co == null) return c;
        // Per-domain wins where the CPU has separate rails: one number for both clusters is pinned by whichever gives
        // out first, so the per-domain values are the real setting and AllCore is only the single-domain fallback.
        if (counts != null)
        {
            if (!co.SetDomains(counts)) LastError = co.LastError;
        }
        else if (!co.Set(allCore)) LastError = co.LastError;
        return c;
    }
}
