# Popups (and the two modals) on a scaled XWayland

What was measured, what the numbers actually fit, what the app does about it, and when that can be deleted.
The code is `UI/PopupPlacement.cs`; the sibling on the window's side is `MainWindow.Reanchor`.

## The measurement

Session: one 3440x1440 output under KWin with `[Xwayland] Scale=1.25`. The flyout is placed by `Reanchor` in
**device** pixels.

| | measured |
|---|---|
| flyout window | `585x1292+2830+65` |
| in-window `ComboBox` dropdown (a popup window of its own) | `226x207+2526+228` |
| where that dropdown belonged | about `+3092+241` |

The shortfall is (566, 13), and 566 = 2830 x 0.2 while 13 = 65 x 0.2 — i.e. exactly
`windowPosition * (1 - 1/scale)`. That is the observation the first attempt at this bug was built on: a
compensation added to every popup through `Popup.HorizontalOffset` / `VerticalOffset`. It does not work, and it
cannot: see "why not an offset" below.

## The mechanism the numbers fit

Avalonia computes a popup's ideal rectangle in **device** pixels — the parent window's device origin plus the
anchor, both scaled — and then clamps that rectangle into the screen's working area
(`ManagedPopupPositioner.Calculate`, the `SlideX`/`SlideY`/`FlipX`/`FlipY`/`Resize*` adjustments, whose bounds
come from `Screen.WorkingArea`). The clamp is right whenever the working area and the geometry are in the same
space. It is systematically wrong when the session reports its screens in **logical** units
(3440 / 1.25 = 2752 wide) while windows and anchors are in device pixels:

```
x = min(ideal.X, workingArea.Right - popupWidth) = min(3092, 2752 - 226) = 2526   ← measured, exactly
y = min(ideal.Y, workingArea.Bottom - popupHeight) = min(228, ...) = 228           ← not clamped
```

Every popup of a window that hugs the screen's right edge is dragged back to `2752 - popupWidth`, whatever
control it belongs to. The reported 566 px is what that displacement happens to equal *in that state*: the flyout
sits at `screenWidth - width - gap` (2830) while the reported area's right edge is `screenWidth / 1.25` (2752), so
`3092 - 2526` comes out as `2830 * 0.2`. It is a property of that state's geometry, not of popups — which is why
the formula was mistaken for the cause. The 13 px on y is a layout-estimate artefact: the y was never clamped.

## Why not an offset

`HorizontalOffset`/`VerticalOffset` move the *ideal* rectangle. The clamp then runs on the moved rectangle and
pins it to `workingArea.Right - popupWidth` anyway — the same position it was already at. Measured against the
shipped wiring in the harness: the popup did not move a single pixel.

## The fix

`UI/PopupPlacement.Install()` (called once from `App.OnFrameworkInitializationCompleted`) registers a class
handler on `Popup.IsOpenProperty`, so every popup of every window is covered — including the ones the Fluent
theme builds for `ComboBox` inside its own templates. When a popup opens, and the owning window is on a session
where the reported screens disagree with the window's own space, the popup's
`PlacementConstraintAdjustment` is cleared: the position Avalonia computed stands. `UI/Views/ConfirmDialog` and
`UI/Views/FanCurveWindow` do the same thing for the clamp that `WindowStartupLocation="CenterOwner"` applies to
their position (Avalonia centres correctly in device pixels and then clamps the result against the same
wrong-space area, which pulls a dialog over the right-edge flyout roughly 600 px to its left).

The gate is deliberately narrow, and it is four facts, not one:

* **Linux** and a **Wayland-driven XWayland session** (`WAYLAND_DISPLAY` set) and a **scale other than 1** — the
  session the measurement came from, and nothing wider; and
