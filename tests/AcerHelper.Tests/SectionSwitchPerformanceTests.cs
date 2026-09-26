using System.Runtime.CompilerServices;

namespace AcerHelper.Tests;

/// <summary>
/// THE SHELL'S SECTION SWITCH ANIMATES, AND THE ANIMATED TREE IS RENDER-CACHED — a performance guard for the
/// jerkiness the owner reported when clicking Options / Overclocking / Lighting ("переключает экран, но делает
/// это как-то дёргано, как-будто поводов для тормозов нет").
///
/// THE SHAPE THIS PINS, and why both halves are needed. The Home and drawer pages are two full-card trees side
/// by side, each offset with a <c>RenderTransform</c> and swapped by the <c>.pushed</c> / <c>.open</c> classes
/// bound to <c>IsDrawerOpen</c>. Each carries several <c>ExperimentalAcrylicBorder</c> cards (Home's
/// Monitor/Profiles/Fans/Battery; Tuning's GPU / MUX / CPU cards, and a ColorPicker per zone on Lighting). A
/// <c>TransformOperationsTransition</c> on such a tree is polished but, WITHOUT a render cache, re-samples the
/// acrylic and re-rasterizes the clip every frame — the owner's hitch, which had no logical cost at all. So the
/// intended design is BOTH:
///   * the push transition is present (motion — the owner asked for it back: "мгновенный сдвиг - это некрасиво");
///   * while the slide runs, both page grids carry a <c>BitmapCache</c>, and only while it runs (MainWindow
///     arms it when <c>IsDrawerOpen</c> flips and clears it once the slide settles). Avalonia's compositor then
///     draws the subtree into an offscreen layer once and blits it per frame, and a RenderTransform change does
///     not invalidate that layer — so the motion is a few blits, not ~15 full re-renders. Scoping it to the
///     slide is what keeps the static page at full fidelity (a cache bakes its layer: grayscale text AA).
///
/// A regression that puts the uncached heavy transition back — the exact defect this file exists for — reddens
/// this guard at the cache asserts. A source guard, because no test in this suite can open a window or measure a
/// frame (the same limit, and the same technique, as <c>GpuMuxTests</c>' shell guard and
/// <c>PopupPlacementTests</c>).
/// </summary>
public class SectionSwitchPerformanceTests
{
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>The navigation invariant must survive: both pages translate by the card's width so exactly one
    /// is on screen, swapped by the <c>IsDrawerOpen</c> classes.</summary>
    [Fact]
    public void ThePagesStillPushByTheCardWidth()
    {
        var window = Source("UI/MainWindow.axaml");

        Assert.Contains("x:Name=\"HomePage\" Classes.pushed", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DrawerPage\" Classes.open", window, StringComparison.Ordinal);
        Assert.Contains("Grid#HomePage.pushed", window, StringComparison.Ordinal);
        Assert.Contains("Grid#DrawerPage.open", window, StringComparison.Ordinal);
        Assert.Contains("Value=\"translateX(-468px)\"", window, StringComparison.Ordinal);
        Assert.Contains("Value=\"translateX(468px)\"", window, StringComparison.Ordinal);
    }

    /// <summary>Motion is back: the push transition exists on the page grids (the owner asked for the polished
    /// slide, not the instant swap).</summary>
    [Fact]
    public void ThePagesAnimateThePush()
    {
        var window = Source("UI/MainWindow.axaml");

        Assert.Contains("<Transitions>", window, StringComparison.Ordinal);
        Assert.Contains("TransformOperationsTransition Property=\"RenderTransform\"", window, StringComparison.Ordinal);
        Assert.Contains("Duration=\"0:0:0.24\"", window, StringComparison.Ordinal);
    }

    /// <summary>THE PERFORMANCE PROPERTY: the animated tree is render-cached for the duration of the slide.
    /// Both page grids get a <c>CacheMode</c>, and it is CLEARED afterwards — so the static page the user reads
    /// keeps full fidelity and the cache never becomes a permanent fidelity tax. The arm is driven by the same
    /// state the transition is (<c>IsDrawerOpen</c>), so the cache and the motion cannot drift apart.</summary>
    [Fact]
    public void TheAnimatedPagesAreRenderCachedOnlyWhileTheSlideRuns()
    {
        var code = Source("UI/MainWindow.axaml.cs");

        // The cache is armed on the slide and applied to BOTH moving grids.
        Assert.Contains("IsDrawerOpen", code, StringComparison.Ordinal);
        Assert.Contains("ArmSlideCache", code, StringComparison.Ordinal);
        Assert.Contains("HomePage.CacheMode = _slideCacheMode", code, StringComparison.Ordinal);
        Assert.Contains("DrawerPage.CacheMode = _slideCacheMode", code, StringComparison.Ordinal);

        // ...and cleared once the slide settles: no permanent fidelity cost on the static page.
        Assert.Contains("DisarmSlideCache", code, StringComparison.Ordinal);
        Assert.Contains("HomePage.CacheMode = null", code, StringComparison.Ordinal);
        Assert.Contains("DrawerPage.CacheMode = null", code, StringComparison.Ordinal);

        // The cache type is Avalonia's BitmapCache (the only CacheMode this toolkit parses — CacheMode.Parse).
        Assert.Contains("BitmapCache _slideCacheMode", code, StringComparison.Ordinal);

        // The XAML must NOT set CacheMode statically: a cache left on permanently would grayscale-antialias the
        // static page's text, which is the fidelity trade this design deliberately scopes away.
        Assert.DoesNotContain("CacheMode=\"BitmapCache\"", Source("UI/MainWindow.axaml"), StringComparison.Ordinal);
    }

    /// <summary>The cache's disarm is a one-shot on the app's shared timer primitive, never a raw
    /// <c>DispatcherTimer</c> (the suite's one-timer rule, and the starvable-idle-priority bug it prevents) —
    /// and a window torn down for a language rebuild disposes it rather than leaving a queued disarm.</summary>
    [Fact]
    public void TheCacheDisarmUsesTheSharedTimerAndIsDisposedWithTheWindow()
    {
        var code = Source("UI/MainWindow.axaml.cs");

        Assert.Contains("new PeriodicSchedule(DisarmSlideCache", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new DispatcherTimer", code, StringComparison.Ordinal);
        Assert.Contains("_slideCache.Dispose()", code, StringComparison.Ordinal);
        Assert.Contains("UnbindNavigation()", code, StringComparison.Ordinal);
    }
}
