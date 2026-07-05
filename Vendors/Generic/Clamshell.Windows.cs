using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AcerHelper.Vendors.Generic;

// Windows hooks for clamshell: the AC "lid close action" power setting (powrprof), AC state
// (GetSystemPowerStatus), external-display detection (QueryDisplayConfig), and re-evaluation on
// display/power change events (SystemEvents). The battery lid value is left alone (safe).
public sealed partial class Clamshell
{
    private partial void Subscribe()
    {
        SystemEvents.DisplaySettingsChanged += OnSystemEvent;
        SystemEvents.PowerModeChanged       += OnPowerEvent;
    }

    private partial void Unsubscribe()
    {
        SystemEvents.DisplaySettingsChanged -= OnSystemEvent;
        SystemEvents.PowerModeChanged       -= OnPowerEvent;
    }

    private void OnSystemEvent(object? sender, EventArgs e) => Evaluate();
    private void OnPowerEvent(object? sender, PowerModeChangedEventArgs e) => Evaluate();

    private partial bool CanManageLidAction() => GetAcLidAction() != null;
    private partial bool OnAc() => GetSystemPowerStatus(out SYSTEM_POWER_STATUS s) && s.ACLineStatus == 1;
    private partial void SetLidStayAwake(bool stayAwake) => SetAcLidAction(stayAwake ? LID_DO_NOTHING : LID_SLEEP);

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

    private partial bool HasExternalDisplay()
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
