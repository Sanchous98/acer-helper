using System.Runtime.InteropServices;

namespace AcerHelper;

/// <summary>
/// Detects whether an external display is currently active. Uses QueryDisplayConfig
/// and inspects each active path's target output technology, so it stays correct
/// even with the lid closed (the internal panel's path is INTERNAL/embedded).
/// </summary>
public static class DisplayInfo
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    private const uint OUT_INTERNAL     = 0x80000000; // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
    private const uint OUT_DP_EMBEDDED  = 11;          // DISPLAYPORT_EMBEDDED
    private const uint OUT_UDI_EMBEDDED = 13;          // UDI_EMBEDDED

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int  targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public PATH_SOURCE_INFO sourceInfo;
        public PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // DISPLAYCONFIG_MODE_INFO is 64 bytes; we never read it, just size the array.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
                                                 ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    /// <summary>True if at least one active display is an external monitor.</summary>
    public static bool HasExternalDisplay()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPath, out uint numMode) != 0)
                return false;

            var paths = new PATH_INFO[numPath];
            var modes = new MODE_INFO[numMode];

            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero) != 0)
                return false;

            for (int i = 0; i < numPath; i++)
            {
                uint tech = paths[i].targetInfo.outputTechnology;
                if (tech != OUT_INTERNAL && tech != OUT_DP_EMBEDDED && tech != OUT_UDI_EMBEDDED)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }
}
