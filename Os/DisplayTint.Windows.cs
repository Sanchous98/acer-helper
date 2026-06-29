using System.Runtime.InteropServices;
using AcerHelper.Features;

namespace AcerHelper.Os;

/// <summary>
/// Blue-light reduction as a display gamma adjustment (gdi32 SetDeviceGammaRamp on the primary
/// display). Per-channel scales measured from NitroSense. Pure software, no admin required.
/// Vendor-agnostic. Levels: 0 = Off, 1 = Low, 2 = Medium, 3 = High, 4 = Long-use.
/// </summary>
public sealed class DisplayTint : IDisplayTint
{
    private static readonly (double R, double B)[] Scale =
    {
        (1.00, 1.00),   // 0 Off
        (1.00, 0.85),   // 1 Low
        (1.00, 0.70),   // 2 Medium
        (1.00, 0.60),   // 3 High
        (1.06, 0.50),   // 4 Long-use
    };

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern bool SetDeviceGammaRamp(IntPtr hDC, ushort[] lpRamp);

    public int Levels => Scale.Length;   // 5 (Off + 4)

    public bool Apply(int level)
    {
        int lvl = Math.Clamp(level, 0, Scale.Length - 1);
        bool on = lvl > 0;
        double rScale = Scale[lvl].R;
        double bScale = Scale[lvl].B;

        ushort[] ramp = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            ushort linear = (ushort)Math.Min(i * 257, 65535);
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