* `ScreensDisagreeWithTheWindow(...)`: the window's own device rectangle must not fit the reported working area.
  A compositor keeps a managed window inside the real output, so a window that looks out of bounds is a window
  being compared against an area in another space. This is the fact that makes the correction *inert where it is
  not needed* — and therefore safe to ship without being able to reproduce the broken state. (Risk, stated: a
  compositor that allows a managed window to hang off the screen would make this predicate fire where the area is
  in fact correct; only on Linux + Wayland + a scaled session.)

## How it was verified

* **Arithmetic, in the suite** (`tests/AcerHelper.Tests/PopupPlacementTests.cs`): the clamp model reproduces the
  measured `2526` in the reported state and `3092` in today's state with the same rule and no free parameter; the
  decision function is asserted on both states, plus the per-axis cases.
* **On the machine, in this session** — with a throwaway harness that links `UI/PopupPlacement.cs` itself (not a
  copy) and drives a real `ComboBox` on the real X server:

  | phase | window | popup | reading |
  |---|---|---|---|
  | A | 468x400 at (2000,300) | `200x129+2125+402` | = window + the control's anchor, exact |
  | B | same at (2855,355) | `200x129+2980+457` | exact, and *not* clamped even though its right edge (3180) is past 2752 |
  | E | the flyout's own window style, at (2855,355) | `200x129+2980+457` | exact |
  | F | E **with the shipped correction installed** | `200x129+2980+457` | byte-identical to E: inert, as designed |
  | W | window at (3350,300) (KWin kept it at 2855) | `200x129+2980+402` | exact |

  The harness also printed what this session reports today: `renderScaling=1.25`, screen
  `bounds=0,0,3440,1440`, `workingArea=0,0,3440,1383` — **device** pixels, panel included. That is why the clamp
  does not misfire here today, and why the reported displacement could not be reproduced: the two sessions differ
  in what the platform reports for the screens, not in how popups are placed.

* **A live dropdown of the app**, seen on 2026-09-21 with the flyout at (2855,355), measured `226x207+3214+575` —
  i.e. exactly its control's anchor, unclamped.

## The drop-down's own geometry (the owner's other report)

Separate defect, same widget, and the one the owner actually reported: *"вот прямо сейчас по центру от элемента
выпадающего списка. Это немного некорректно, элемент и сам выпадающий список должны быть одной ширины"* — the open
list is centred on its field and a different width from it, where a Win32/WinUI drop-down is the width of its box
and flush with its left edge. Measured here: a drop-down `226x207` under a field whose own `MinWidth` is 110 DIP.

The cause is in the theme, not in the views. Read out of the installed Fluent theme (`Avalonia.Themes.Fluent.dll`
12.1.2; the element names are UTF-16 literals in its compiled XAML, `strings -e l`): the ComboBox template's
`Popup` (`PART_Popup`) declares `PlacementTarget="Background"`, `MaxHeight="{TemplateBinding MaxDropDownHeight}"`
and `MinWidth="{Binding Bounds.Width, RelativeSource={RelativeSource TemplatedParent}}"` — and **no `Placement`**,
and no offsets. `Popup.Placement`'s default is `PlacementMode.Bottom` (checked by reflection against the same
package: `PlacementProperty`'s default value), and `Bottom` means *centred on the target*. `MinWidth` alone
therefore gives a list that is at least as wide as the field and grows symmetrically around it for any item wider
than the field — which is exactly what is seen.

The fix is one app-level style (`UI/App.axaml`, at the end of `Application.Styles`), app-level so that it reaches
every ComboBox in every view and any added later, rather than four local edits:

```xml
<Style Selector="ComboBox /template/ Popup#PART_Popup">
  <Setter Property="Placement" Value="BottomEdgeAlignedLeft" />
  <Setter Property="Width" Value="{Binding Bounds.Width, RelativeSource={RelativeSource TemplatedParent}}" />
</Style>
```

`Width` pins the list to the field's width (`MinWidth` alone only sets a floor), and `Placement` states the
alignment directly — so the list is flush with the field even if the width binding has not settled on the first
layout pass. The binding is the same idiom the template itself uses one line above for `MinWidth`, so it resolves
against the ComboBox the popup is templated into.

