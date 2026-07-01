using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>Open/close = slide + fade of the window's CONTENT (the root Border), not the Window itself.
/// On Linux the window's own geometry/opacity is the compositor's domain (KWin animates map/unmap), so
/// we never animate the Window — we animate a child Border that Avalonia draws into its own surface,
/// which the WM doesn't touch. The window is shown at full (transparent) size first, so KWin's one-shot
/// map/unmap effect plays over transparent pixels and is invisible; then this reveals the content.
///
/// Easing/duration live in XAML (the Border's <c>Transitions</c>: a DoubleTransition on Opacity + a
/// TransformOperationsTransition on RenderTransform); this just flips the target values. <see cref="Out"/>
/// fires <paramref name="done"/> after the fade so the caller can Hide() the window once it's invisible.</summary>
internal sealed class SlideFader(Decorator root)
{
    // Must match the XAML transition durations on the root Border.
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);
    private static readonly TransformOperations Shown  = TransformOperations.Parse("translateX(0)");
    private static readonly TransformOperations Hidden = TransformOperations.Parse("translateX(26px)");

    private IDisposable? _pending;

    /// <summary>Reveal the content (call after the window is shown, i.e. attached to the visual tree —
    /// otherwise Opacity=0 is the initial value and the transition has nothing to animate from).</summary>
    public void In()
    {
        Cancel();
        root.RenderTransform = Shown;
        root.Opacity = 1;
    }

    /// <summary>Fade the content out, then invoke <paramref name="done"/> (typically Window.Hide).</summary>
    public void Out(Action done)
    {
        Cancel();
        root.RenderTransform = Hidden;
        root.Opacity = 0;
        _pending = DispatcherTimer.RunOnce(() => { _pending = null; done(); }, Duration);
    }

    private void Cancel()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
