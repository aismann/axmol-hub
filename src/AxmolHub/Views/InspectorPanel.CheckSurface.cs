using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Markdown.Avalonia;

namespace AxmolHub;
internal sealed partial class InspectorPanel : UserControl
{
    // ── check accessors ──
    internal bool PlanTabVisibleForCheck => _planScroll.IsVisible;
    internal bool ChangesTabVisibleForCheck => _changesScroll.IsVisible;
    internal bool RepoTabVisibleForCheck => _repoScroll.IsVisible;
    /// <summary>Which tab button carries the underline. The three visibility booleans say what is on screen; this
    /// says what the header claims is on screen, and a pane that showed one page while marking another is caught
    /// by nothing else.</summary>
    internal string SelectedTabKeyForCheck => _planTab.Classes.Contains("selected") ? "plan" : _repoScroll.IsVisible ? "repo" : _changesTab.Classes.Contains("selected") ? "changes" : "";

    /// <summary>Presses a header tab. Every repository test used to enter through the shell's own
    /// <c>OpenInspector</c> — which is exactly how a pane that only <em>looked</em> like it had moved to 仓库
    /// stayed green, because the shell already knew the tab it had just been asked for.</summary>
    internal void ClickPlanTabForCheck() => ClickTab(_planTab);
    internal void ClickChangesTabForCheck() => ClickTab(_changesTab);
    internal void ClickRepoTabForCheck() => SetChangesScope("repo");
    private static void ClickTab(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    /// <summary>The markdown the plan tab is holding, read off the rendered viewer's own <c>Tag</c>. A check can
    /// therefore prove *which* plan a card click landed on, not merely that some plan is on screen.</summary>
    internal string PlanMarkdownForCheck => _planHost.Content is MarkdownScrollViewer viewer ? viewer.Tag as string ?? "" : "";
    /// <summary>The plan tab's own "there is nothing here" sentence, read apart from the markdown. The markdown
    /// accessor cannot tell a pane that was never built from a conversation that has no plan — both read as an
    /// empty string — and a restored column is exactly the case where that difference is the whole claim.</summary>
    internal string PlanNoticeForCheck => _planHost.Content is TextBlock block && block.Classes.Contains("inspector-empty") ? block.Text ?? "" : "";

    /// <summary>Runs the pass that installs the fenced block's toolbar on the plan's own viewer, the way the
    /// renderer's LayoutUpdated hook does it for the chat. Split from the read below so the check states both
    /// halves: first the toolbar gets built, then it is asked whether it draws.</summary>
    internal void ApplyPlanSyntaxHighlightingForCheck()
    {
        if (_planHost.Content is MarkdownScrollViewer viewer)
            MarkdownMessageRenderer.ApplySyntaxHighlighting(viewer);
    }

    /// <summary>Whether the plan's fenced code block carries a copy button that actually paints its icon. The
    /// button is built with no Content at all — the glyph comes entirely from a template — and this pane is the
    /// renderer's audience outside ChatPanel, which is exactly where a style written in ChatPanel.axaml never
    /// reached: the button fell back to the base Button theme and showed an empty plate with only its tooltip.
    /// The icon being there is therefore a property worth asserting on its own.</summary>
    internal bool PlanCopyIconForCheck => _planHost.Content is MarkdownScrollViewer viewer && MarkdownMessageRenderer.HasCopyToolbar(viewer);
    internal bool PlanCopyButtonInHeaderForCheck => ReferenceEquals(_planCopyButton.Parent, _header) && Grid.GetColumn(_planCopyButton) == 4;
    internal bool PlanCopyButtonVisibleForCheck => _planCopyButton.IsVisible;
    /// <summary>Whether the plan tab's own document paints its selection from the Hub token. The pane is the
    /// renderer's other audience, and the colour of a selection is exactly the kind of thing a rule written for
    /// the chat half alone does not reach: asked of the plan's viewer, not of the chat's.</summary>
    internal bool PlanHasThemedSelectionForCheck => _planHost.Content is MarkdownScrollViewer viewer && MarkdownMessageRenderer.HasThemedSelection(viewer);
    /// <summary>Whether the plan's own document carries the menu a selection earns — the pane is the renderer's
    /// other audience, and a menu attached inside ChatPanel alone would leave selecting the plan useless.</summary>
    internal bool PlanSelectionMenuForCheck => _planHost.Content is MarkdownScrollViewer viewer && SelectionMenu.HeadersForCheck(viewer).SequenceEqual([HubStrings.Get("CopyMessage"), HubStrings.Get("QuoteSelection")]);
    /// <summary>How many buttons the header holds. Exactly three, the three tabs: the pane's other two controls
    /// moved to the title bar, and a check that only ever pressed those would stay green while a second, dead pair
    /// was still painted in here. Counted off the header itself rather than off the pane, because the body has
    /// buttons of its own (the repository tab's ⟳) that have nothing to do with this.</summary>
    internal int HeaderButtonCountForCheck => _header.GetLogicalDescendants().OfType<Button>().Count();
    internal bool ExpandedForCheck => _expanded;
    internal double PlanHostMaxWidthForCheck => _planHost.MaxWidth;
    /// <summary>The file rows, not the children of the list. An empty scope now ends on a sentence and sometimes
    /// a jump, and a count that grew because a helper line was drawn would stop meaning "how many files".</summary>
    internal int ChangeRowCountForCheck => ChangeRows().Count;
    internal string ChangeScopeLabelForCheck => _scopeButton.Content?.ToString() ?? "";
    internal string[] ChangeFileMenuItemsForCheck => ChangeRows().FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
        .FirstOrDefault(button => button.Tag as string == "InspectorFileActions")?.ContextMenu?.Items
        .OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray() ?? [];
    internal string FirstChangePathForCheck => ChangeRows().FirstOrDefault()?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-path"))?.Text ?? "";
    /// <summary>Which file rows the reader had opened, as the panel keeps them across repaints. A check drives
    /// this by clicking a row rather than by setting the field, so what it asserts is the wiring.</summary>
    internal int OpenChangeCountForCheck => _openedChanges.Count;

    internal bool DurableDiffControlsForCheck
    {
        get
        {
            var controls = _changesList.GetLogicalDescendants().OfType<ComboBox>().ToArray();
            return controls.Any(control => Equals(control.Tag, "InspectorHistorySelector")) && controls.All(control => !Equals(control.Tag, "InspectorDiffViewSelector")) && _changesList.GetLogicalDescendants().OfType<ScrollViewer>().Any(viewer => viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
        }
    }

    internal void SelectHistoryRevisionForCheck(int index)
    {
        var selector = _changesList.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(control => Equals(control.Tag, "InspectorHistorySelector"));
        if (selector is not null)
            selector.SelectedIndex = index;
    }

    internal string? SelectedRevisionForCheck(string path) => _selectedRevisions.GetValueOrDefault(path);
    /// <summary>Every string in one change row, joined: the verdict mark, the path, the counts, and once opened,
    /// the diff lines themselves — which is how a check tells the frozen preview from a resolved one.</summary>
    internal string ChangeRowTextForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")) : "";
    internal int ChangeRowDiffGutterCountForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row ? row.GetLogicalDescendants().OfType<TextBlock>().Count(block => block.Classes.Contains("inspector-diff-gutter") && !string.IsNullOrWhiteSpace(block.Text)) : 0;
    internal string ChangeRowDiffMarkersForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row ? string.Concat(row.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("inspector-diff-marker")).Select(block => block.Text)) : "";
    internal bool ChangeRowExpandedForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row && row.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault()?.IsVisible == true;
    /// <summary>Presses a change row's header, so the lazy per-file diff is reached the way a reader reaches it
    /// and the resolver can be proven wired to the click rather than run for the whole list up front.</summary>
    internal void ClickChangeRowForCheck(int index)
    {
        if (ChangeRows().ElementAtOrDefault(index)is not { } row)
            return;
        row.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("inspector-change-header"))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The empty-scope jump on the changes tab, as the reader sees it. Read apart from the notice line
    /// on purpose: "the session has changed nothing" and "the repository holds three files, go look" are two
    /// sentences, and a check that only saw the first would pass on a pane that cannot offer the second.</summary>
    internal string ChangeJumpTextForCheck => JumpText(_changesList);
    internal bool ChangeJumpPresentForCheck => ChangeJumpTextForCheck.Length > 0;

    /// <summary>Presses the jump the reader would press. The click leaves the panel as an event, so a fixture pane
    /// with no host can still prove *which* tab was asked for, and a live pane can prove the column opened.</summary>
    internal void ClickChangeJumpForCheck() => ClickJump(_changesList);
    private List<Control> ChangeRows() => _changesList.Children.OfType<StackPanel>().Where(panel => panel.Classes.Contains("inspector-change")).Cast<Control>().ToList();
    // ── the repository scope, as a check reads it ──
    /// <summary>Every line in the body. The caveat and the head row are always there, so a list of N entries is
    /// N+2 — plus one more when the read said it was cut, and one more again when it said why it could not read.</summary>
    internal int RepoBodyCountForCheck => _repoBody.Children.Count;
    /// <summary>The head line's text: the branch, and the ahead/behind pair only when it is off its upstream.</summary>
    internal string RepoHeadlineForCheck => _repoBody.Children.OfType<Grid>().SelectMany(grid => grid.Children).OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-note"))?.Text ?? "";
    /// <summary>The caveat, read apart from the why-sentences: it is the tab's standing first line, present in
    /// every state, while a why-sentence is what the tab ends on instead of a list.</summary>
    internal string RepoCaveatForCheck => _repoBody.Children.OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-note"))?.Text ?? "";
    /// <summary>The sentences the tab ends on instead of a list, joined. Each outcome owns its own string, and
    /// this is how that is proven rather than inferred from a row count.</summary>
    internal string RepoNoticeForCheck => string.Join(" ~ ", _repoBody.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("inspector-empty")).Select(block => block.Text ?? ""));
    internal string RepoFirstPathForCheck => _repoBody.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-path"))?.Text ?? "";
    internal string[] RepoFileMenuItemsForCheck(int index) => _repoBody.GetLogicalDescendants().OfType<Button>()
        .Where(button => button.Tag as string == "InspectorFileActions").ElementAtOrDefault(index)?.ContextMenu?.Items
        .OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray() ?? [];

