using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI.Views;

/// <summary>Interactive fan-curve graph: one draggable point per fixed temperature anchor. Dragging a point
/// (or clicking anywhere in its column) sets that anchor's duty% — temperatures are fixed, so only the Y axis
/// moves. Binds to the same <see cref="CurvePointViewModel"/> list the slider editor used, so changes flow
/// through the existing debounced apply/persist. Pure code control (draws + handles pointer itself).</summary>
public sealed class FanCurveEditor : Control
{
    public static readonly StyledProperty<IList<CurvePointViewModel>?> PointsProperty =
        AvaloniaProperty.Register<FanCurveEditor, IList<CurvePointViewModel>?>(nameof(Points));

    public IList<CurvePointViewModel>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private int _drag = -1;

    static FanCurveEditor()
    {
        AffectsRender<FanCurveEditor>(PointsProperty);
    }

    public FanCurveEditor()
    {
        // Redraw on resize; a curve point changing (drag or external Load) also invalidates.
        this.GetObservable(BoundsProperty).Subscribe(new AnonObserver(() => InvalidateVisual()));
    }

    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Wire(Points);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unwire(Points);   // don't leave handlers on the shared point list after the modal closes
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty)
        {
            Unwire(change.OldValue as IList<CurvePointViewModel>);
            if (_attached) Wire(change.NewValue as IList<CurvePointViewModel>);
            InvalidateVisual();
        }
    }

    private void Wire(IList<CurvePointViewModel>? pts)
    {
        if (pts is INotifyCollectionChanged incc) incc.CollectionChanged += OnCollChanged;
        if (pts != null) foreach (var p in pts) p.PropertyChanged += OnPointChanged;
    }

    private void Unwire(IList<CurvePointViewModel>? pts)
    {
        if (pts is INotifyCollectionChanged incc) incc.CollectionChanged -= OnCollChanged;
        if (pts != null) foreach (var p in pts) p.PropertyChanged -= OnPointChanged;
    }

    private void OnCollChanged(object? s, NotifyCollectionChangedEventArgs e) { Unwire(Points); Wire(Points); InvalidateVisual(); }
    private void OnPointChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();

    // ---- geometry ----
    private const double Pad = 12;
    private double PlotW => Math.Max(1, Bounds.Width - 2 * Pad);
    private double PlotH => Math.Max(1, Bounds.Height - 2 * Pad);
    private double X(int i, int n) => Pad + (n <= 1 ? 0 : PlotW * i / (n - 1));
    private double Y(double pct) => Pad + PlotH * (1 - Math.Clamp(pct, 0, 100) / 100.0);
    private double PctFromY(double y) => Math.Clamp((1 - (y - Pad) / PlotH) * 100.0, 0, 100);

    // ---- pointer (drag a point / click a column) ----
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pts = Points;
        if (pts is not { Count: > 0 }) return;
        var p = e.GetPosition(this);
        _drag = NearestIndex(p.X, pts.Count);
        SetPoint(pts, _drag, p.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pts = Points;
        if (_drag < 0 || pts is not { Count: > 0 } || _drag >= pts.Count) return;
        SetPoint(pts, _drag, e.GetPosition(this).Y);
    }

    // A fan curve must be monotonic (duty never drops as temperature rises), so a point is constrained
    // between its neighbours: not below the previous point, not above the next.
    private void SetPoint(IList<CurvePointViewModel> pts, int i, double y)
    {
        double pct = Math.Round(PctFromY(y));
        double lo = i > 0 ? pts[i - 1].Percent : 0;
        double hi = i < pts.Count - 1 ? pts[i + 1].Percent : 100;
        if (lo > hi) lo = hi;                       // safety if a stored curve wasn't monotonic
        pts[i].Percent = Math.Clamp(pct, lo, hi);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = -1;
        e.Pointer.Capture(null);
    }

    private int NearestIndex(double x, int n)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < n; i++) { double d = Math.Abs(X(i, n) - x); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    // ---- render ----
    public override void Render(DrawingContext ctx)
    {
        // Transparent fill so the whole graph area is hit-testable (drag anywhere, not just on a point).
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var pts = Points;
        var accent = Brush("AccentBrush", Color.FromRgb(0xF5, 0x7C, 0x00));
        var muted  = Brush("AppMuted", Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var grid   = new SolidColorBrush(Colors.Gray, 0.18);

        // gridlines at 0/25/50/75/100 %
        var gpen = new Pen(grid, 1);
        for (int g = 0; g <= 100; g += 25)
        {
            double y = Y(g);
            ctx.DrawLine(gpen, new Point(Pad, y), new Point(Bounds.Width - Pad, y));
        }

        if (pts is not { Count: > 0 }) return;
        int n = pts.Count;

        // curve line
        var pen = new Pen(accent, 2, lineJoin: PenLineJoin.Round);
        for (int i = 1; i < n; i++)
            ctx.DrawLine(pen, new Point(X(i - 1, n), Y(pts[i - 1].Percent)), new Point(X(i, n), Y(pts[i].Percent)));

        // points + labels
        for (int i = 0; i < n; i++)
        {
            double px = X(i, n), py = Y(pts[i].Percent);
            ctx.DrawEllipse(accent, null, new Point(px, py), 5, 5);

            var pctText = Text($"{(int)pts[i].Percent}%", accent, 10);
            ctx.DrawText(pctText, new Point(px - pctText.Width / 2, py - 16));

            var tempText = Text(pts[i].Label, muted, 10);
            ctx.DrawText(tempText, new Point(px - tempText.Width / 2, Bounds.Height - Pad + 1));
        }
    }

    private IBrush Brush(string key, Color fallback)
        => this.TryFindResource(key, out var v) && v is IBrush b ? b : new SolidColorBrush(fallback);

    private static FormattedText Text(string s, IBrush brush, double size)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    /// <summary>Minimal IObservable&lt;Rect&gt; sink (avoids pulling in System.Reactive just for a redraw).</summary>
    private sealed class AnonObserver(Action onNext) : IObserver<Rect>
    {
        public void OnNext(Rect value) => onNext();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
