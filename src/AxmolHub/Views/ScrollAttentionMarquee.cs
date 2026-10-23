using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>Draws a short light trail traveling around the scroll-to-bottom button's rounded edge.</summary>
public sealed class ScrollAttentionMarquee : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private const double SpeedPixelsPerSecond = 28;
    private const double TrailLength = 24;
    private const int TrailDots = 25;
    private const double TrailStep = TrailLength / (TrailDots - 1);
    private bool _isActive;
    private double _distance;

    public ScrollAttentionMarquee()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) =>
        {
            _distance += SpeedPixelsPerSecond * _timer.Interval.TotalSeconds;
            var perimeter = Perimeter(Bounds.Width, Bounds.Height);
            if (perimeter > 0 && _distance >= perimeter) _distance %= perimeter;
            InvalidateVisual();
        };
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (_isActive) _timer.Start();
            else _timer.Stop();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_isActive || Bounds.Width < 8 || Bounds.Height < 8) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        const double inset = 1.6;
        const double radius = 6.2;
        var left = inset;
        var top = inset;
        var right = width - inset;
        var bottom = height - inset;
        var perimeter = Perimeter(width, height);
        var color = Color.Parse("#59C7FF");

        // Several fading dots read as one moving beam while keeping the normal button border visible.
        for (var i = TrailDots - 1; i >= 0; i--)
        {
            var behind = i * TrailStep;
            var point = PointAt((_distance - behind + perimeter) % perimeter,
                left, top, right, bottom, radius);
            var strength = 1.0 - (double)i / TrailDots;
            var alpha = (byte)(24 + (strength * 176));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            var dotRadius = 0.5 + (strength * 0.5);
            context.DrawEllipse(brush, null, point, dotRadius, dotRadius);
        }
    }

    private static double Perimeter(double width, double height)
    {
        const double inset = 1.6;
        const double radius = 6.2;
        var straightWidth = Math.Max(0, width - (2 * inset) - (2 * radius));
        var straightHeight = Math.Max(0, height - (2 * inset) - (2 * radius));
        return (2 * straightWidth) + (2 * straightHeight) + (2 * Math.PI * radius);
    }

    private static Point PointAt(double distance, double left, double top, double right, double bottom, double radius)
    {
        var straightWidth = Math.Max(0, right - left - (2 * radius));
        var straightHeight = Math.Max(0, bottom - top - (2 * radius));
        var arc = Math.PI * radius / 2;
        var segment = straightWidth;
        if (distance < segment) return new Point(left + radius + distance, top);
        distance -= segment;

        if (distance < arc) return Arc(right - radius, top + radius, -Math.PI / 2, distance, radius);
        distance -= arc;
        segment = straightHeight;
        if (distance < segment) return new Point(right, top + radius + distance);
        distance -= segment;

        if (distance < arc) return Arc(right - radius, bottom - radius, 0, distance, radius);
        distance -= arc;
        if (distance < straightWidth) return new Point(right - radius - distance, bottom);
        distance -= straightWidth;

        if (distance < arc) return Arc(left + radius, bottom - radius, Math.PI / 2, distance, radius);
        distance -= arc;
        if (distance < straightHeight) return new Point(left, bottom - radius - distance);
        distance -= straightHeight;

        return Arc(left + radius, top + radius, Math.PI, distance, radius);
    }

    private static Point Arc(double centerX, double centerY, double startAngle, double distance, double radius)
    {
        var angle = startAngle + Math.Clamp(distance / radius, 0, Math.PI / 2);
        return new Point(centerX + (Math.Cos(angle) * radius), centerY + (Math.Sin(angle) * radius));
    }
}
