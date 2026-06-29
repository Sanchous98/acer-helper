using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>An acrylic side panel (Options / Lighting) pinned to the left of the main flyout.
/// Content/title/back-command are set once via <see cref="Configure"/>; <see cref="SlideX"/> animates
/// its horizontal position so it slides out from (or back behind) the main window.</summary>
public partial class SidePanelWindow : Window
{
    private DispatcherTimer? _anim;

    public SidePanelWindow()
    {
        InitializeComponent();
        // NOT topmost: the main flyout is, so during the slide this passes behind it.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
    }

    public void Configure(string title, object? content, ICommand back)
    {
        TitleText.Text = title;
        ContentHost.Content = content;
        BackButton.Command = back;
    }

    /// <summary>Animate X from <paramref name="fromX"/> to <paramref name="toX"/> (physical px) over a
    /// short ease-out, keeping Y fixed. Invokes <paramref name="onDone"/> when finished.</summary>
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
