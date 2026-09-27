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

    /// <summary>THE FRAME'S HEIGHT IS FIXED TO THE HOME PAGE, SO NAVIGATION NEVER RESIZES THE WINDOW. Both page
    /// grids live in the one card <c>Panel</c> and are swapped by <c>RenderTransform</c> alone — a RenderTransform
    /// does NOT remove a page from layout — so the window used to size to whichever page was measured. First it
    /// sized to the TALLER of the two (blank at the bottom of the shorter page), then a collapse-on-disarm fix
    /// made it size to the ACTIVE one, which removed the blank but made the window CHANGE HEIGHT on every
    /// navigation — the owner's report ("разницы по высоте быть не должно", Home is taller than Options).
    ///
    /// THE FIX IS A FIXED FRAME, AND THE HEIGHT IS HOME'S CONTENT HEIGHT — NOT THE VIEWPORT IT WAS HANDED. Home's
    /// body is a <c>ScrollViewer</c> (id <c>HomeScroll</c>), so <c>HomePage.DesiredSize.Height</c> reports the
    /// slot the body was given, not the content it wants: the first fixed-frame fix pinned one chrome-height
    /// SHORT, which made BOTH pages scroll (measured on the owner's box: Home clipped ~87 px, the Tuning/undervolt
    /// drawer ~154 px — the owner's "у тебя скролл на экране с даунвольтом"). The natural height is the body's
    /// scrollable content EXTENT plus the chrome around it (<c>frame − viewport</c>), and it must be read on a
    /// STABLE pass — the sections are built and populated over the first few layout passes, so an early reading is
    /// undercounted. <c>LockFrameHeight</c> therefore requires the candidate to agree on two consecutive passes,
    /// then assigns it to <c>Height</c> and switches the window to <c>SizeToContent="Width"</c>, so from then on
    /// it sizes only its width and cannot be resized by a page swap. Because the height is locked, the old
    /// collapse-on-disarm machinery is gone: both pages stay measured, which is also what the slide needs (the
    /// incoming page must be visible for its transition to fire at all).
    ///
    /// MUTATION: delete the <c>Height = _heightCandidate</c> assignment or the
    /// <c>LayoutUpdated += LockFrameHeight</c> hook (nothing pins the frame, it sizes to a page again), drop the
    /// <c>SizeToContent = SizeToContent.Width</c> line (a later layout pass can re-shrink the window), or go back
    /// to reading <c>HomePage.DesiredSize.Height</c> instead of the body's extent (the frame comes back one
    /// viewport short and the asserts below name the extent go red). Putting a static <c>IsVisible="False"</c> on
    /// either page grid would kill the incoming page's transition, so the XAML must not collapse a page.</summary>
    [Fact]
    public void TheWindowHeightIsFixedToTheHomePage()
    {
        var window = Source("UI/MainWindow.axaml");
        var code = Source("UI/MainWindow.axaml.cs");

        // The XAML must open as content-sizing so the first HOME measurement is unconstrained.
        var tagStart = window.IndexOf("<Window ", StringComparison.Ordinal);
        Assert.True(tagStart >= 0, "the window element is gone from MainWindow.axaml");
        var windowTag = window[tagStart..window.IndexOf('>', tagStart)];
        Assert.Contains("SizeToContent=\"WidthAndHeight\"", windowTag, StringComparison.Ordinal);

        // The home body must be NAMED, and its content EXTENT (not the viewport DesiredSize) is what sizes the
        // frame — a viewport reading is exactly the scroll bug this guard was written for.
        Assert.Contains("x:Name=\"HomeScroll\"", window, StringComparison.Ordinal);
        Assert.Contains("HomeScroll.Extent.Height", code, StringComparison.Ordinal);

        // The frame is locked to the measured candidate on a stable layout pass...
        Assert.Contains("LayoutUpdated += LockFrameHeight", code, StringComparison.Ordinal);
        Assert.Contains("private void LockFrameHeight", code, StringComparison.Ordinal);
        Assert.Contains("Height = _heightCandidate", code, StringComparison.Ordinal);
        Assert.Contains("_stablePasses < 2", code, StringComparison.Ordinal);

        // ...and the window then STOPS content-sizing its height, so no later pass can resize it.
        Assert.Contains("SizeToContent = SizeToContent.Width", code, StringComparison.Ordinal);

        // The per-page collapse is gone: both pages stay measured, so nothing resizes the window on navigation.
        Assert.DoesNotContain("ShowOnlyActivePage", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HomePage.IsVisible", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawerPage.IsVisible", code, StringComparison.Ordinal);

        // The XAML must NOT collapse a page statically: a collapsed incoming page would never animate in.
        Assert.DoesNotContain("IsVisible=\"False\"", window, StringComparison.Ordinal);
    }

    /// <summary>NO DRAWER PAGE SCROLLS — the owner's rule, twice stated ("не должно быть прокрутки",
    /// "у тебя скролл на экране с даунвольтом"). Options and Lighting are hosted bare (they lay their content
    /// out to fill the frame), and Tuning — whose content is genuinely variable — is the ONE page with a
    /// ScrollViewer, kept only as a safety valve for a long result. The frame is tall enough for Tuning's
    /// content at rest (see <c>TheWindowHeightIsFixedToTheHomePage</c>), so that safety valve does not engage
    /// today; this guard pins that the bare hosting is what the shell still does.</summary>
    [Fact]
    public void OnlyTuningKeepsAScrollViewerInTheDrawer()
    {
        var window = Source("UI/MainWindow.axaml");

        // Options and Lighting are NOT wrapped in a ScrollViewer: their content fills the fixed frame.
        Assert.DoesNotContain("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\" VerticalScrollBarVisibility=\"Auto\"\n                          IsVisible=\"{Binding IsOptionsPage}\"", window, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding OptionsPage}\"  IsVisible=\"{Binding IsOptionsPage}\"", window, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding LightingPage}\" IsVisible=\"{Binding IsLightingPage}\"", window, StringComparison.Ordinal);

        // Tuning keeps its ScrollViewer (the variable-height page).
        Assert.Contains("IsVisible=\"{Binding IsTuningPage}\"", window, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\" VerticalScrollBarVisibility=\"Auto\"", window, StringComparison.Ordinal);
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

    /// <summary>THE TUNING PAGE FITS THE FIXED FRAME BY RE-LAYOUT, NOT BY A SCROLLBAR. The frame is exactly Home's
    /// natural height (see <c>TheWindowHeightIsFixedToTheHomePage</c>), so the Tuning drawer must arrange its
    /// controls to fit that height. This pins the two structural arrangements the fit rests on:
    ///   * the GPU clock card and the GPU-mode/MUX card are ONE "GPU" card (the merge removed a whole card's
    ///     padding + its own section title + its bottom margin), while the MUX half keeps its own conditional
    ///     visibility and semantics; and
    ///   * TuningView is NOT itself wrapped in a ScrollViewer (its safety-valve ScrollViewer lives in the shell,
    ///     around it), so a future edit cannot hide an overflow inside the page view.
    /// A source guard, because no test in this suite can lay out a window (the same technique as the neighbours
    /// above).</summary>
    [Fact]
    public void TheTuningPageFitsTheFrameByRelayoutNotAScrollViewer()
    {
        var xaml = Source("UI/Views/TuningView.axaml");

        // The GPU clock card and the MUX half are ONE card: a single "GPU" section header and the MUX block nested
        // inside the SAME card StackPanel, whose visibility ORs the two capabilities.
        Assert.Contains("IsVisible=\"{Binding HasGpuOrMux}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{l:Tr GPU}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{l:Tr GPU mode}\"", xaml, StringComparison.Ordinal);   // no second full header
        Assert.Contains("<StackPanel Spacing=\"4\" IsVisible=\"{Binding HasGpuMux}\">", xaml, StringComparison.Ordinal);

        // The MUX semantics survive the merge: the selector IS the switch, one stable NoWrap line, the note shares
        // the selector's row.
        Assert.Contains("GpuMux.SelectedIndex", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GpuMuxState\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GpuMuxNote\"", xaml, StringComparison.Ordinal);

        // TuningView carries no ScrollViewer of its own — the page lays its cards out to fit.
        Assert.DoesNotContain("<ScrollViewer", xaml, StringComparison.Ordinal);
    }

    /// <summary>THE OPTIONS ROWS KEEP THEIR OWN SPACING, they do not fill the frame by stretching. A one-column
    /// UniformGrid was briefly used to fill the taller fixed frame, but it divides the frame between the rows and
    /// so widens the gaps — the owner's "надо вернуть небольшие отступы у параметров". This pins the plain
    /// spacing panel and the absence of the stretching layout, so the gaps cannot silently drift again.</summary>
    [Fact]
    public void TheOptionsRowsKeepASmallFixedSpacingAndDoNotStretchToFillTheFrame()
    {
        var xaml = Source("UI/Views/OptionsView.axaml");

        Assert.Contains("<StackPanel Spacing=\"12\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<UniformGrid Columns=\"1\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VerticalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
    }
}
