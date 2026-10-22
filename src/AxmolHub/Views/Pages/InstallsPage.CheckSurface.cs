using Avalonia.Controls;

namespace AxmolHub;
public partial class InstallsPage : UserControl
{
    internal DataGrid GridForCheck => EnginesGrid;
    internal EngineListItem[] RowsForCheck => EnginesGrid.ItemsSource as EngineListItem[] ?? [];
    internal TextBox SearchForCheck => SearchBox;
    internal ComboBox ChannelFilterForCheck => ChannelFilter;
    internal Button SortForCheck => SortButton;
    internal Button ImportForCheck => ImportButton;
    internal Button InstallForCheck => InstallButton;
}
