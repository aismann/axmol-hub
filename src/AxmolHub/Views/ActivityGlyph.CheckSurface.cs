using Avalonia.Controls;

namespace AxmolHub;
public sealed partial class ActivityGlyph : Control
{
    /// <summary>Whether the internal clock is running. Readable so a check can assert the stop: an animation
    /// that keeps ticking on a hidden row is invisible in every screenshot and expensive in every profile.</summary>
    public bool IsTickingForCheck => IsTicking;
}