    /// <summary>Every string in one row, joined: the two status letters, the path, and the mark when this
    /// conversation also wrote it. Read as text because that is what the reader sees, and a fixture state makes
    /// the whole tab assertable without a repository behind it.</summary>
    internal string RepoRowTextForCheck(int index) => RepositoryRows().ElementAtOrDefault(index)is { } row ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")) : "";
    internal int RepoRowCountForCheck => RepositoryRows().Count;
    internal bool RepoCommitComparisonControlsAbsentForCheck => RepositoryRows().SelectMany(row => row.GetLogicalDescendants())
        .All(control => control is not TextBox
            && (control is not Button button || button.Tag as string is not ("InspectorGitCommitRef" or "InspectorGitCommitCompare")));

    internal bool RepoRowExpandedForCheck(int index) => RepositoryRows().ElementAtOrDefault(index)is { } row && row.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault(control => control != row)?.IsVisible == true;
    internal void ClickRepoRowForCheck(int index)
    {
        if (RepositoryRows().ElementAtOrDefault(index)is not { } row)
            return;
        row.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("inspector-change-header"))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Presses the ⟳ through its own button, so a check drives the event the panel raises rather than
    /// calling the shell method the button happens to end up in.</summary>
    internal void ClickRepoRefreshForCheck()
    {
        _repoBody.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Tag as string == "InspectorRepoRefresh")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The empty-scope jump back to the session's own list, which is what the tab ends on right after a
    /// commit — the working tree is clean and the transcript still holds the writes.</summary>
    internal string RepoJumpTextForCheck => JumpText(_repoBody);
    internal bool RepoJumpPresentForCheck => RepoJumpTextForCheck.Length > 0;

    internal void ClickRepoJumpForCheck() => ClickJump(_repoBody);
    /// <summary>The jump line in this body, if one was drawn. Only the text carries the class, so the button and
    /// its label are read through one descendant lookup.</summary>
    private static string JumpText(StackPanel host) => host.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-jump"))?.Text ?? "";
    private static void ClickJump(StackPanel host) => host.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("inspector-jump"))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private List<Control> RepositoryRows() => _repoBody.Children.OfType<StackPanel>().Where(panel => panel.Classes.Contains("inspector-change")).Cast<Control>().ToList();
}
