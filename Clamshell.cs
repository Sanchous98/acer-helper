using System.Runtime.InteropServices;

namespace AcerHelper;

/// <summary>
/// Clamshell mode: keep the laptop running when the lid is closed (for docked /
/// external-display use). Implemented like G-Helper — by changing the Windows
/// power setting "Lid close action" (GUID_LIDCLOSE_ACTION) in the active scheme.
///
/// We only change the AC (plugged-in) value, so closing the lid on battery still
/// sleeps the machine (safe). Values: 0=Do nothing, 1=Sleep, 2=Hibernate, 3=Shutdown.
/// </summary>
public static class Clamshell
{
    private static Guid SUB_BUTTONS = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static Guid LID_ACTION  = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    private const uint LID_DO_NOTHING = 0;
    private const uint LID_SLEEP      = 1;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid scheme);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
        return true;
    }

    /// <summary>Current AC lid-close action, or null on failure.</summary>
    public static uint? GetAcLidAction()
    {
        if (!TryGetActiveScheme(out Guid scheme)) return null;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SUB_BUTTONS, ref LID_ACTION, out uint v) != 0) return null;
        return v;
    }

    private static bool SetAcLidAction(uint value)
    {
        if (!TryGetActiveScheme(out Guid scheme)) return false;
        bool ok = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SUB_BUTTONS, ref LID_ACTION, value) == 0;
        // re-apply so the change takes effect immediately
        PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        return ok;
    }

    public static bool Enable()  => SetAcLidAction(LID_DO_NOTHING);
    public static bool Disable() => SetAcLidAction(LID_SLEEP);

    public static bool IsEnabled() => GetAcLidAction() == LID_DO_NOTHING;
    public static bool Available  => GetAcLidAction() != null;
}
