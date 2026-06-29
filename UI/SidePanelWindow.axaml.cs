using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>An acrylic/Mica side panel (Options / Lighting) pinned to the left of the main flyout.
/// It physically slides: <see cref="SlideX"/> animates the window's X. Mica (not acrylic) is used so
/// that while it slides behind the main window it is occluded (no bleed-through) and the compositor
/// moves it smoothly.</summary>
public partial class SidePanelWindow : Window
{
    private DispatcherTimer? _anim;

    public SidePanelWindow()
    {
        InitializeComponent();
        // Mica: samples only the wallpaper (not the windows behind), so the main window occludes this
        // one as it slides behind it; and DWM moves a Mica window smoothly (no per-move blur recompute).
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
        ];
        Opened += (_, _) => WindowEffects.RoundCorners(this);   // round the backdrop's corners
        Closing += (_, e) => { e.Cancel = true; Hide(); };       // reused across opens; never truly close
    }

    public void SetBack(ICommand back) => BackButton.Command = back;

    public void SetPanel(string title, object? content)
    {
        TitleText.Text = title;
        ContentHost.Content = content;
    }

    /// <summary>Animate the window's X from <paramref name="fromX"/> to <paramref name="toX"/> (physical
    /// px), Y fixed, then invoke <paramref name="onDone"/>.</summary>
    public void SlideX(int fromX, int toX, int y, Action? onDone)
    {
        _anim?.Stop();
        Position = new PixelPoint(fromX, y);

        var start = DateTime.UtcNow;
        var dur = TimeSpan.FromMilliseconds(180);
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _anim.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - start) / dur, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);   // cubic ease-out
            Position = new PixelPoint((int)(fromX + (toX - fromX) * e), y);
            if (t >= 1) { _anim!.Stop(); onDone?.Invoke(); }
        };
        _anim.Start();
    }
}
