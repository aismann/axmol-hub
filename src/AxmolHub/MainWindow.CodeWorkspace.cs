using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AxmolHub;

public partial class MainWindow
{
    private ContentControl _codeWorkspaceContent = null!;
    private Grid _codeWorkspaceHost = null!;
    private TextBlock _codeWorkspaceTitle = null!;
    private Func<bool>? _codeWorkspaceIsDirty;

    internal bool CodeWorkspaceIsDirty => _codeWorkspaceHost?.IsVisible == true && _codeWorkspaceIsDirty?.Invoke() == true;
    internal bool CodeWorkspaceVisibleForCheck => _codeWorkspaceHost.IsVisible;
    internal string CodeWorkspaceTitleForCheck => _codeWorkspaceTitle.Text ?? "";
    internal Control? CodeWorkspaceContentForCheck => _codeWorkspaceContent.Content as Control;

    private void InitializeCodeWorkspace()
    {
        _codeWorkspaceContent = this.FindControl<ContentControl>("CodeWorkspaceContent")!;
        _codeWorkspaceHost = this.FindControl<Grid>("CodeWorkspaceHost")!;
        _codeWorkspaceTitle = this.FindControl<TextBlock>("CodeWorkspaceTitle")!;
        this.FindControl<Button>("CodeWorkspaceCloseButton")!.Click += async (_, _) => await CloseCodeWorkspaceAsync();
        AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Escape || !_codeWorkspaceHost.IsVisible) return;
            e.Handled = true;
            await CloseCodeWorkspaceAsync();
        }, RoutingStrategies.Tunnel);
    }

    internal void ShowCodeWorkspace(string title, Control content, Func<bool>? isDirty = null)
    {
        if (CodeWorkspaceIsDirty)
        {
            _ = ReplaceCodeWorkspaceAsync(title, content, isDirty);
            return;
        }
        SetCodeWorkspace(title, content, isDirty);
    }

    private async Task ReplaceCodeWorkspaceAsync(string title, Control content, Func<bool>? isDirty)
    {
        var answer = await HubDialog.ShowAsync(this, "Unsaved code buffer",
            "This file has unsaved changes. Discard them and open the requested code view?",
            HubDialogButtons.YesNo, danger: true);
        if (answer == HubDialogResult.Yes) SetCodeWorkspace(title, content, isDirty);
    }

    private void SetCodeWorkspace(string title, Control content, Func<bool>? isDirty)
    {
        _codeWorkspaceTitle.Text = title;
        _codeWorkspaceContent.Content = content;
        _codeWorkspaceIsDirty = isDirty;
        _codeWorkspaceHost.IsVisible = true;
    }

    internal void CloseCodeWorkspace(bool discard = false)
    {
        if (discard)
        {
            _codeWorkspaceContent.Content = null;
            _codeWorkspaceIsDirty = null;
            _codeWorkspaceHost.IsVisible = false;
        }
        else _ = CloseCodeWorkspaceAsync();
    }

    private async Task CloseCodeWorkspaceAsync()
    {
        if (!_codeWorkspaceHost.IsVisible) return;
        if (_codeWorkspaceIsDirty?.Invoke() == true)
        {
            var answer = await HubDialog.ShowAsync(this, "Unsaved code buffer",
                "This file has unsaved changes. Discard them and close the code workspace?",
                HubDialogButtons.YesNo, danger: true);
            if (answer != HubDialogResult.Yes) return;
        }
        _codeWorkspaceContent.Content = null;
        _codeWorkspaceIsDirty = null;
        _codeWorkspaceHost.IsVisible = false;
    }
}
