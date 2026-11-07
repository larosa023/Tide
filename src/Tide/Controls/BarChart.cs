using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Tide.Core;

namespace Tide.Controls;

/// <summary>Stacked download/upload bars for the history view, with hover read-out.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(
        nameof(Buckets), typeof(IReadOnlyList<UsageBucket>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownBrushProperty = Reg(nameof(DownBrush));
    public static readonly DependencyProperty UpBrushProperty = Reg(nameof(UpBrush));
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush));
    public static readonly DependencyProperty LabelBrushProperty = Reg(nameof(LabelBrush));
    public static readonly DependencyProperty HoverBrushProperty = Reg(nameof(HoverBrush));
    public static readonly DependencyProperty TextBrushProperty = Reg(nameof(TextBrush));

    private static DependencyProperty Reg(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<UsageBucket>? Buckets { get => (IReadOnlyList<UsageBucket>?)GetValue(BucketsProperty); set => SetValue(BucketsProperty, value); }
    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private int _hover = -1;
    private static readonly Typeface Face = new("Segoe UI");
    private const double AxisH = 20, LabelW = 56, TopPad = 26;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var b = Buckets;
        if (b == null || b.Count == 0) return;
        double plotW = ActualWidth - LabelW;
        double x = e.GetPosition(this).X;
        int i = (int)(x / plotW * b.Count);
        i = i >= 0 && i < b.Count && x <= plotW ? i : -1;
        if (i != _hover) { _hover = i; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var buckets = Buckets;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double plotW = w - LabelW, plotTop = TopPad, plotBottom = h - AxisH, plotH = plotBottom - plotTop;

        long max = 0;
        if (buckets != null)
            foreach (var b in buckets) max = Math.Max(max, b.Down + b.Up);
        double scale = LiveGraph.NiceCeiling(Math.Max(max, 1024 * 1024));

        var grid = new Pen(GridBrush, 1);
        grid.Freeze();
        for (int g = 0; g <= 4; g++)
        {
            double y = Math.Round(plotTop + plotH * g / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(plotW, y));
            var ft = Text(Format.Bytes(scale * (4 - g) / 4), 10.5, LabelBrush, dpi);
            dc.DrawText(ft, new Point(plotW + 6, y - ft.Height / 2));
        }

        if (buckets == null || buckets.Count == 0) return;

        int n = buckets.Count;
        double slot = plotW / n;
        double barW = Math.Max(2, Math.Min(36, slot * 0.62));
        int labelEvery = n <= 7 ? 1 : n <= 12 ? 1 : n <= 24 ? 3 : 5;

        for (int i = 0; i < n; i++)
        {
            var b = buckets[i];
            double cx = slot * i + slot / 2;
            double x = Math.Round(cx - barW / 2);

            if (i == _hover)
                dc.DrawRoundedRectangle(HoverBrush, null, new Rect(slot * i + 1, plotTop, slot - 2, plotH), 4, 4);

            double hDown = b.Down / scale * plotH;
            double hUp = b.Up / scale * plotH;
            if (hDown + hUp > 0 && hDown + hUp < 2) hDown = 2; // keep tiny days visible

            double y = plotBottom;
            if (hDown > 0)
            {
                dc.DrawRoundedRectangle(DownBrush, null, new Rect(x, y - hDown, barW, hDown), 2, 2);
                y -= hDown;
            }
            if (hUp > 0)
                dc.DrawRoundedRectangle(UpBrush, null, new Rect(x, y - hUp - (hDown > 0 ? 1 : 0), barW, hUp), 2, 2);

            if (i % labelEvery == 0 || n <= 12)
            {
                var ft = Text(b.Label, 10.5, LabelBrush, dpi);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, plotBottom + 4));
            }
        }

        if (_hover >= 0 && _hover < n)
        {
            var b = buckets[_hover];
            var ft = Text($"{b.Detail}     ↓ {Format.Bytes(b.Down)}     ↑ {Format.Bytes(b.Up)}", 12, TextBrush, dpi);
            dc.DrawText(ft, new Point(0, 2));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi);
}