Verified: by that API evidence and by the source guard in the suite
(`PopupPlacementTests.TheDropDownIsTheWidthOfItsBox`). NOT yet observed on screen — the session was locked while
this was written, and the running instance is being measured by the coordinator.

## The flyout's own corner: which space the working area is in

`MainWindow.Reanchor` used to carry the same kind of assumption this file is about, one level up. It multiplied
`Screen.WorkingArea` by the scale, on the first measurement (the area came back as 2752 on a 3440-wide output, so
it had to be logical). On 2026-09-21 the SAME session reported the same area as `3440x1383` — **device** pixels,
panel included — and the same multiplication asked for `x = 3440*1.25 - 585 - 25 = 3690`: past the right edge of
the output, an X window launched off the screen (measured with `xwininfo`: the ask 3690, and KWin clamping the
mapped window back to 2855). Nothing about the display, the scale or the layout had changed; only what the
platform reported. So the space is now *measured*:

* `PopupPlacement.SpaceOf(workingArea, screenBounds, scale)` compares the area against the output's own rectangle
  (`Screen.Bounds`), which is the one report that is the same in both spaces. The **width** is the axis compared
  and not the height, because a panel strips the bottom (1383 against 1440 here) and never the width. The
  fallback is *device* pixels — the space `Position` is interpreted in — because an area that matches the output
  in neither space is not evidence for scaling it.
* `PopupPlacement.Settle(...)` places the chosen corner, reads the position back, and falls back to the other
  space's corner when the read-back is evidence that it should. "Evidence" is deliberately not a plain
  inequality: X11's window implementation keeps its position in a nullable field filled from the compositor's
  `ConfigureNotify`, so a read taken immediately after the assignment is the value from *before* the call —
  `(0,0)` for a window that has never been mapped. Acting on that would ask for 3690 on every first open. The
  read-back therefore counts only when it lands OUTSIDE the area we aimed into: a stale value never does, and a
  real clamp always does (a clamp leaves the window inside the area it clamps into). All four outcomes are
  asserted in the suite through an injected `place`.

Measured on the running instance built from this tree (2026-09-21, `xwininfo -id <flyout>`):

| | ask (what the code computed) | what the X server reports |
|---|---|---|
| before | `+3690` (working area × scale) | `585x1027+2855+355`, clamped by KWin on map |
| after | `+2830` = 3440 − 585 − 20×1.25 | `585x1027+2830+355`, **IsViewable, no clamp** |

The x is the whole of the acceptance: the flyout's left edge is a visible 20 DIP from the output's right edge
again. The y is 355 where the arithmetic says `1383 - 1027 - 25 = 331`; the 24 px is not the space correction but
`SizeToContent` growing the window after the anchor (the height used at anchor time was 1003 device), so the
bottom gap reads 58 px rather than 25 — pre-existing, inside the screen, and harmless.

## Removing it

Delete `UI/PopupPlacement.cs`, its `Install()` call in `App.axaml.cs`, the `Opened` hooks in the two modals, and
`PopupPlacementTests`, when either of these is true:

1. Avalonia keeps the popup's geometry and the reported screens in one space (open a `ComboBox` dropdown and
   compare its window against its control: if it sits under the control, the correction is a no-op); or
2. the session stops reporting scaled screens (`Screens.Primary.WorkingArea` against the window's device
   position: on 2026-09-21 they agree, so this code does nothing here).

`ScreensDisagreeWithTheWindow` is the single line that decides it, and it is the line to look at first if a popup
ever lands somewhere unexpected on a scaled session.

The `ComboBox` drop-down style in `UI/App.axaml` is NOT part of this workaround and does not go with it: it asks
for a Win32-shaped drop-down (the width of its field, flush with it) on every platform and every session, which
is a design decision rather than a compensation for a toolkit bug. Nothing in it depends on a scale, a session or
a space.
