using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AcerHelper.Infrastructure;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace AcerHelper.UI;

/// <summary>A shrouded cooling fan that spins at a calm, RPM-proportional speed (well below a real fan, so it
/// reads as a smooth indicator with no aliasing). The geometry is parametric - <see cref="BladeCount"/> swept
/// blades inside a still shroud, with a machined hub on top - and this maps <see cref="Rpm"/> to rotation and
/// paints the whole glyph with <see cref="Blades"/>.
///
/// THE SPIN IS SPOOLED, not snapped: an RPM reading arrives once per poll, and a fan does not jump from idle to
/// full, so the angular speed chases the reading (and coasts down to a stop) on an exponential approach. The
/// frame rate is ~60 fps because the rotor is the only moving thing on the card; at 30 fps the blade steps are
/// visible at this size.
///
/// Its own timer runs only while attached; a tick while the control is not effectively visible does no work (the
/// flyout is usually hidden, and a hidden spinner must not cost a repaint).</summary>
public partial class FanSpinner : UserControl
{
    public static readonly StyledProperty<int> RpmProperty =
        AvaloniaProperty.Register<FanSpinner, int>(nameof(Rpm));

    public static readonly StyledProperty<IBrush?> BladesProperty =
        AvaloniaProperty.Register<FanSpinner, IBrush?>(nameof(Blades));

    /// <summary>The fan's speed; drives the rotation. 0 (or unknown) = stopped.</summary>
    public int Rpm { get => GetValue(RpmProperty); set => SetValue(RpmProperty, value); }

    /// <summary>Brush for the blades, shroud and hub (typically the muted theme brush).</summary>
    public IBrush? Blades { get => GetValue(BladesProperty); set => SetValue(BladesProperty, value); }

    private const double TickMs = 16;                 // ~60 fps: the rotor is the only animation on the card
    private const double DegPerSecPerRpm = 0.12;      // far below real (rpm*6): ~1 rev/s at 2900 rpm
    private const double MaxDegPerSec = 660;          // calm ceiling (~1.8 rev/s)
    private const double SpoolPerSecond = 5.0;        // how quickly the rotor follows a new reading (~0.2 s)

    // The glyph's own coordinate space (the control is a fixed 24x24, see the .axaml): the blades are generated
    // once, here, so the shape has one statement rather than a hand-written path per blade.
    private const double Cx = 12, Cy = 12, ROuter = 9.9, RRoot = 2.4;
    private const int BladeCount = 5;

    private readonly PeriodicSchedule _timer;
    private readonly RotateTransform _rot;
    private readonly List<AvaloniaPath> _blades = [];
    private double _angle;                            // current rotation, degrees
    private double _velocity;                         // current angular speed, deg/s
    private double _target;                           // the reading's angular speed, deg/s

    public FanSpinner()
    {
        InitializeComponent();
        _rot = (RotateTransform)Rotor.RenderTransform!;   // the rotor's rotation (named fields aren't generated for transforms)

        for (var i = 0; i < BladeCount; i++)
        {
            var blade = new AvaloniaPath { Data = BladeGeometry(i) };
            _blades.Add(blade);
            Rotor.Children.Add(blade);
        }

        // The shared periodic timer in UI mode, at Render priority: a default DispatcherTimer would be
        // Background (the GLib idle priority) and the spin would stutter while the window renders — see
        // PeriodicSchedule. The tick writes the transform, so it still runs on the UI thread (UiSchedule.Render).
        _timer = new PeriodicSchedule(Advance, TimeSpan.FromMilliseconds(TickMs), UiSchedule.Render);
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
            // The reading is the TARGET, not the speed: Advance() spools towards it, so a fan that was idle
            // spins up and one that stopped coasts to a halt.
            _target = Rpm <= 0 ? 0 : Math.Min(Rpm * DegPerSecPerRpm, MaxDegPerSec);
        }
        else if (change.Property == BladesProperty)
        {
            ApplyBlades();
        }
    }

    // One color, four depths: the shroud is the faintest, the inner rim fainter still, the blades solid, and
    // the hub slightly heavier - which is what makes a monochrome glyph read as a housing with a rotor inside
    // it rather than as a flat pinwheel. Element opacity does the shading, so a dynamic brush (the theme's
    // muted foreground) stays a single resource.
    private void ApplyBlades()
    {
        var brush = Blades ?? new SolidColorBrush(Color.FromArgb(0xB3, 0x88, 0x88, 0x88));
        foreach (var blade in _blades) blade.Fill = brush;
        Shroud.Stroke = brush;
        Shroud.Opacity = 0.42;
        ShroudInner.Stroke = brush;
        ShroudInner.Opacity = 0.20;
        Hub.Fill = brush;
        Hub.Opacity = 0.92;
        HubRing.Stroke = brush;
        HubRing.Opacity = 0.55;
        HubCore.Fill = brush;
        HubCore.Opacity = 0.38;
        HubShine.Fill = Brushes.White;
        HubShine.Opacity = 0.28;
    }

    private void Advance()
    {
        if (!IsEffectivelyVisible) return;   // no work while the flyout is hidden

        // Spool: the distance still to cover shrinks by a fixed fraction per second, so the speed change is
        // smooth and the stop is a short coast instead of a freeze on the last frame.
        var dt = TickMs / 1000.0;
        _velocity += (_target - _velocity) * Math.Min(1, dt * SpoolPerSecond);

        if (_target == 0 && Math.Abs(_velocity) < 0.5)
        {
            _velocity = 0;                   // parked: no repaint at all while the fan is stopped
            return;
        }

        _angle = (_angle + _velocity * dt) % 360;
        _rot.Angle = _angle;
    }

    /// <summary>One blade, as a CURVED and TAPERED petal: a narrow root on the hub, a wide tip on the outer
    /// circle, and both edges bowed backwards so the rotor reads as a fan rather than as a pinwheel. The shape
    /// is a pure function of the blade index, which is why it is generated rather than hand-written.</summary>
    private static Geometry BladeGeometry(int index)
    {
        var step = 360.0 / BladeCount;
        var a0 = index * step + step * 0.06;   // a small gap so neighbouring blades do not touch
        var sweep = step * 0.80;

        var leadRoot = Polar(RRoot, a0 + sweep * 0.10);
        var leadTip = Polar(ROuter, a0 + sweep * 0.52);
        var trailTip = Polar(ROuter, a0 + sweep * 0.97);
        var trailRoot = Polar(RRoot, a0 + sweep * 0.58);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(leadRoot, true);

        // Leading edge: bows towards the previous blade (a backward-swept fan blade).
        ctx.CubicBezierTo(
            Polar(RRoot + (ROuter - RRoot) * 0.42, a0 + sweep * 0.16),
            Polar(ROuter * 0.92, a0 + sweep * 0.34),
            leadTip);

        // The tip: a short arc along the outer circle, so it is rounded rather than cut off.
        ctx.ArcTo(trailTip, new Size(ROuter, ROuter), 0, false, SweepDirection.Clockwise);

        // Trailing edge: back towards the hub, bowing the other way.
        ctx.CubicBezierTo(
            Polar(ROuter * 0.90, a0 + sweep * 0.82),
            Polar(RRoot + (ROuter - RRoot) * 0.34, a0 + sweep * 0.74),
            trailRoot);

        // The root: an arc along the hub (the hub covers it, which is what makes the blade look attached).
        ctx.ArcTo(leadRoot, new Size(RRoot, RRoot), 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return geometry;
    }

    private static Point Polar(double r, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(Cx + r * Math.Cos(rad), Cy + r * Math.Sin(rad));
    }
}
