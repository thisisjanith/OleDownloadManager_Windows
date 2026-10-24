using System.Windows;
using System.Windows.Media;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.App.Controls;

/// <summary>
/// Draws the byte map of a segmented download: each segment's downloaded portion is filled at its
/// real offset, so gaps and per-connection progress are visible at a glance.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentSnapshot>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(SegmentBar), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DividerProperty = DependencyProperty.Register(
        nameof(Divider), typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentSnapshot>? Segments
    {
        get => (IReadOnlyList<SegmentSnapshot>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long Total
    {
        get => (long)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush Divider
    {
        get => (Brush)GetValue(DividerProperty);
        set => SetValue(DividerProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 10);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), 2, 2);
        var segments = Segments;
        var total = Total;
        if (segments is null || segments.Count == 0 || total <= 0 || width <= 0)
        {
            return;
        }

        var scale = width / total;
        var pen = new Pen(Divider, 1);
        pen.Freeze();
        foreach (var segment in segments)
        {
            var x = segment.StartOffset * scale;
            var filled = Math.Clamp(segment.DownloadedBytes, 0, segment.Length) * scale;
            if (filled > 0)
            {
                dc.DrawRectangle(Fill, null, new Rect(x, 0, Math.Max(1, filled), height));
            }

            if (segment.StartOffset > 0)
            {
                dc.DrawLine(pen, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, height));
            }
        }
    }
}
