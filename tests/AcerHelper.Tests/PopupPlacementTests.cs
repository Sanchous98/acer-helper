using System.Runtime.CompilerServices;
using Avalonia;

namespace AcerHelper.Tests;

/// <summary>
/// THE SCALED-XWAYLAND POPUP CORRECTION, in three halves: the arithmetic that identifies the bug, the decision
/// that gates the fix, and the wiring that has to be present for either to matter.
///
/// THE ARITHMETIC IS EXECUTED, with the numbers two sessions on the owner's machine produced. The first row is
/// the MEASURED displacement: a popup that belongs at device x=3092 (the flyout's 2830 plus the control's anchor,
/// 262 device px) came out at 2526, and 2526 is exactly <c>workingArea.Right - popupWidth</c> = 2752 - 226 when
/// the session reports its 3440x1440 output in LOGICAL units (3440 / 1.25 = 2752). The second row is the SAME
/// popup in the state the machine reports today (working area 3440 wide, device pixels): the clamp does not fire
/// and the popup is at 3092 — which is where a live dropdown of the real app was measured on 2026-09-21
/// (+3214 with the flyout at 2855, i.e. exactly its control's anchor). One model, both observations, no free
/// parameter: that is what makes this a diagnosis rather than a fitted formula. (The 566 the report quotes as
/// <c>windowPosition * (1 - 1/scale)</c> is real, and it is what the clamp's outcome differs from the ideal by in
/// the first state — 3092 - 2526 — because that window sits at <c>screenWidth - width - gap</c> while the reported
/// area's right edge is <c>screenWidth / 1.25</c>. It is a property of that state's geometry, not of popups.)
///
/// THE DECISION IS EXECUTED TOO: <see cref="UI.PopupPlacement.ScreensDisagreeWithTheWindow"/> is a pure function
/// of the working area, the window's device position and the window's device size, so both measured states are
/// asserted directly — the one that was broken must be recognised, and the one that works today must not be
/// touched. The Windows TFM, a native Wayland session and an unscaled X11 session never reach it (the gate's
/// tokens are pinned by the source guard below).
///
/// WHAT A SOURCE GUARD IS WORTH is stated in AcerLinuxWiringTests and holds here: nothing in this suite can open
/// a popup (that needs a window, a platform and the very session the bug lives on), so the claims "the app
/// installs the correction" and "both modals consult it" are checked the only way left — by reading the source
/// from the compiler's path. What they catch is a correction that computes perfectly and is never called, and a
/// dialog left with a <c>CenterOwner</c> that the AXAML still declares.
/// </summary>
public class PopupPlacementTests
{
    /// <summary>The repository root, from the COMPILER's path: the test host's working directory is its output
    /// folder, where a relative path finds nothing or a stale copy. Same helper, same reason, as
    /// <see cref="AcerLinuxWiringTests.Root"/>.</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relativePath)
    {
        var path = Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>What the popup positioner does with a popup's ideal rectangle: the horizontal clamp, in the
    /// positioner's own order (SlideX). Kept here, in the test, because it is the toolkit's rule being modelled
    /// rather than our code.</summary>
    private static int ClampedX(int intendedX, int popupWidth, int workingAreaRight)
        => Math.Min(intendedX, workingAreaRight - popupWidth);

    private const int FlyoutX = 2830;          // the flyout's device x, from Reanchor's arithmetic
    private const int AnchorDevice = 262;      // the measured dropdown's control, 262 device px into the window
    private const int DropdownWidth = 226;     // measured

    /// <summary>The measured state: the session reports the 3440-wide output in logical units (2752), the clamp
    /// misfires, and the popup lands 566 px short of its control.</summary>
    [Fact]
    public void TheClampIsWhatProducedTheMeasured2526()
    {
        var intended = FlyoutX + AnchorDevice;                       // 3092, where the control asked for it
        var logicalWorkingAreaRight = 2752;                          // 3440 / 1.25: the reported area, in DIP

        var placed = ClampedX(intended, DropdownWidth, logicalWorkingAreaRight);

        Assert.Equal(2526, placed);                                  // measured, with the dropdown open
        Assert.Equal(566, intended - placed);                         // 2830 * (1 - 1/1.25), as reported
    }

    /// <summary>The state this machine reports today (working area 3440x1383, device pixels): the same popup, the
    /// same arithmetic, and the clamp does not fire — which is what a live dropdown of the app measured.</summary>
    [Fact]
    public void TheSamePopupIsUntouchedWhenTheAreaIsInDevicePixels()
    {
        var intended = FlyoutX + AnchorDevice;

        Assert.Equal(3092, ClampedX(intended, DropdownWidth, workingAreaRight: 3440));
    }

    /// <summary>The decision, on the measured state: a 585x1292 flyout at (2830,65) does not fit a 2752x1152
    /// working area, so the area is in the other space.</summary>
    [Fact]
    public void TheBrokenStateIsRecognised()
        => Assert.True(UI.PopupPlacement.ScreensDisagreeWithTheWindow(
            new PixelRect(0, 0, 2752, 1152), new PixelPoint(2830, 65), new PixelSize(585, 1292)));

    /// <summary>And on today's state, where the same test must be false: 2855 + 585 = 3440 is exactly the
    /// reported right edge, and the window's bottom is inside the reported bottom. A correction that fired here
    /// would move a popup that is already in the right place — the reason this gate exists at all.</summary>
    [Fact]
    public void TheWorkingStateIsLeftAlone()
        => Assert.False(UI.PopupPlacement.ScreensDisagreeWithTheWindow(
            new PixelRect(0, 0, 3440, 1383), new PixelPoint(2855, 355), new PixelSize(585, 1027)));

    /// <summary>A window that fits comfortably, and one past only the bottom edge: the predicate is per axis, so a
    /// session whose screens are wrong about height alone is still recognised.</summary>
    [Theory]
    [InlineData(100, 100, 585, 1027, 3440, 1383, false)]     // wholly inside: nothing to correct
    [InlineData(2850, 100, 585, 1400, 3440, 1383, true)]     // hangs past the reported bottom
    [InlineData(3440, 100, 585, 1027, 3440, 1383, true)]     // hangs past the reported right edge
    public void TheDecisionIsAboutTheWindowNotFittingTheReportedArea(
        int x, int y, int width, int height, int areaWidth, int areaHeight, bool expected)
        => Assert.Equal(expected, UI.PopupPlacement.ScreensDisagreeWithTheWindow(
            new PixelRect(0, 0, areaWidth, areaHeight), new PixelPoint(x, y), new PixelSize(width, height)));

    // ---- the wiring, read as text (the same technique as AcerLinuxWiringTests) ----

    /// <summary>The app must install the correction, once, before any window exists. Without this row the
    /// decision could be perfect and unreachable — a correction nothing calls reads exactly like one that works,
    /// until someone opens a dropdown.</summary>
    [Fact]
    public void TheAppInstallsTheCorrectionBeforeAnyWindowExists()
        => Assert.Contains("PopupPlacement.Install();", Source("UI/App.axaml.cs"), StringComparison.Ordinal);

    /// <summary>The popup path must consult the decision AND do something with it: the answer, and the property
    /// the misfiring clamp is switched off through. Either token alone would be satisfied by a correction that
    /// decides and then discards.</summary>
    [Theory]
    [InlineData("ScreensDisagreeWithTheWindow(\n                screen.WorkingArea, owner.Position")]
    [InlineData("Popup.PlacementConstraintAdjustmentProperty")]
    public void ThePopupPathConsultsTheDecision(string required)
        => Assert.Contains(required, Source("UI/PopupPlacement.cs"), StringComparison.Ordinal);

    /// <summary>The gate's three facts, each load-bearing on its own: without the Linux test it fires on the
    /// Windows TFM, without the Wayland test on every scaled X11 session, and without the scale test on sessions
    /// where there is no scaling to disagree about.</summary>
    [Theory]
    [InlineData("OperatingSystem.IsLinux()")]
    [InlineData("\"WAYLAND_DISPLAY\"")]
    [InlineData("scale != 1.0")]
    public void TheGateStaysNarrow(string required)
        => Assert.Contains(required, Source("UI/PopupPlacement.cs"), StringComparison.Ordinal);

    /// <summary>Both modals over the flyout must re-centre themselves. They are the only two windows in the app
    /// with <c>CenterOwner</c>, and the clamp that moves them is invisible from the AXAML that declares it.</summary>
    [Theory]
    [InlineData("UI/Views/ConfirmDialog.axaml.cs")]
    [InlineData("UI/Views/FanCurveWindow.axaml.cs")]
    public void TheCenterOwnerModalsRecentreThemselves(string relativePath)
    {
        var source = Source(relativePath);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"",
                        Source(relativePath.Replace(".axaml.cs", ".axaml")), StringComparison.Ordinal);   // still the reason it is needed
        Assert.Contains("PopupPlacement.RecenterDialog(this)", source, StringComparison.Ordinal);
    }

