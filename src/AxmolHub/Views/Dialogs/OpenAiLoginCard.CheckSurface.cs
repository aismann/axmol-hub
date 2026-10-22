using Avalonia.Controls;

namespace AxmolHub;
public sealed partial class OpenAiLoginCard : UserControl
{
    internal TextBox CallbackForCheck => _callback;
    internal bool HasActionsForCheck => Content is ScrollViewer { Content: StackPanel panel } && panel.Children.Count == 6;
}
