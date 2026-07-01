using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>Blue-light reduction on X11 via per-output gamma (<c>xrandr --output N --gamma 1:1:B</c>): lower
/// blue = warmer. Same level scale as the Windows build; Level 0 = off (gamma reset to 1:1:1). Applied to
/// every connected output. X11 only — on Wayland/headless <c>xrandr</c> fails, so <see cref="Available"/> is
/// false and the composition root omits the port. Pure software, no root. (Shells out to xrandr, mirroring
/// how the generic power-profiles backend shells out to busctl.)</summary>
public sealed class DisplayTint : IDisplayTint
{
    // Blue-channel gamma per level (1.0 = neutral, lower = warmer). Mirrors the Windows blue scale.
    private static readonly double[] Blue = [1.00, 0.85, 0.70, 0.60, 0.50];

    private readonly List<string> _outputs;

    public DisplayTint() => _outputs = ConnectedOutputs();

    /// <summary>True if X11 is reachable and at least one output was found (composition gate).</summary>
    public bool Available => _outputs.Count > 0;

    public int Levels => Blue.Length;   // 5 (Off + 4)

    public bool Apply(int level)
    {
        double blue = Blue[Math.Clamp(level, 0, Blue.Length - 1)];
        string g = "1:1:" + blue.ToString(CultureInfo.InvariantCulture);
        bool ok = _outputs.Count > 0;
        foreach (var o in _outputs) ok &= Run("--output", o, "--gamma", g).code == 0;
        return ok;
    }

    private static List<string> ConnectedOutputs()
    {
        var list = new List<string>();
        var (code, outp) = Run("--query");
        if (code != 0) return list;
        foreach (Match m in Regex.Matches(outp, @"^(\S+) connected", RegexOptions.Multiline))
            list.Add(m.Groups[1].Value);
        return list;
    }

    private static (int code, string output) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("xrandr") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