    // ---- the flyout's own corner: which space the WORKING AREA arrived in, measured ----
    //
    // The two sessions, as the app itself saw them. Session 1: the area arrived as 2752x1152 (the 3440x1440
    // output in LOGICAL units), and multiplying it by the scale was right. Session 2: the same area arrived as
    // 3440x1383 (DEVICE pixels, panel stripped) and the same multiplication asked for x=3690 — a flyout launched
    // off the screen. The width of the area is the axis compared with the output's own rectangle, because the
    // panel strips the bottom and never the width.

    private const int ScreenWidth = 3440;      // the output, device pixels, from the X11 root
    private const int ScreenHeight = 1440;

    /// <summary>Session 1, the logical report: 2752 is not the output's width, 2752 × 1.25 = 3440 is — so the
    /// area is logical and must be scaled. This is the branch the old hard-coded factor got right.</summary>
    [Fact]
    public void ALogicalAreaIsRecognised()
        => Assert.Equal(UI.PopupPlacement.AreaSpace.LogicalPixels,
            UI.PopupPlacement.SpaceOf(new PixelRect(0, 0, 2752, 1152), new PixelRect(0, 0, ScreenWidth, ScreenHeight), 1.25));

    /// <summary>Session 2, the device report — the state that put the flyout off the screen. The area IS the
    /// output's width, so it is already in the space Position is interpreted in and must NOT be scaled: scaling
    /// it is what asked for 3690. The height differs from the output's (1383 against 1440) because of the panel,
    /// which is exactly why the comparison is on the width.</summary>
    [Fact]
    public void ADeviceAreaIsRecognisedDespiteThePanel()
        => Assert.Equal(UI.PopupPlacement.AreaSpace.DevicePixels,
            UI.PopupPlacement.SpaceOf(new PixelRect(0, 0, 3440, 1383), new PixelRect(0, 0, ScreenWidth, ScreenHeight), 1.25));

