using System.Diagnostics;
using System.Globalization;

namespace AcerHelper.Infrastructure.Diagnostics;

/// <summary>Appends a one-line <see cref="GateStats"/> summary to <c>%AppData%\AcerHelper\gate-stats.log</c>,
/// beside <c>settings.json</c>.
///
/// Written periodically as well as on exit, and the periodic write is the point: the numbers are wanted from
/// the owner's real machine, where the process is as likely to be killed (or crash) as to reach
/// <c>ExitApp</c>, and a final-only write would lose exactly the session worth measuring.
///
/// Every failure is swallowed: a diagnostic that can break the app is worse than no diagnostic. See
/// docs/refactoring-plan.md, waves 5-6.</summary>
internal static class GateStatsLog
{
    private static readonly long IntervalTicks = Stopwatch.Frequency * 60;   // one line a minute
    private static readonly Lock WriteGate = new();
    private static long _lastWrite;
    private static long _startupTicks;
    private static long _buildTicks;

    /// <summary>Ticks the UI thread spent inside the gate during <c>ApplyStartupState</c>. One-shot.</summary>
    public static void RecordStartup(long ticks) => Interlocked.Exchange(ref _startupTicks, ticks);

    /// <summary>Ticks the UI thread spent inside the gate while building the UI. One-shot.</summary>
    public static void RecordBuild(long ticks) => Interlocked.Exchange(ref _buildTicks, ticks);

    /// <summary>Write, unless a line was written in the last minute. Safe to call from anywhere, including
    /// the UI thread: the throttle is checked inline (a clock read) and the file I/O itself is handed to the
    /// pool, because this is called from the poll's timer.</summary>
    public static void MaybeWrite()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastWrite) < IntervalTicks) return;
        Interlocked.Exchange(ref _lastWrite, now);
        _ = Task.Run(Write);
    }

    /// <summary>Write a line now. Called on exit, where a queued task might never get to run — so this one
    /// does its I/O inline.</summary>
    public static void Write()
    {
        var line = Format(GateStats.Snapshot(), Interlocked.Read(ref _startupTicks),
                          Interlocked.Read(ref _buildTicks), DateTime.UtcNow);
        lock (WriteGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* best effort — never let a log break the app */ }
        }
    }

    /// <summary>One line: cumulative gate counters for the process, split pool vs UI, plus the two one-shot
    /// startup samples. Exposed for the test that pins the format, and the culture it is written in — the app
    /// runs on Russian-locale machines, where a default-formatted <c>12.3</c> would come out as
    /// <c>12,3</c> and make the file unparseable.</summary>
    internal static string Format(GateStats.Report r, long startupTicks, long buildTicks, DateTime utcNow)
    {
        var startupMs = startupTicks * 1000.0 / Stopwatch.Frequency;
        var buildMs = buildTicks * 1000.0 / Stopwatch.Frequency;
        var stamp = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{stamp} tx_pool={r.Pool.Tx} tx_ui={r.NonPool.Tx} wait_pool_ms={r.Pool.WaitMs:F1} wait_pool_max_ms={r.Pool.MaxWaitMs:F1} wait_ui_ms={r.NonPool.WaitMs:F1} wait_ui_max_ms={r.NonPool.MaxWaitMs:F1} hold_pool_ms={r.Pool.HoldMs:F1} hold_pool_max_ms={r.Pool.MaxHoldMs:F1} hold_pool_stalls={r.Pool.Stalls} hold_ui_ms={r.NonPool.HoldMs:F1} hold_ui_max_ms={r.NonPool.MaxHoldMs:F1} hold_ui_stalls={r.NonPool.Stalls} startup_hw_ms={startupMs:F1} build_hw_ms={buildMs:F1}");
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcerHelper", "gate-stats.log");
}
