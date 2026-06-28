using System.Runtime.InteropServices;

namespace AcerHelper;

/// <summary>
/// Acer "Bluelight Shield" reimplemented as a display gamma adjustment (it is NOT an
/// EC/WMI feature). Measured from NitroSense (level "Low"): R/G stay ~linear (i*256)
/// and the blue channel is scaled down, warming the screen. Pure software via gdi32
/// SetDeviceGammaRamp on the primary display, no admin required.
///
/// Levels: 0 = Off, 1 = Low, 2 = Medium, 3 = High, 4 = Long-use.
/// Per-channel scales measured from NitroSense (R/G base = i*256; G always linear):
///   Low  B*0.85 | Medium B*0.70 | High B*0.60 | Long-use R*1.06, B*0.50.
/// </summary>
public static class Bluelight
{
    // index = level; (R scale, B scale). [0] = off (identity).
    private static readonly (double R, double B)[] Scale =
    {
        (1.00, 1.00),   // 0 Off (unused; identity applied)
        (1.00, 0.85),   // 1 Low
        (1.00, 0.70),   // 2 Medium
        (1.00, 0.60),   // 3 High
        (1.06, 0.50),   // 4 Long-use
    };

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern bool SetDeviceGammaRamp(IntPtr hDC, ushort[] lpRamp);

    public static int Levels => Scale.Length;   // 5 (Off + 4)

    /// <summary>Apply a blue-light level (0=off) to the primary display gamma. True on success.</summary>
    public static bool Apply(int level)
    {
        int lvl = Math.Min(Math.Max(level, 0), Scale.Length - 1);
        bool on = lvl > 0;
        double rScale = Scale[lvl].R;
        double bScale = Scale[lvl].B;

        ushort[] ramp = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            ushort linear = (ushort)Math.Min(i * 257, 65535);   // identity
            ramp[i]       = on ? (ushort)Math.Min(i * 256 * rScale, 65535) : linear;   // R
            ramp[256 + i] = on ? (ushort)(i * 256)                          : linear;   // G
            ramp[512 + i] = on ? (ushort)(i * 256 * bScale)                 : linear;   // B
        }

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(dc, ramp); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
