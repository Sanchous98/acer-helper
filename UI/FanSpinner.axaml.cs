using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>A standard cooling-fan glyph that spins at a calm, RPM-proportional speed (well below a
/// real fan, so it reads as a smooth indicator with no aliasing — hence no motion blur). The look is
/// all in FanSpinner.axaml; this only maps <see cref="Rpm"/> to rotation and paints with <see cref="Blades"/>.
/// Its own ~30 fps timer runs only while attached and effectively visible.</summary>
public partial class FanSpinner : UserControl
{
    public static readonly StyledProperty<int> RpmProperty =
        AvaloniaProperty.Register<FanSpinner, int>(nameof(Rpm));

    public static readonly StyledProperty<IBrush?> BladesProperty =
        AvaloniaProperty.Register<FanSpinner, IBrush?>(nameof(Blades));

    /// <summary>The fan's speed; drives the rotation. 0 (or unknown) = stopped.</summary>
    public int Rpm { get => GetValue(RpmProperty); set => SetValue(RpmProperty, value); }

    /// <summary>Brush for the blades + hub (typically the muted theme brush).</summary>
    public IBrush? Blades { get => GetValue(BladesProperty); set => SetValue(BladesProperty, value); }

    private const double TickMs = 33;                // ~30 fps; a calm spin needs no more
    private const double DegPerSecPerRpm = 0.12;     // far below real (rpm*6): ≈1 rev/s at 2900 rpm
    private const double MaxDegPerSec = 600;         // calm ceiling (~1.7 rev/s)

    private readonly DispatcherTimer _timer;
    private readonly RotateTransform _rot;
    private double _degPerTick;

    public FanSpinner()
    {
        InitializeComponent();
        _rot = (RotateTransform)Rotor.RenderTransform!;   // the blades' rotation (named fields aren't generated for transforms)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => Advance();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyBlades();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RpmProperty)
        {
            var degPerSec = Rpm <= 0 ? 0 : Math.Min(Rpm * DegPerSecPerRpm, MaxDegPerSec);
            _degPerTick = degPerSec * TickMs / 1000.0;
        }
        else if (change.Property == BladesProperty)
        {
            ApplyBlades();
        }
    }

    private void ApplyBlades()
    {
        var brush = Blades ?? new SolidColorBrush(Color.FromArgb(0xB3, 0x88, 0x88, 0x88));
        Rotor.Fill = brush;
        Hub.Fill = brush;
    }

    private void Advance()
    {
        if (!IsEffectivelyVisible || _degPerTick <= 0) return;   // no work while the flyout is hidden
        _rot.Angle = (_rot.Angle + _degPerTick) % 360;
    }
}
