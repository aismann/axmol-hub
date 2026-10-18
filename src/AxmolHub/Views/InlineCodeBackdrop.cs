using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;

namespace AxmolHub;

/// <summary>Code remains ordinary text geometry; its static surface paints behind the selection band.</summary>
internal sealed class ReadingCode : CCode
{
    protected override Type StyleKeyOverride => typeof(CCode);
    internal readonly List<CGeometry> Geometries = [];

    internal ReadingCode(IEnumerable<CInline> content) : base(content)
    {
        // CSpan's decorator consumes pointer presses and maps its last run to the end, not the hit character.
        // Keep the text geometry instead; the sibling backdrop owns the decoration, not input.
        Padding = default;
        Margin = default;
        BorderThickness = default;
        CornerRadius = default;
        BoxShadow = default;
        Background = Brushes.Transparent;
    }

    protected override IEnumerable<CGeometry> MeasureOverride(double entireWidth, double remainWidth)
    {
        Geometries.Clear();
        Geometries.AddRange(base.MeasureOverride(entireWidth, remainWidth));
        return Geometries;
    }
}

internal sealed class InlineCodeBackdrop : Control
{
    internal static readonly StyledProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.Register<InlineCodeBackdrop, IBrush?>("Surface");
    private readonly CTextBlock block;
    private readonly IReadOnlyList<ReadingCode> codes;

    static InlineCodeBackdrop() => AffectsRender<InlineCodeBackdrop>(SurfaceProperty);

    internal InlineCodeBackdrop(CTextBlock block, IReadOnlyList<ReadingCode> codes)
    {
        this.block = block;
        this.codes = codes;
        this.Bind(SurfaceProperty, new DynamicResourceExtension("Hub.SurfaceRaised"));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (GetValue(SurfaceProperty) is not { } brush) return;
        var origin = block.TranslatePoint(default, this) ?? default;
        foreach (var code in codes)
        foreach (var geometry in code.Geometries)
            context.DrawRectangle(brush, null,
                new Rect(origin.X + geometry.Left, origin.Y + geometry.Top, geometry.Width, geometry.Height), 2, 2);
    }
}
