using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcerHelper.Localization;

namespace AcerHelper.Tests;

/// <summary>
/// THE D2b WIRING, read out of the sources (docs/vendor-plugins.md §5.3 "reusing the existing update machinery",
/// task D2b). <c>AppController</c> is not constructible in this suite (it needs a desktop application lifetime,
/// the limit <c>UpdateRouterTests</c>/<c>NotificationSurfaceTests</c> already record), so the claims that cannot
/// be exercised are pinned as text — the repo's source-guard idiom.
///
/// WHAT IS PINNED:
///   1. THE SAME SCHEDULE. The plugin check runs from the update schedule's own tick
///      (<c>CheckForUpdatesAsync</c>, the delegate handed to <c>new UpdateSchedule(...)</c>) and NOTHING new is
///      scheduled: exactly one <c>new UpdateSchedule(</c>, no <c>PeriodicSchedule</c>/<c>DispatcherTimer</c>/
///      <c>System.Threading.Timer</c> added beside it. This is the "no second timer" half of §5.3.
///   2. THE SAME SINGLE-FLIGHT GUARD. The plugin run takes/repects the existing <c>_updating</c> flag (the one
///      <c>StartUpdate</c> owns), so a plugin download cannot race an app update (§5.3 single-flight row).
///   3. INSTALLED RAISES, EVERYTHING ELSE IS SILENT. <c>AnnouncePluginInstalled</c> is reached only under
///      <c>PluginUpdateReason.Installed</c>; the other reasons raise nothing (§5.3: failures are not surfaced).
///   4. THE MESSAGE AND ITS RESTART. The notice is keyed by the plugin id, names the plugin, and its click runs
///      the per-OS restart the self-update already uses.
/// </summary>
public class PluginUpdateWiringTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The <c>AppController</c> source with line comments dropped, so a rule about CODE cannot be
    /// satisfied by the file's own prose (which names <c>PeriodicSchedule</c>, <c>UpdateSchedule</c> etc. in
    /// explanations on purpose).</summary>
    private static string AppControllerCode() =>
        string.Join("\n", Source("UI/AppController.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// THE PLUGIN CHECK RIDES THE ONE UPDATE SCHEDULE (§5.3: "the same schedule drives the plugin check; no
    /// second timer"). Two facts: the app wires exactly ONE <c>UpdateSchedule</c> and it is fed
    /// <c>CheckForUpdatesAsync</c>; and <c>CheckForUpdatesAsync</c> calls <c>RunPluginUpdateAsync</c>. Plus the
    /// absence of any NEW timer/periodic schedule beside it.
    ///
    /// MUTATION: add a second <c>new UpdateSchedule(</c> for the plugin check, or a <c>PeriodicSchedule</c> /
    /// <c>DispatcherTimer</c> — the corresponding count/absence assert goes red.</summary>
    [Fact]
    public void ThePluginCheckRidesTheOneUpdateSchedule()
    {
        var code = AppControllerCode();

        // Exactly one schedule, and the startup tick's delegate is the method the plugin check lives in.
        Assert.Single(Regex.Matches(code, @"new UpdateSchedule\("));
        Assert.Contains("new UpdateSchedule(CheckForUpdatesAsync, AnnounceUpdate)", code, StringComparison.Ordinal);

        // The check method reaches the plugin run — the same tick drives both halves.
        var check = code.IndexOf("private async Task CheckForUpdatesAsync", StringComparison.Ordinal);
        var pluginRun = code.IndexOf("private async Task RunPluginUpdateAsync", StringComparison.Ordinal);
        Assert.True(check >= 0 && pluginRun > check, "CheckForUpdatesAsync must exist before RunPluginUpdateAsync");
        var checkBody = code[check..pluginRun];
        Assert.Contains("RunPluginUpdateAsync", checkBody, StringComparison.Ordinal);

        // ONE GitHub call per tick (§5.2): the app check and the plugin check share CheckDetailedAsync, so the
        // tick must NOT call CheckAsync and FetchLatestReleaseAsync separately (which would fetch twice).
        Assert.Contains("CheckDetailedAsync", checkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_updates.CheckAsync(", checkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchLatestReleaseAsync(", checkBody, StringComparison.Ordinal);

        // No second timer/periodic schedule was added by this feature.
        Assert.DoesNotContain("new PeriodicSchedule(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new DispatcherTimer(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new System.Threading.Timer(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new Timer(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PLUGIN RUN RESPECTS THE EXISTING SINGLE-FLIGHT GUARD (§5.3 single-flight row: "the same
    /// <c>StartUpdate</c> guard wraps the plugin download"). <c>RunPluginUpdateAsync</c> returns early on
    /// <c>_updating</c>, and the shared <c>RunGuardedAsync</c> takes the flag before doing any work — the same
    /// flag <c>StartUpdate</c> reads.
    ///
    /// MUTATION: drop the <c>if (_updating) return;</c> from <c>RunPluginUpdateAsync</c> — a plugin download can
    /// overlap an app update and this goes red.</summary>
    [Fact]
    public void ThePluginRunSharesTheSingleFlightGuard()
    {
        var code = AppControllerCode();

        var run = code.IndexOf("private async Task RunPluginUpdateAsync", StringComparison.Ordinal);
        var guarded = code.IndexOf("private async Task RunGuardedAsync", StringComparison.Ordinal);
        Assert.True(run >= 0 && guarded > run, "the two methods must both exist, in this order");
        var runBody = code[run..guarded];

        Assert.Contains("if (_updating) return;", runBody, StringComparison.Ordinal);
        Assert.Contains("RunGuardedAsync", runBody, StringComparison.Ordinal);

        // The guard is the SAME field StartUpdate owns: taken before work and released in a finally.
        var guardedBody = code[guarded..];
        Assert.Contains("_updating = true;", guardedBody, StringComparison.Ordinal);
        Assert.Contains("_updating = false", guardedBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONLY <c>Installed</c> RAISES (§5.3 step 5; failures degrade silently like the app update). The raise is
    /// guarded by the reason, and the announce is UI-thread-marshalled because the service runs off it.
    ///
    /// MUTATION: raise on every reason instead of just <c>Installed</c> — the guard assert goes red.</summary>
    [Fact]
    public void OnlyAnInstalledPluginRaisesTheNotification()
    {
        var code = AppControllerCode();

        Assert.Contains("if (result.Reason == PluginUpdateReason.Installed)", code, StringComparison.Ordinal);
        // The raise is on the UI thread (the service completes on a pool thread).
        Assert.Contains("Dispatcher.UIThread.Post(() => AnnouncePluginInstalled(", code, StringComparison.Ordinal);

        // The ONLY call to AnnouncePluginInstalled is the Installed one (the other match is the declaration).
        Assert.Single(Regex.Matches(code, @"AnnouncePluginInstalled\(result\.PluginId\)"));
        // ...and the raise goes over the EXISTING notification surface, keyed by the plugin id helper.
        Assert.Contains("NotificationCenter.PluginId(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE MESSAGE IS KEYED BY THE PLUGIN ID AND ITS CLICK RESTARTS, the per-OS mechanism the self-update already
    /// uses (§5.3 step 5). Linux/AppImage relaunches through <see cref="AcerHelper.Infrastructure.AppImageUpdater.Restart"/>;
    /// Windows, which has no in-place relaunch primitive for a plugin, spawns the running executable detached
    /// (the process-restart idiom; <see cref="System.Environment.ProcessPath"/>, the same source
    /// <c>WindowsUpdater</c> uses).
    ///
    /// MUTATION: raise without the restart action, or drop the AppImage path — the asserts go red.</summary>
    [Fact]
    public void TheNoticeRestartsTheAppThroughTheExistingPaths()
    {
        var code = AppControllerCode();

        // The raise hands the plugin id (the stable key) and the per-OS restart action, over the existing surface.
        Assert.Contains("NotificationCenter.PluginId(pluginId)", code, StringComparison.Ordinal);
        Assert.Contains("RestartApp", code, StringComparison.Ordinal);
        Assert.Contains("_notifications.Raise(", code, StringComparison.Ordinal);

        // Per-OS restart: AppImage relaunch on Linux, detached process restart on Windows, then exit both ways.
        var restart = code.IndexOf("private void RestartApp()", StringComparison.Ordinal);
        Assert.True(restart >= 0, "RestartApp must exist");
        var restartBody = code[restart..];
        Assert.Contains("AppImageUpdater.IsAppImage", restartBody, StringComparison.Ordinal);
        Assert.Contains("AppImageUpdater.Restart()", restartBody, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessPath", restartBody, StringComparison.Ordinal);
        Assert.Contains("ExitApp()", restartBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RESTART-NOTIFICATION SENTENCE EXISTS IN BOTH TABLES, and the app reads it by the neutral key. A
    /// missing Russian row is silent — the app falls back to English — so it is asserted directly, like the other
    /// feature guards in this suite.
    ///
    /// MUTATION: delete the <c>plugin.installed_restart</c> row from either file, or rename the key the app asks
    /// for.</summary>
    [Fact]
    public void TheRestartSentenceExistsInBothTables()
    {
        Assert.Contains("Loc.T(\"plugin.installed_restart\"", Source("UI/AppController.cs"),
                        StringComparison.Ordinal);
        Assert.True(Loc.Ru("plugin.installed_restart") is not null,
                    "the plugin restart notice has no row in Localization/Strings.ru.resx");
        // The neutral English row is the fallback and must be there too — both files carry the key.
        Assert.Contains("name=\"plugin.installed_restart\"", Source("Localization/Strings.resx"),
                        StringComparison.Ordinal);
        Assert.Contains("name=\"plugin.installed_restart\"", Source("Localization/Strings.ru.resx"),
                        StringComparison.Ordinal);
    }
}