    /// <summary>The two corrections, as positions: the whole point of the decision. Session 1's corner needs the
    /// scaling (3440 - 585 - 25 = 2830), session 2's corner must not be scaled — it is already asked for at
    /// 2830, and scaling it would ask for 3690, off the right edge of a 3440-wide output.</summary>
    [Fact]
    public void TheCornerIsOnTheScreenInBothStates()
    {
        var window = new PixelSize(585, 1027);
        var logical = UI.PopupPlacement.Corner(
            UI.PopupPlacement.InDevicePixels(new PixelRect(0, 0, 2752, 1152),
                UI.PopupPlacement.SpaceOf(new PixelRect(0, 0, 2752, 1152), new PixelRect(0, 0, ScreenWidth, ScreenHeight), 1.25), 1.25),
            window, 25);
        var device = UI.PopupPlacement.Corner(
            UI.PopupPlacement.InDevicePixels(new PixelRect(0, 0, 3440, 1383),
                UI.PopupPlacement.SpaceOf(new PixelRect(0, 0, 3440, 1383), new PixelRect(0, 0, ScreenWidth, ScreenHeight), 1.25), 1.25),
            window, 25);

        Assert.Equal(new PixelPoint(2830, 388), logical);    // 3440 - 585 - 25, and 1152*1.25 - 1027 - 25
        Assert.Equal(new PixelPoint(2830, 331), device);     // 1383 - 1027 - 25; the panel shows up as a larger y
        Assert.True(device.X + window.Width <= ScreenWidth); // and both are ON the output — 3690 was not
    }

    /// <summary>A 1.0-scale session scales neither way: both candidates are the same corner, so the decision is
    /// inert there by arithmetic rather than by a flag.</summary>
    [Fact]
    public void AtScaleOneBothSpacesAreThesameCorner()
        => Assert.Equal(
            UI.PopupPlacement.InDevicePixels(new PixelRect(0, 0, 3440, 1440), UI.PopupPlacement.AreaSpace.DevicePixels, 1.0),
            UI.PopupPlacement.InDevicePixels(new PixelRect(0, 0, 3440, 1440), UI.PopupPlacement.AreaSpace.LogicalPixels, 1.0));

