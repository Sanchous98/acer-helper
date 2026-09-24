using System.Runtime.CompilerServices;
using AcerHelper.Infrastructure;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="UpdateRouter"/> — which install path the update notification's click takes. The defect this file
/// exists for: a Windows build that is NOT the MSI install (a portable download, a development run) used to fall
/// straight through to "open the release page", so the notification pointed at GitHub and never offered to
/// install anything. The router now hands a portable Windows run the release MSI to launch.
///
/// WHY IT IS PURE. Every OS fact is a parameter (<c>isWindows</c>, <c>windowsInstalled</c>, <c>isAppImage</c>),
/// so the whole decision is testable here on any host, with the REAL asset names the release workflow uploads
/// (<c>AcerHelper-Setup.msi</c>, <c>AcerHelper-x86_64.AppImage</c> — see .github/workflows/build.yml).
/// </summary>
public class UpdateRouterTests
{
    private static readonly ReleaseAsset Msi =
        new("AcerHelper-Setup.msi", "https://example.invalid/v0.34.0/AcerHelper-Setup.msi");

    private static readonly ReleaseAsset AppImage =
        new("AcerHelper-x86_64.AppImage", "https://example.invalid/v0.34.0/AcerHelper-x86_64.AppImage");

    private static readonly ReleaseAsset[] Release = [Msi, AppImage];

    /// <summary>THE FIX. A portable Windows run gets an INSTALL action carrying the MSI, not the release page —
    /// that is what turns "the notification opens GitHub" into "the notification installs". The asset is asserted
    /// beside the kind because the install path is only as good as the file it is handed.
    ///
    /// MUTATION: put the old inline choice back (<c>windowsInstalled ? UpgradeWindows : OpenPage</c>) — the kind
    /// reads OpenPage and this goes red.</summary>
    [Fact]
    public void APortableWindowsRunInstallsTheMsi()
    {
        var plan = UpdateRouter.Route(Release, isWindows: true, windowsInstalled: false, isAppImage: false);

        Assert.Equal(UpdateAction.InstallWindows, plan.Kind);
        Assert.Equal(Msi, plan.Asset);
    }

    /// <summary>The MSI-installed build keeps its in-place silent upgrade, which is a DIFFERENT action from the
    /// portable install: it quits and lets the detached helper run msiexec, while the portable run leaves the
    /// Windows Installer on screen.
    ///
    /// MUTATION: return InstallWindows regardless of <c>windowsInstalled</c> — this goes red.</summary>
    [Fact]
    public void AnInstalledWindowsRunUpgradesInPlace()
    {
        var plan = UpdateRouter.Route(Release, isWindows: true, windowsInstalled: true, isAppImage: false);

        Assert.Equal(UpdateAction.UpgradeWindows, plan.Kind);
        Assert.Equal(Msi, plan.Asset);
    }

    /// <summary>A release with no MSI we can hand to the Installer has nothing to install on Windows, so the
    /// click still opens the page. The fallback is the honest answer rather than a silent no-op.
    ///
    /// MUTATION: return InstallWindows whenever <c>isWindows</c> — the asset is null and this goes red.</summary>
    [Fact]
    public void AWindowsReleaseWithNoMsiFallsBackToThePage()
    {
        var plan = UpdateRouter.Route([AppImage], isWindows: true, windowsInstalled: false, isAppImage: false);

        Assert.Equal(UpdateAction.OpenPage, plan.Kind);
        Assert.Null(plan.Asset);
    }

    /// <summary>A Linux AppImage replaces the running file; the MSI on the same release is not for it.
    ///
    /// MUTATION: check the MSI before the <c>isWindows</c> guard — the action becomes InstallWindows and this
    /// goes red.</summary>
    [Fact]
    public void AnAppImageReplacesItself()
    {
        var plan = UpdateRouter.Route(Release, isWindows: false, windowsInstalled: false, isAppImage: true);

        Assert.Equal(UpdateAction.ReplaceAppImage, plan.Kind);
        Assert.Equal(AppImage, plan.Asset);
    }

