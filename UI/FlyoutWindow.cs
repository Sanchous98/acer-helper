using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>Shared base for the app's frameless flyout windows — the main quick-settings flyout
/// (<see cref="MainWindow"/>) and the Options/Lighting drawer (<see cref="SidePanelWindow"/>). Both are
/// app-painted: a translucent card <see cref="Border"/> with its own shadow, shown at full size with the
/// content transparent (so the WM's map/unmap effect plays over nothing), then revealed via
/// <see cref="SlideFader"/>. Each derived window calls <see cref="InitFlyout"/> from its constructor with
/// its root card; close/teardown (Closing handling) stays window-specific since the two differ.</summary>
public abstract class FlyoutWindow : Window
{
    private SlideFader _fader = null!;

    /// <summary>Wire the slide+fade to the root card Border. Call once from the derived constructor.</summary>
    protected void InitFlyout(Border root) => _fader = new SlideFader(root);

    /// <summary>Reveal the card (slide + fade). Call after the window is shown so the Border is attached
    /// (else Opacity=0 is the initial value and the transition has nothing to animate from).</summary>
    public void AnimateIn() => _fader.In();

    /// <summary>Fade the card out, then run <paramref name="onDone"/> (e.g. Hide the window, or re-park it).</summary>
    public void AnimateOut(Action onDone) => _fader.Out(onDone);
}