    /// <summary>THE READ-BACK, THREE WAYS. The candidate the reports chose (session 2's, x=2830) is asked for
    /// and the answer decides: an accepted placement stands; a STALE read — this platform hands back the
    /// position from before the call, (0,0) before the window has ever been mapped — must be ignored rather
    /// than mistaken for a compositor that moved us, because acting on it would ask for 3690, off the screen,
    /// on every first open; and a placement the area we aimed into cannot explain is the evidence that the area
    /// was in the other space, so the other candidate is applied. <c>place</c> is injected, so all three are
    /// asserted with no window, no platform and no session.</summary>
    [Fact]
    public void TheReadBackDecidesOnlyWhenItIsEvidence()
    {
        var area = new PixelRect(0, 0, 3440, 1383);
        var bounds = new PixelRect(0, 0, ScreenWidth, ScreenHeight);
        var window = new PixelSize(585, 1027);

        // Accepted: the compositor left the window where it was asked to put it.
        var accepted = UI.PopupPlacement.Settle(area, bounds, window, 25, 1.25, ask => ask);
        Assert.Equal(new PixelPoint(2830, 331), accepted.Position);
        Assert.Equal(UI.PopupPlacement.AreaSpace.DevicePixels, accepted.Space);
        Assert.True(accepted.Verified);

        // Stale: the platform's pre-assignment value, which on X11 is (0,0) for a window that has never been
        // mapped. It is inside the area we aimed into, so it is not evidence of anything and the placement
        // stands (unverified — the ask was never confirmed — but unchanged).
        var stale = UI.PopupPlacement.Settle(area, bounds, window, 25, 1.25, _ => new PixelPoint(0, 0));
        Assert.Equal(new PixelPoint(2830, 331), stale.Position);
        Assert.Equal(UI.PopupPlacement.AreaSpace.DevicePixels, stale.Space);
        Assert.False(stale.Verified);

        // Evidence: the answer is outside the area we aimed into (3690 + 585 = 4275 > 3440) — the compositor
        // is describing a bigger screen than the one this area belongs to. The LOGICAL candidate is applied in
        // place, and reported unverified because this compositor moves that one too.
        var moved = UI.PopupPlacement.Settle(area, bounds, window, 25, 1.25, _ => new PixelPoint(3900, 677));
        Assert.Equal(UI.PopupPlacement.AreaSpace.LogicalPixels, moved.Space);
        Assert.Equal(new PixelPoint(3690, 677), moved.Position);   // 3440*1.25 - 585 - 25, 1383*1.25 - 1027 - 25
        Assert.False(moved.Verified);
    }

    /// <summary>The flyout's own path must go through the measured decision rather than through the scale
    /// factor, and the read-back must be wired to the window's real position — a <c>place</c> that does not
    /// assign is a verification of nothing.</summary>
    [Theory]
    [InlineData("PopupPlacement.Settle(")]
    [InlineData("screen.WorkingArea, screen.Bounds")]
    [InlineData("place: ask => { Position = ask; return Position; }")]
    public void TheFlyoutAsksTheMeasuredDecision(string required)
        => Assert.Contains(required, Source("UI/MainWindow.axaml.cs"), StringComparison.Ordinal);

    /// <summary>And the removed assumption must not creep back: the old arithmetic scaled the area by the scale
    /// on the way into the corner. If a future edit reintroduces it, the flyout goes off the screen again in the
    /// state this session reports today — which is a state no test on this machine can otherwise reach.</summary>
    [Fact]
    public void TheFlyoutNoLongerScalesTheAreaByHand()
    {
        var source = Source("UI/MainWindow.axaml.cs");
        Assert.DoesNotContain("wa.Width * s", source, StringComparison.Ordinal);
        Assert.DoesNotContain("waX + waW", source, StringComparison.Ordinal);
    }

    /// <summary>THE DROP-DOWN'S GEOMETRY, READ AS TEXT. The owner's report is that the open list is centred on
    /// its field and a different width from it, and the cause is in the theme, not in the views: the Fluent
    /// ComboBox template's Popup (PART_Popup) carries a MinWidth bound to the control's own width and NO
    /// Placement, so it inherits Popup's default — <c>PlacementMode.Bottom</c>, which centres the popup on its
    /// target — and grows past the control for any item wider than it. The fix is one app-level style, so the
    /// rows below hold it to: the popup selector, the left-edge placement, and a width bound to the ComboBox.
    /// </summary>
    [Theory]
    [InlineData("ComboBox /template/ Popup#PART_Popup")]
    [InlineData("Value=\"BottomEdgeAlignedLeft\"")]
    [InlineData("{Binding Bounds.Width, RelativeSource={RelativeSource TemplatedParent}}")]
    public void TheDropDownIsTheWidthOfItsBox(string required)
        => Assert.Contains(required, Source("UI/App.axaml"), StringComparison.Ordinal);
}
