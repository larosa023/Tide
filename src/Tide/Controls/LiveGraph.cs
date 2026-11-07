using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Tide.Core;

namespace Tide.Controls;

/// <summary>
/// Lightweight two-series area graph for live download/upload rates. Rendered directly with
/// DrawingContext so redrawing once a second costs almost nothing.
/// </summary>
public sealed class LiveGraph : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(RateSeries), typeof(LiveGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty DownBrushProperty = Reg(nameof(DownBrush));
    public static readonly DependencyProperty UpBrushProperty = Reg(nameof(UpBrush));
    public static readonly DependencyProperty DownFillProperty = Reg(nameof(DownFill));
    public static readonly DependencyProperty UpFillProperty = Reg(nameof(UpFill));
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush));
    public static readonly DependencyProperty LabelBrushProperty = Reg(nameof(LabelBrush));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(LiveGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Reg(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(LiveGraph),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

    public RateSeries? Series { get => (RateSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }
    public Brush DownFill { get => (Brush)GetValue(DownFillProperty); set => SetValue(DownFillProperty, value); }
    public Brush UpFill { get => (Brush)GetValue(UpFillProperty); set => SetValue(UpFillProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    private Pen? _downPen, _upPen, _gridPen, _cursorPen;
    private int _hover = -1;
    private static readonly Typeface Face = new("Segoe UI");

    public LiveGraph()
    {
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => InvalidateVisual();
        Unloaded += (_, _) => { if (Series != null) Series.Changed -= OnSamples; };
        Loaded += (_, _) => { if (Series != null) { Series.Changed -= OnSamples; Series.Changed += OnSamples; } };
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (LiveGraph)d;
        if (e.OldValue is RateSeries o) o.Changed -= g.OnSamples;
        if (e.NewValue is RateSeries n) n.Changed += g.OnSamples;
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (LiveGraph)d;
        g._downPen = g._upPen = g._gridPen = g._cursorPen = null;
    }

    private void OnSamples()
    {
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var s = Series;
        if (s == null || ActualWidth <= 0) return;
        double x = e.GetPosition(this).X;
        _hover = Math.Clamp((int)Math.Round(x / ActualWidth * (s.Capacity - 1)), 0, s.Capacity - 1);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    private static Pen MakePen(Brush b, double w)
    {
        var p = new Pen(b, w) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable

        _downPen ??= MakePen(DownBrush, 1.6);
        _upPen ??= MakePen(UpBrush, 1.6);
        _gridPen ??= MakePen(GridBrush, 1);
        _cursorPen ??= MakePen(LabelBrush, 1);

        var s = Series;
        int n = s?.Capacity ?? 0;

        double max = 0;
        for (int i = 0; i < n; i++)
            max = Math.Max(max, Math.Max(s!.Down[i], s.Up[i]));
        max = NiceCeiling(Math.Max(max, 8 * 1024)); // never scale below 8 KB/s so idle looks idle

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double top = 6, bottom = h - 1;
        double plotH = bottom - top;

        // Grid lines + scale labels
        for (int g = 0; g <= 3; g++)
        {
            double y = Math.Round(top + plotH * g / 3) + 0.5;
            dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            if (ShowLabels && g < 3)
            {
                var ft = new FormattedText(Format.Rate(max * (3 - g) / 3), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, 10.5, LabelBrush, dpi);
                dc.DrawText(ft, new Point(w - ft.Width - 4, y + 2));
            }
        }

        if (s == null || n < 2) return;

        double X(int i) => i * w / (n - 1);
        double Y(double v) => bottom - v / max * plotH;

        DrawSeries(dc, n, i => s.DownAt(i), X, Y, bottom, DownFill, _downPen);
        DrawSeries(dc, n, i => s.UpAt(i), X, Y, bottom, UpFill, _upPen);

        if (_hover >= 0 && _hover < n)
        {
            double x = Math.Round(X(_hover)) + 0.5;
            dc.DrawLine(_cursorPen, new Point(x, top), new Point(x, bottom));
            int ago = n - 1 - _hover;
            var text = $"{(ago == 0 ? "now" : $"{ago}s ago")}   ↓ {Format.Rate(s.DownAt(_hover))}   ↑ {Format.Rate(s.UpAt(_hover))}";
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, LabelBrush, dpi);
            double tx = Math.Clamp(x + 8, 4, Math.Max(4, w - ft.Width - 4));
            dc.DrawText(ft, new Point(tx, top + 2));
        }
    }

    private static void DrawSeries(DrawingContext dc, int n, Func<int, double> value, Func<int, double> X, Func<double, double> Y,
        double bottom, Brush fill, Pen pen)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var p0 = new Point(X(0), Y(value(0)));
            lc.BeginFigure(p0, false, false);
            ac.BeginFigure(new Point(X(0), bottom), true, true);
            ac.LineTo(p0, false, false);
            for (int i = 1; i < n; i++)
            {
                var p = new Point(X(i), Y(value(i)));
                lc.LineTo(p, true, true);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(X(n - 1), bottom), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Rounds up to 1, 2 or 5 × a power of 1024-ish step so the scale reads cleanly.</summary>
    internal static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        double unit = 1;
        while (v / unit >= 1024) unit *= 1024;
        double x = v / unit;
        double[] steps = { 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 40, 50, 60, 80, 100, 150, 200, 300, 400, 500, 600, 800, 1024 };
        foreach (var st in steps)
            if (x <= st) return st * unit;
        return 1024 * unit;
    }
}
