using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AxmolHub;

/// <summary>The small outline glyph shared by inspector and message actions.</summary>
internal static class HubActionIcon
{
    internal static Path Create(string geometryKey)
    {
        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.1,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        icon.Bind(Path.DataProperty, new DynamicResourceExtension(geometryKey));
        icon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        return icon;
    }
}
