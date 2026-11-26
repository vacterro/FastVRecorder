using System.Windows;
using System.Windows.Media;

namespace FastVRecorder.App.Controls;

/// <summary>Compact segmented peak meter rendered with the active theme accent.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(LevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        const int bars = 32;
        const double gap = 2;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var barWidth = Math.Max(2, (width - gap * (bars - 1)) / bars);
        var active = (int)Math.Round(Math.Clamp(Value, 0, 1) * bars);
        var accent = (Brush)(TryFindResource("Brush.Accent") ?? Brushes.Gold);
        var hot = (Brush)(TryFindResource("Brush.Recording") ?? Brushes.IndianRed);
        var dim = (Brush)(TryFindResource("Brush.SurfaceElevated") ?? Brushes.DimGray);

        for (var i = 0; i < bars; i++)
        {
            var x = i * (barWidth + gap);
            var barHeight = height * (0.35 + 0.65 * (i / (double)bars));
            var y = (height - barHeight) / 2;
            var brush = i < active
                ? (i >= bars * 0.82 ? hot : accent)
                : dim;
            drawingContext.DrawRectangle(brush, null, new Rect(x, y, barWidth, barHeight));
        }
    }
}
