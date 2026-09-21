using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace AcerHelper.UI;

/// <summary>
/// POPUPS AND THE TWO <c>CenterOwner</c> MODALS ON A SCALED XWAYLAND — the measured bug, the mechanism the
/// numbers actually fit, and the compensation for it. Read with <see cref="MainWindow.Reanchor"/>, which is the
/// same disagreement about the same session seen from the window's own side.
///
/// THE MEASUREMENT (the owner's machine: a 3440x1440 output under KWin with <c>[Xwayland] Scale=1.25</c>; the
/// flyout is placed by <see cref="MainWindow.Reanchor"/> in device pixels). With an in-window ComboBox dropdown
/// open, the popup window measured <c>226x207+2526+228</c> where it belonged at about <c>+3092+241</c>: 566 px
/// short on x, and 566 = 2830 * (1 - 1/1.25), the flyout's own x times (1 - 1/scale). Two later sessions of the
/// same app on the same machine (2026-09-21, measured with a harness that drives the shipped file — see
/// docs/ui-popup-placement.md) put the very same dropdown at <c>+3214</c>, i.e. exactly where it belonged, with
/// the flyout at x = 2855. So the displacement is NOT a standing property of the session; it is a property of
/// the session's REPORTED SCREEN GEOMETRY, which changed between the two days.
///
/// THE MECHANISM (this is the part that matters, and it is the toolkit's, not our arithmetic): Avalonia's popup
/// positioner computes the popup's ideal device-pixel rectangle correctly — the parent window's device origin
/// plus the anchor, both scaled to device pixels — and then CLAMPS it into the screen's working area:
/// <c>x = min(ideal.X, workingArea.Right - popupWidth)</c> (SlideX in <c>ManagedPopupPositioner</c>), the same
/// for y, plus a flip when the ideal does not fit. That is right whenever the working area and the geometry are
/// in the same space. When the session reports its screens in LOGICAL units (2752x1152 on this output) while the
/// windows and their anchors are in DEVICE pixels, the clamp is comparing two different spaces, and for a window
/// that hugs the screen's right edge it fires every time: every popup of the flyout is dragged back to
/// <c>2752 - popupWidth</c>. For the measured dropdown that is <c>2752 - 226 = 2526</c> — the measurement, exactly,
/// with no free parameter. The y did not move (228 = the ideal; the area's logical bottom, 1152, is far below it),
/// which is why the y difference against the estimate above is a layout-estimate artefact rather than a second
/// displacement; and it is also why the x error LOOKS like <c>windowPosition * (1 - 1/scale)</c>: in that state
/// <c>workingArea.Right - popupWidth</c> happens to equal <c>idealX - 566</c>, because the window sits where
/// <c>3440 - width - gap</c> and the area's right edge is <c>3440 / 1.25</c>. A formula fitted to one state; the
/// clamp is the thing that produced it.
///
/// WHY THIS IS NOT A NUDGE BY <c>windowPosition * (1 - 1/scale)</c>. Adding that offset through
/// <c>Popup.HorizontalOffset</c> cannot survive: the offset moves the ideal, and the clamp then runs on the moved
/// ideal and pins the popup to <c>workingArea.Right - popupWidth</c> regardless — the same position it was
/// already at. Measured against the shipped wiring in the harness (phase C), the popup did not move by a single
/// pixel. What fixes the placement is to stop the misfiring clamp: <see cref="Install"/> clears
/// <see cref="Popup.PlacementConstraintAdjustment"/> on the popups of a window whose reported working area is in
/// the other space, so the position Avalonia computed stands. That is inert wherever the two spaces agree (no
/// clamp would have applied, so removing it changes nothing — verified in the harness on 2026-09-21, phases A,
/// B, E and W, where the popup sat exactly under its control with and without this code) and it is the whole fix
/// where they do not.
///
/// THE GATE is deliberately narrow: Linux, a Wayland-driven XWayland session (<c>WAYLAND_DISPLAY</c> set is the
/// cheapest honest signal that the session is Xwayland under a compositor, which is where a scaling that the X11
/// screens do not share comes from), a scale other than 1, and — the part that makes this safe to ship without
/// being able to reproduce the broken state — <see cref="ScreensDisagreeWithTheWindow"/>: the window's own
/// device rectangle must not fit the working area it is being compared against. On the Windows TFM, on a native
/// Wayland session, on an unscaled X11 session, and on this machine's current state (which reports 3440x1383,
/// i.e. device pixels) every entry point here returns without touching anything.
///
/// REMOVE THIS WHEN: Avalonia keeps the popup's geometry and the screens in one space, or the session stops
/// reporting scaled screens. Both are checkable from outside this file: open a ComboBox dropdown and compare its
/// window against its control (measured on 2026-09-21: exact), and print
/// <c>Screens.Primary.WorkingArea</c> against the window's device position (measured: 3440x1383 device). If
/// either stops being true, the compensation is a no-op waiting to be deleted —
/// <see cref="ScreensDisagreeWithTheWindow"/> is the line that decides it.
/// </summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Do the session's reported screens live in a different space than the window does?
    ///
    /// The test is the window's own rectangle: a compositor keeps a managed window inside the real output, so a
    /// window whose DEVICE rectangle does not fit the reported working area is a window being compared against
    /// an area that is not in device pixels. That is the state the measurement above was taken in
    /// (<c>2830 + 585 = 3415 &gt; 2752</c>), and it is not the state this machine reports today
    /// (<c>2855 + 585 = 3440</c>, the working area's right edge exactly).
    ///
    /// Pure and total, so the decision can be tested without a window; the tests call it with the measured
    /// numbers of both states.
    /// </summary>
    internal static bool ScreensDisagreeWithTheWindow(PixelRect workingArea, PixelPoint windowPosition, PixelSize windowSize)
        => windowPosition.X + windowSize.Width > workingArea.Right
        || windowPosition.Y + windowSize.Height > workingArea.Bottom;

    /// <summary>Is this the kind of session this workaround exists for at all? Linux + a Wayland-driven XWayland
    /// session + a scale other than 1 — the three facts the measurement was taken under, and nothing wider.</summary>
    internal static bool ScaledXWayland(double scale)
        => OperatingSystem.IsLinux()
           && scale != 1.0
           && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

    /// <summary>Wire the popup correction for the whole app. Called once, from the app's startup.
    ///
    /// A class handler on <see cref="Popup.IsOpenProperty"/> rather than a style or a subclass: whether a popup
    /// needs it depends on the OWNING WINDOW's position and size at the moment it opens, which no style setter
    /// can express, and a subclass would have to be threaded through every ComboBox in every view — including
    /// the ones the Fluent theme builds inside its own templates. The handler runs after Popup's own (the one
    /// that opens the host and places it), and changing the property re-runs the placement.</summary>
    public static void Install()
        => Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(static (popup, e) =>
        {
            if (e.NewValue is true) Correct(popup);
        });

    private static void Correct(Popup popup)
    {
        // Window, not TopLevel: in Avalonia 12 a top-level's position lives on Window only, and a popup's parent
        // is always one.
        if (TopLevel.GetTopLevel(popup) is not Window owner) return;
        if (!ScaledXWayland(owner.RenderScaling)) return;
        if (owner.Screens.Primary is not { } screen) return;
        if (!ScreensDisagreeWithTheWindow(
                screen.WorkingArea, owner.Position, PixelSize.FromSize(owner.ClientSize, owner.RenderScaling)))
            return;

        // Trust the computed placement: the clamp that would otherwise run compares this popup's device-pixel
        // rectangle against a working area in the other space, and pins every popup of this window to that
        // area's right/bottom edge — the displacement above. Nothing is replaced but the adjustment, so the
        // popup still opens where its control asked for it (and a dropdown anchored inside a window that is
        // itself on screen stays on screen: the anchor is a control's position, not the screen's edge).
        popup.SetCurrentValue(Popup.PlacementConstraintAdjustmentProperty, PopupPositionerConstraintAdjustment.None);
    }

    /// <summary>Put a <c>CenterOwner</c> dialog where it belongs in the same state. Avalonia's centring is right
    /// and in device pixels, but the result is then clamped against <c>Screen.WorkingArea</c> — the same area,
    /// in the same wrong space — so a dialog over the flyout (which hugs the right edge) is dragged back to
    /// <c>workingArea.Right - dialogWidth</c> — roughly 600 px left of the window it belongs to, floating over
    /// whatever else is on screen. Recomputing the centre in device pixels and assigning it after the dialog is
    /// shown is the fix; the gate makes it inert wherever the two spaces agree.</summary>
    internal static void RecenterDialog(Window dialog)
    {
        if (dialog.Owner is not Window owner) return;
        var scale = owner.RenderScaling;
        if (!ScaledXWayland(scale)) return;
        if (owner.Screens.Primary is not { } screen) return;
        if (!ScreensDisagreeWithTheWindow(
                screen.WorkingArea, owner.Position, PixelSize.FromSize(owner.ClientSize, scale)))
            return;

        var ownerSize = PixelSize.FromSize(owner.ClientSize, scale);      // client == frame: no decorations here
        var dialogSize = PixelSize.FromSize(dialog.ClientSize, scale);
        dialog.Position = new PixelPoint(
            owner.Position.X + (ownerSize.Width - dialogSize.Width) / 2,
            owner.Position.Y + (ownerSize.Height - dialogSize.Height) / 2);
    }

    // ---- WHICH SPACE ARE THE SCREENS REPORTED IN? Measured, because it is not a property of the display. ----

    /// <summary>The two conventions a session's screens can arrive under. <c>Window.Position</c> is always
    /// interpreted in DEVICE pixels (the X11 root's space, where the flyout measured 3440x1440 wide), so the only
    /// open question is which space the screens were reported in.</summary>
    internal enum AreaSpace { DevicePixels, LogicalPixels }

    /// <summary>How far the reported working area may differ from the output's own rectangle and still count as
    /// the same rectangle: a panel is a few percent of the axis it sits on (this session's bottom panel is 57 of
    /// 1440), while the two candidate spaces differ by the whole scale factor — 25% here — so the gap between
    /// "same rectangle, panel" and "different rectangle, scaled" is wide.</summary>
    private const double SameWidthTolerance = 0.05;

    /// <summary>
    /// WHICH SPACE THE WORKING AREA IS IN, DECIDED BY OBSERVATION RATHER THAN ASSUMPTION.
    ///
    /// The measurement this replaces inferred the space from a single reading (the area came back as 2752 on a
    /// 3440-wide output, so it must be logical, so multiply by the scale). The same session has since reported
    /// the same area as 3440 — DEVICE pixels — which the same arithmetic turned into 3690, past the right edge,
    /// and the flyout was launched off the screen. So the space is a property of what the toolkit REPORTS at that
    /// moment, and asking it twice is the only way to know: this compares the working area against THE OUTPUT'S
    /// OWN RECTANGLE (<paramref name="screenBounds"/>), the one rectangle of the pair that is the same in both
    /// reports (XRandR describes the output in pixels, and a managed window is inside it).
    ///
    /// The area's WIDTH is the axis compared, not its height: a panel strips pixels off the bottom on most
    /// desktops, so the height legitimately differs from the output's while the width does not.
    ///
    /// Total and pure, so both branches and the fallback are asserted with the numbers this machine reported.
    /// The fallback is DEVICE pixels — the space <c>Position</c> is interpreted in — because an area that matches
    /// the output in neither space is not evidence for scaling it, and the read-back in <see cref="Settle"/>
    /// remains as the second net.
    /// </summary>
    internal static AreaSpace SpaceOf(PixelRect workingArea, PixelRect screenBounds, double scale)
        => scale != 1.0
           && !SameWidth(workingArea.Width, screenBounds.Width)
           && SameWidth(workingArea.Width * scale, screenBounds.Width)
            ? AreaSpace.LogicalPixels
            : AreaSpace.DevicePixels;

    private static bool SameWidth(double a, double b) => b > 0 && Math.Abs(a - b) <= b * SameWidthTolerance;

    /// <summary>The reported area converted into the DEVICE pixels <c>Position</c> is interpreted in — the
    /// identity when it was already there, and the same factor the window size gets when it was logical.</summary>
    internal static PixelRect InDevicePixels(PixelRect area, AreaSpace space, double scale)
        => space == AreaSpace.DevicePixels
            ? area
            : new PixelRect((int)Math.Round(area.X * scale), (int)Math.Round(area.Y * scale),
                            (int)Math.Round(area.Width * scale), (int)Math.Round(area.Height * scale));

    /// <summary>The anchored corner: the area's bottom-right, inset by the gap, never past its origin (a window
    /// larger than the area is pinned to the area's top-left rather than to a negative coordinate, which the
    /// compositor would then clamp, and the two would fight — the jitter this anchoring exists to avoid).</summary>
    internal static PixelPoint Corner(PixelRect areaDevice, PixelSize window, int gap)
        => new(Math.Max(areaDevice.X, areaDevice.Right - window.Width - gap),
               Math.Max(areaDevice.Y, areaDevice.Bottom - window.Height - gap));

    /// <summary>Is the window's rectangle inside the area we aimed it into? A compositor's clamp always leaves
    /// the window inside the area it clamped into. The other thing a read-back can be is NOT an answer at all —
    /// the platform handing back the position from before the call — and that is why this test rather than a
    /// plain inequality decides whether the read-back is evidence (see <see cref="Settle"/>).</summary>
    private static bool Inside(PixelRect areaDevice, PixelPoint position, PixelSize window)
        => position.X >= areaDevice.X && position.Y >= areaDevice.Y
           && position.X + window.Width <= areaDevice.Right
           && position.Y + window.Height <= areaDevice.Bottom;

    /// <summary>
    /// WHERE TO PUT THE WINDOW — asked for, then read back. <see cref="SpaceOf"/> decides from the two reports
    /// this session makes; this is the second net, for the state where those two reports agree with each other
    /// and are both wrong, because a compositor that puts the window somewhere the area we aimed into cannot
    /// explain is the compositor telling us that area was not the space the window is placed in. The candidate
    /// is asked for, the position that comes back is read, and if it is one the aimed-into area cannot account
    /// for, the other space's candidate is asked for and left in place.
    ///
    /// WHY THE READ-BACK IS NOT SIMPLY COMPARED WITH THE ASK. On this platform it usually is not an answer: X11's
    /// window implementation keeps its position in a nullable field filled from the compositor's ConfigureNotify
    /// events, so immediately after an assignment the read is the value from BEFORE it — <c>(0,0)</c> if this
    /// window has never been mapped. A plain inequality would read that as "the compositor moved us", flip the
    /// space, and ask for the very position the old arithmetic asked for (3690, off the screen) on every first
    /// open. The read-back therefore counts only when it lands OUTSIDE the area we aimed into, which a stale
    /// value never does and a real clamp always does (a clamp puts the window inside the area it clamps into).
    ///
    /// <paramref name="place"/> is the whole of the I/O — in the app it assigns <c>Window.Position</c> and reads
    /// it straight back — so all four outcomes (accepted, stale-ignored, moved-elsewhere-and-fallen-back, and
    /// fallback-that-is-itself-moved) are exercised by the tests with no window, no platform and no session.
    ///
    /// Both candidates are deterministic functions of the reports, and the decision restarts from
    /// <see cref="SpaceOf"/> on every call, so repeated calls cannot oscillate: the fallback is the same corner
    /// every time.
    /// </summary>
    internal static (PixelPoint Position, AreaSpace Space, bool Verified) Settle(
        PixelRect workingArea, PixelRect screenBounds, PixelSize window, int gap, double scale,
        Func<PixelPoint, PixelPoint> place)
    {
        var first = SpaceOf(workingArea, screenBounds, scale);
        var firstArea = InDevicePixels(workingArea, first, scale);
        var asked = Corner(firstArea, window, gap);
        var got = place(asked);
        if (got == asked || Inside(firstArea, got, window)) return (asked, first, got == asked);

        var second = first == AreaSpace.DevicePixels ? AreaSpace.LogicalPixels : AreaSpace.DevicePixels;
        var fallback = Corner(InDevicePixels(workingArea, second, scale), window, gap);
        return (fallback, second, place(fallback) == fallback);
    }
}
