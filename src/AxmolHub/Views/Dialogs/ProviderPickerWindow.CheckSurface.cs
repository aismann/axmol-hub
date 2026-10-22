using Avalonia.Controls;
using AxmolHub.Core;

namespace AxmolHub;
public sealed partial class ProviderPickerWindow : Window
{
    // ── Verification hooks (used by --verify-shell) ──
    //
    // The dialog is modal, so the self-check cannot click it. Instead it constructs the window directly and
    // drives the pure parts — the query and what the list ends up holding — which is where a broken filter or a
    // missing custom row would show up. Rendering the rows is the part deliberately left to the eye.
    /// <summary>Builds the dialog without showing it, for the self-check.</summary>
    internal static ProviderPickerWindow ForCheck(IReadOnlyList<ModelProvider> presets) => new(presets);
    /// <summary>Types a query into the search box, exactly as a person would (the TextChanged handler is what
    /// re-filters, so writing the property directly would test nothing).</summary>
    internal void SearchForCheck(string query) => _search.Text = query;
    /// <summary>How many rows the list currently holds, custom row included.</summary>
    internal int RowCountForCheck => _list.ItemCount;

    /// <summary>
    /// Runs the row template once with a null item — the exact call a container recycling pass makes (see the
    /// note on <c>ItemTemplate</c>), and the one that used to kill the process: a layout-pass exception has no
    /// dialog left to catch it. Swallowed here rather than thrown so the failure lands as one FAIL in the report
    /// instead of aborting every assertion after it.
    /// </summary>
    internal bool RowTemplateSurvivesNullItemForCheck()
    {
        try
        {
            return _list.ItemTemplate?.Build(null) is Control;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The ids of the preset rows currently shown, in order (the custom row contributes nothing).</summary>
    internal string[] VisiblePresetIdsForCheck => _list.Items.OfType<Row>().OfType<PresetRow>().Select(row => row.Provider.Id).ToArray();
    /// <summary>Whether the "custom endpoint" row survived the current query.</summary>
    internal bool CustomRowVisibleForCheck => _list.Items.OfType<Row>().OfType<CustomRow>().Any();
    /// <summary>Whether the "no match" notice is on screen.</summary>
    internal bool EmptyNoticeVisibleForCheck => _empty.IsVisible;

    /// <summary>Selects a preset row by id and commits, as clicking Add would.</summary>
    internal bool ChoosePresetForCheck(string presetId)
    {
        _list.SelectedItem = _list.Items.OfType<Row>().OfType<PresetRow>().FirstOrDefault(row => row.Provider.Id == presetId);
        if (_list.SelectedItem is null)
            return false;
        Commit();
        return true;
    }

    /// <summary>Selects the custom row and commits.</summary>
    internal bool ChooseCustomForCheck()
    {
        _list.SelectedItem = _list.Items.OfType<Row>().OfType<CustomRow>().FirstOrDefault();
        if (_list.SelectedItem is null)
            return false;
        Commit();
        return true;
    }
}