    /// <summary>A Linux install that is NOT an AppImage (an RPM, a raw tree) has no in-place path, so the click
    /// opens the page rather than downloading a file it cannot use.
    ///
    /// MUTATION: drop the <c>isAppImage</c> guard — the action becomes ReplaceAppImage and this goes red.</summary>
    [Fact]
    public void ANonAppImageLinuxInstallFallsBackToThePage()
    {
        var plan = UpdateRouter.Route(Release, isWindows: false, windowsInstalled: false, isAppImage: false);

        Assert.Equal(UpdateAction.OpenPage, plan.Kind);
        Assert.Null(plan.Asset);
    }

    /// <summary>The two pickers choose their own kind out of one asset list, which is what lets the router treat
    /// them as alternatives. Pinned together because a change to either suffix test would otherwise be invisible
    /// to the router tests above, which hand in an asset already picked.
    ///
    /// MUTATION: make <see cref="WindowsUpdater.PickAsset"/> prefer the AppImage, or drop its <c>.msi</c> test —
    /// the Windows routes above then carry the wrong asset even though their kind stays right.</summary>
    [Fact]
    public void ThePickersChooseTheirOwnKind()
    {
        Assert.Equal(Msi, WindowsUpdater.PickAsset(Release));
        Assert.Equal(AppImage, AppImageUpdater.PickAsset(Release));
    }

    /// <summary>THE UI ACTUALLY USES THE ROUTER. The pure tests above prove the decision but not that the running
    /// app asks for it — and the original defect lived in the call site, not in a picker. <c>AppController</c> is
    /// not constructible here, so the wiring is read out of the source.
    ///
    /// MUTATION: put the old inline choice back in <c>AnnounceUpdate</c> — the first assert goes red.</summary>
    [Fact]
    public void TheAppRoutesThroughTheInstallerPlan()
    {
        var source = Source("UI/AppController.cs");

        Assert.Contains("UpdateRouter.Route(", source, StringComparison.Ordinal);
        Assert.Contains("UpdateAction.InstallWindows =>", source, StringComparison.Ordinal);
        Assert.Contains("InstallWindowsAsync", source, StringComparison.Ordinal);
    }

    /// <summary>EVERY UPDATE ACTION RUNS OFF THE UI THREAD. The three updater calls are documented "Blocking I/O —
    /// call off the UI thread", yet the click that reaches them is a UI-thread command: running the body inline
    /// (the old <c>_ = SelfUpdateAsync(...)</c>) is what made pressing Install hitch the cursor. Each route is now
    /// wrapped in <c>StartUpdate</c>, which takes the single-flight guard on the UI thread and hands the body to
    /// the pool.
    ///
    /// MUTATIONS: put a route back to <c>() =&gt; _ = FooAsync(...)</c> — that route's <c>StartUpdate(() =&gt;</c>
    /// assert goes red; move the <c>_updating</c> check inside the pool task — the guard assert goes red.</summary>
    [Fact]
    public void EveryUpdateActionRunsOffTheUiThread()
    {
        var source = Source("UI/AppController.cs");

        // The guard is taken BEFORE any pool work exists, then the body is dispatched.
        var guard = source.IndexOf("if (_updating) return;", StringComparison.Ordinal);
        var pool = source.IndexOf("_ = Task.Run(async () =>", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the single-flight guard is gone");
        Assert.True(pool > guard, "the guard must be taken before the work is handed to the pool");

        // Each of the three routes goes through StartUpdate, never straight at the async body.
        Assert.Contains("StartUpdate(() => SelfUpdateWindowsAsync(", source, StringComparison.Ordinal);
        Assert.Contains("StartUpdate(() => InstallWindowsAsync(", source, StringComparison.Ordinal);
        Assert.Contains("StartUpdate(() => SelfUpdateAsync(", source, StringComparison.Ordinal);
        // ...and the blocking updater calls still sit on the body's side of the hand-off.
        Assert.Contains("await WindowsUpdater.DownloadAsync", source, StringComparison.Ordinal);
        Assert.Contains("WindowsUpdater.LaunchInstaller", source, StringComparison.Ordinal);
        Assert.Contains("WindowsUpdater.InstallAndExit", source, StringComparison.Ordinal);
        Assert.Contains("AppImageUpdater.ReplaceAsync", source, StringComparison.Ordinal);
    }

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory (the test
    /// host runs with its working directory set to the output folder, where a relative path finds nothing).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A source file of this repository, read from the compiler's path. The existence assertion matters:
    /// a moved or renamed file must fail loudly here rather than let a "does not contain" claim pass on an empty
    /// string.</summary>
    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }
}
