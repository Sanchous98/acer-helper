using System.IO;
using System.Linq;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

// Generic laptop telemetry through the Linux hwmon sysfs tree (/sys/class/hwmon). This is the one
// vendor-independent way to read fan speeds and temperatures: whatever the EC/ACPI driver exposes
// (dell_smm, thinkpad_acpi, asus-wmi, coretemp, k10temp, amdgpu, …) appears here in a uniform shape.
// Monitoring is universal; control is not — only some drivers expose user-writable PWM, so fan
// control is offered separately and only where the kernel actually permits it (HwmonFanControl).

/// <summary>Thin reader/scanner over /sys/class/hwmon.</summary>
internal static class Hwmon
{
    public const string Root = "/sys/class/hwmon";

    public readonly record struct Chip(string Path, string Name);

    /// <summary>All hwmon chips, ordered (hwmon0, hwmon1, …).</summary>
    public static IReadOnlyList<Chip> Chips()
    {
        var list = new List<Chip>();
        try
        {
            list.AddRange(Directory.EnumerateDirectories(Root).Select(dir => new Chip(dir, ReadText(Path.Combine(dir, "name")) ?? "")));
        }
        catch { /* no hwmon (unlikely) -> empty */ }
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>Channel numbers for files like <c>fan1_input</c> / <c>pwm1_enable</c>, ascending.</summary>
    public static int[] Channels(string dir, string glob, string prefix, string suffix)
    {
        try
        {
            return Directory.EnumerateFiles(dir, glob)
                .Select(Path.GetFileName)
                .Select(n => n!.Length > prefix.Length + suffix.Length &&
                             int.TryParse(n.AsSpan(prefix.Length, n.Length - prefix.Length - suffix.Length), out var i)
                                 ? i : -1)
                .Where(i => i > 0)
                .OrderBy(i => i)
                .ToArray();
        }
        catch { return []; }
    }

    /// <summary>The real fans on a chip, with their labels. Channels that are unreadable, or read 0
    /// with no label, are dropped — many ITE/EC drivers expose phantom fan inputs, and we want the
    /// reported count to match the laptop's actual 1–3 fans.</summary>
    public static List<(int Idx, string Label)> Fans(string dir)
    {
        var fans = new List<(int, string)>();
        foreach (var i in Channels(dir, "fan*_input", "fan", "_input"))
        {
            var rpm = ReadInt(Path.Combine(dir, $"fan{i}_input"));
            var label = ReadText(Path.Combine(dir, $"fan{i}_label")) ?? "";
            switch (rpm)
            {
                case null:
                case <= 0 when label.Length == 0:
                    continue;
                default:
                    fans.Add((i, label));
                    break;
            }
        }
        return fans;
    }

    /// <summary>Pick the single chip that best represents the laptop's fans: the one with the most
    /// real fans, preferring labelled ones. This de-duplicates the common case where two drivers
    /// (e.g. dell_ddv + dell_smm) each surface the same physical fan.</summary>
    public static (string? Dir, List<(int Idx, string Label)> Fans) BestFanSource(IReadOnlyList<Chip> chips)
    {
        string? bestDir = null;
        var best = new List<(int, string)>();
        var bestLabeled = false;
        foreach (var c in chips)
        {
            var fans = Fans(c.Path);
            if (fans.Count == 0) continue;
            var labeled = fans.Any(f => f.Label.Length > 0);
            if (fans.Count > best.Count || (fans.Count == best.Count && labeled && !bestLabeled))
                (bestDir, best, bestLabeled) = (c.Path, fans, labeled);
        }
        return (bestDir, best);
    }

    public static string? CpuTempPath(IReadOnlyList<Chip> chips)
    {
        if (ChipDir(chips, "coretemp") is { } intel)
            return ByExactLabel(intel, "Package id 0") ?? FirstTemp(intel);
        if (ChipDir(chips, "k10temp", "zenpower") is { } amd)
            return ByExactLabel(amd, "Tctl") ?? ByExactLabel(amd, "Tdie") ?? FirstTemp(amd);
        return chips.Select(c => ByLabelContains(c.Path, "CPU")).FirstOrDefault(p => p != null);
    }

    public static string? GpuTempPath(IReadOnlyList<Chip> chips)
    {
        if (ChipDir(chips, "amdgpu", "nouveau", "nvidia") is { } gpu)
            return ByExactLabel(gpu, "edge") ?? FirstTemp(gpu);
        return chips.Select(c => ByLabelContains(c.Path, "GPU")).FirstOrDefault(p => p != null);
    }

    // Temperatures are in millidegrees; -1 if the path is absent/unreadable.
    public static int Milli(string? path) => path != null && ReadInt(path) is { } v ? v / 1000 : -1;

    public static bool CanWrite(string path)
    {
        // Open for write (no data written) purely to probe permission — sysfs attributes don't act
        // until a write() actually happens, so this is side-effect free.
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }

    public static int? ReadInt(string path) => int.TryParse(ReadText(path), out var v) ? v : null;

    public static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static string? ChipDir(IReadOnlyList<Chip> chips, params string[] names)
    {
        foreach (var c in chips) if (names.Contains(c.Name)) return c.Path;
        return null;
    }

    private static string? FirstTemp(string dir)
    {
        var ch = Channels(dir, "temp*_input", "temp", "_input");
        return ch.Length > 0 ? Path.Combine(dir, $"temp{ch[0]}_input") : null;
    }

    private static string? ByExactLabel(string dir, string label)
    {
        foreach (var i in Channels(dir, "temp*_label", "temp", "_label"))
            if (string.Equals(ReadText(Path.Combine(dir, $"temp{i}_label")), label, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, $"temp{i}_input");
        return null;
    }

    private static string? ByLabelContains(string dir, string sub)
    {
        foreach (var i in Channels(dir, "temp*_label", "temp", "_label"))
            if (ReadText(Path.Combine(dir, $"temp{i}_label")) is { } l && l.Contains(sub, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(dir, $"temp{i}_input");
        return null;
    }
}

/// <summary>Read-only fan + temperature monitoring via hwmon. Reports the laptop's 1–3 fans plus
/// CPU/GPU temperatures where the corresponding drivers are present.</summary>
internal sealed class HwmonSensors : ISensors
{
    private readonly string? _fanDir;
    private readonly int[] _fanIdx;
    private readonly string[] _fanLabels;
    private readonly string? _cpuTemp;
    private readonly string? _gpuTemp;

    private HwmonSensors(string? fanDir, int[] fanIdx, string[] fanLabels, string? cpuTemp, string? gpuTemp)
        => (_fanDir, _fanIdx, _fanLabels, _cpuTemp, _gpuTemp) = (fanDir, fanIdx, fanLabels, cpuTemp, gpuTemp);

    /// <summary>Probe hwmon once; null if the machine exposes no fan and no CPU/GPU temperature.</summary>
    public static HwmonSensors? TryCreate()
    {
        var chips = Hwmon.Chips();
        var (fanDir, fans) = Hwmon.BestFanSource(chips);
        var cpu = Hwmon.CpuTempPath(chips);
        var gpu = Hwmon.GpuTempPath(chips);
        if (fanDir == null && cpu == null && gpu == null) return null;

        var idx = fans.Select(f => f.Idx).ToArray();
        var labels = fans.Select((f, n) =>
            string.IsNullOrWhiteSpace(f.Label) ? fans.Count == 1 ? "Fan" : $"Fan {n + 1}" : f.Label).ToArray();
        return new HwmonSensors(fanDir, idx, labels, cpu, gpu);
    }

    public SensorSnapshot Read()
    {
        var fans = new List<FanReading>(_fanIdx.Length);
        if (_fanDir == null)
            return new SensorSnapshot
                { CpuTempC = Hwmon.Milli(_cpuTemp), GpuTempC = Hwmon.Milli(_gpuTemp), Fans = fans };
        fans.AddRange(_fanIdx.Select((t, i) => new FanReading(_fanLabels[i], Hwmon.ReadInt(Path.Combine(_fanDir, $"fan{t}_input")) ?? -1)));

        return new SensorSnapshot { CpuTempC = Hwmon.Milli(_cpuTemp), GpuTempC = Hwmon.Milli(_gpuTemp), Fans = fans };
    }
}

/// <summary>Best-effort fan control via hwmon PWM. Exists only when the kernel exposes a
/// <c>pwmN_enable</c>+<c>pwmN</c> pair this user can actually write — which most laptops do NOT
/// (the files are usually root-owned), so on a typical machine fans stay read-only. Where it is
/// available it controls one fan (Auto hands control back to the firmware; Max/Custom set speed).</summary>
internal sealed class HwmonFanControl : IFanControl, IDisposable
{
    private const int ManualMode = 1;   // pwmN_enable = 1 -> speed driven by pwmN
    private const int AutoMode   = 2;   // pwmN_enable = 2 -> firmware/driver controls the fan

    private readonly string _enable, _pwm;
    private bool _engaged;              // we forced manual mode -> must restore auto on exit
    public string? LastError { get; private set; }

    private HwmonFanControl(string enable, string pwm) => (_enable, _pwm) = (enable, pwm);

    public static HwmonFanControl? TryCreate()
    {
        foreach (var c in Hwmon.Chips())
            foreach (var n in Hwmon.Channels(c.Path, "pwm*_enable", "pwm", "_enable"))
            {
                var enable = Path.Combine(c.Path, $"pwm{n}_enable");
                var pwm = Path.Combine(c.Path, $"pwm{n}");
                if (File.Exists(pwm) && Hwmon.CanWrite(enable) && Hwmon.CanWrite(pwm))
                    return new HwmonFanControl(enable, pwm);
            }
        return null;
    }

    // A generic PWM channel is a single controllable fan -> one speed slider, no separate GPU fan.
    public FanCapability Capability => new(HasMax: true, HasCustom: true, HasGpuFan: false);

    public bool SetMode(FanMode mode) => mode switch
    {
        FanMode.Max  => SetManual(100),
        FanMode.Auto => Restore(),
        _            => true,
    };

    public bool SetCustomSpeeds(byte cpuPercent, byte gpuPercent) => SetManual(cpuPercent);

    private bool SetManual(int percent)
    {
        if (!Write(_enable, ManualMode)) return false;
        _engaged = true;
        return Write(_pwm, Math.Clamp(percent, 0, 100) * 255 / 100);
    }

    private bool Restore()
    {
        if (!Write(_enable, AutoMode)) return false;
        _engaged = false;
        return true;
    }

    private bool Write(string path, int value)
    {
        try { File.WriteAllText(path, value.ToString()); LastError = null; return true; }
        catch (Exception e) { LastError = e.Message; return false; }
    }

    // Never leave a fan stuck in manual mode after we exit — hand control back to the firmware.
    public void Dispose()
    {
        if (!_engaged) return;
        try { Write(_enable, AutoMode); } catch { /* best effort */ }
    }
}
