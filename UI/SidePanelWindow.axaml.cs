using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>The Options/Lighting side panel, to the left of the main flyout. Fully app-painted: the
/// blur is an Avalonia ExperimentalAcrylicBorder and the shadow is a BoxShadow (no DWM), so blur +
/// shadow + content all fade out together — nothing lingers. The window never moves; the slide is a
/// transform of Root. Content/title/back are set by AppController.</summary>
public partial class SidePanelWindow : Window
{
    private const double SlideDip = 26;   // how far the content slides (from the main-facing edge)
    private readonly TranslateTransform _slide = new();
    private DispatcherTimer? _anim;

    public SidePanelWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];   // lets the acrylic border blur the desktop
        Root.RenderTransform = _slide;
        Closing += (_, e) => { e.Cancel = true; Hide(); };   // reused across opens; never truly close
    }

    public void SetBack(ICommand back) => BackButton.Command = back;

    public void SetPanel(string title, object? content)
    {
        TitleText.Text = title;
        ContentHost.Content = content;
    }

    /// <summary>Hidden start state (panel invisible, shifted) — set before Show so there's no flash.</summary>
    public void ResetForOpen()
    {
        _slide.X = SlideDip;
        Root.Opacity = 0;
    }

    /// <summary>Slide + fade the whole panel in from the main-facing edge.</summary>
    public void AnimateIn() => Animate(SlideDip, 0, 0, 1, null);

    /// <summary>Slide + fade the whole panel out (blur + shadow included), then invoke <paramref name="onDone"/>.</summary>
    public void AnimateOut(Action onDone) => Animate(0, 1, SlideDip, 0, onDone);

    private void Animate(double fromX, double fromOpacity, double toX, double toOpacity, Action? onDone)
    {
        _anim?.Stop();
        _slide.X = fromX;
        Root.Opacity = fromOpacity;

        var start = DateTime.UtcNow;
        var dur = TimeSpan.FromMilliseconds(170);
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _anim.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - start) / dur, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);   // cubic ease-out
            _slide.X = fromX + (toX - fromX) * e;
            Root.Opacity = fromOpacity + (toOpacity - fromOpacity) * e;
            if (t < 1) return;
            _anim!.Stop();
            _slide.X = toX;
            Root.Opacity = toOpacity;
            onDone?.Invoke();
        };
        _anim.Start();
    }
}
