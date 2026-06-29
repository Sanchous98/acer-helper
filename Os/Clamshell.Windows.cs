using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AcerHelper.Os;

/// <summary>
/// Keep the laptop awake on lid-close, but ONLY while an external display is connected AND on AC
/// (like G-Helper). Toggles the Windows AC "lid close action" power setting; the battery value is
/// left alone (safe). Re-evaluates on display/power changes. Vendor-agnostic.
/// </summary>
public sealed class Clamshell : IClamshell
{
    private readonly EventHandler _onDisplay;
    private readonly PowerModeChangedEventHandler _onPower;
    private bool _enabled;
    private bool? _applied;   // last lid action written (true = do nothing); null = unknown

    public Clamshell()
    {
        _onDisplay = (_, _) => Evaluate();
        _onPower   = (_, _) => Evaluate();
        SystemEvents.DisplaySettingsChanged += _onDisplay;
        SystemEvents.PowerModeChanged       += _onPower;
    }

    public string Label => "Stay awake when lid closed (docked, on AC)";
    public bool Enabled => _enabled;

    /// <summary>True if the lid-close power setting is readable (composition gate).</summary>
    public bool Supported => GetAcLidAction() != null;

    public void SetEnabled(bool value)
    {
        _enabled = value;
        if (!value) { SetAcLidAction(LID_SLEEP); _applied = false; }  // restore "sleep on lid close"
        else _applied = null;                                         // force re-apply on next Evaluate
        Evaluate();
    }

    public void Evaluate()
    {
        if (!_enabled) return;
        bool active = HasExternalDisplay() && OnAc();
        if (_applied == active) return;
        SetAcLidAction(active ? LID_DO_NOTHING : LID_SLEEP);
        _applied = active;
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= _onDisplay;
        SystemEvents.PowerModeChanged       -= _onPower;
        if (_enabled) SetAcLidAction(LID_SLEEP);   // never leave lid=do-nothing after exit
    }

    // ---- lid-close power setting (powrprof) ----

    private static Guid SUB_BUTTONS = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static Guid LID_ACTION  = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private const uint LID_DO_NOTHING = 0;
    private const uint LID_SLEEP      = 1;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
        return true;
    }

    private static uint? GetAcLidAction()
    {
        if (!TryGetActiveScheme(out Guid scheme)) return null;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SUB_BUTTONS, ref LID_ACTION, out uint v) != 0) return null;
        return v;
    }

    private static void SetAcLidAction(uint value)
    {
        if (!TryGetActiveScheme(out Guid scheme)) return;
        PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SUB_BUTTONS, ref LID_ACTION, value);
        PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // apply immediately
    }

    // ---- power source (kernel32) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent;
        public byte SystemStatusFlag; public uint BatteryLifeTime; public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static bool OnAc() => GetSystemPowerStatus(out SYSTEM_POWER_STATUS s) && s.ACLineStatus == 1;

    // ---- external-display detection (QueryDisplayConfig) ----

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint OUT_INTERNAL     = 0x80000000;
    private const uint OUT_DP_EMBEDDED  = 11;
    private const uint OUT_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct MODE_INFO { }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    private static bool HasExternalDisplay()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPath, out uint numMode) != 0) return false;
            var paths = new PATH_INFO[numPath];
            var modes = new MODE_INFO[numMode];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < numPath; i++)
            {
                uint tech = paths[i].targetInfo.outputTechnology;
                if (tech != OUT_INTERNAL && tech != OUT_DP_EMBEDDED && tech != OUT_UDI_EMBEDDED) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
