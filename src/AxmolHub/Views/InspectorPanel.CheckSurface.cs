using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Documents;
using ShapePath = Avalonia.Controls.Shapes.Path;
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
    internal void ClickRepoTabForCheck() => ClickTab(_changesTab);
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
    /// <summary>Whether the copy sits in the header, immediately left of the page's gear. Read as "immediately
    /// left" rather than as a column number: what matters is that the two buttons share the header's right end,
    /// and a check pinned to an index goes stale the moment a column is added between them.</summary>
    internal bool PlanCopyButtonInHeaderForCheck => ReferenceEquals(_planCopyButton.Parent, _header) && Grid.GetColumn(_planCopyButton) + 1 == Grid.GetColumn(_viewSettings);
    internal bool PlanCopyButtonVisibleForCheck => _planCopyButton.IsVisible;
    internal bool ViewSettingsVisibleForCheck => _viewSettings.IsVisible;
    /// <summary>Whether the plan tab's own document paints its selection from the Hub token. The pane is the
    /// renderer's other audience, and the colour of a selection is exactly the kind of thing a rule written for
    /// the chat half alone does not reach: asked of the plan's viewer, not of the chat's.</summary>
    internal bool PlanHasThemedSelectionForCheck => _planHost.Content is MarkdownScrollViewer viewer && MarkdownMessageRenderer.HasThemedSelection(viewer);
    /// <summary>Whether the plan's own document carries the menu a selection earns — the pane is the renderer's
    /// other audience, and a menu attached inside ChatPanel alone would leave selecting the plan useless.</summary>
    internal bool PlanSelectionMenuForCheck => _planHost.Content is MarkdownScrollViewer viewer && SelectionMenu.HeadersForCheck(viewer).SequenceEqual([HubStrings.Get("CopyMessage"), HubStrings.Get("QuoteSelection")]);
    /// <summary>Visible header actions only: the plan page has a gear and an optional copy button. The tabs are
    /// not in this row any more — they are in the title bar (see <see cref="TabStrip"/>) — and the hidden Changes
    /// edit actions must not inflate this count or appear over a plan.</summary>
    internal int HeaderButtonCountForCheck => _header.GetLogicalDescendants().OfType<Button>().Count(button => button.IsVisible);
    internal bool ExpandedForCheck => _expanded;
    internal double PlanHostMaxWidthForCheck => _planHost.MaxWidth;
    /// <summary>The file rows, not the children of the list. An empty scope now ends on a sentence and sometimes
    /// a jump, and a count that grew because a helper line was drawn would stop meaning "how many files".</summary>
    internal int ChangeRowCountForCheck => ChangeRows().Count;
    /// <summary>Presses the header gear — the only way into the page's menu, which is the point: the rows a check
    /// reads are the ones a reader's press produces.</summary>
    internal void PressViewSettingsForCheck() => ClickTab(_viewSettings);

    internal bool ViewMenuOpenForCheck => _viewMenu.IsOpen;

    /// <summary>The gear's rows in the order it draws them, each carrying "✓" when its tick is lit. The ▾ button
    /// that used to spell the scope out in the header is gone, so this menu is where the page states both of its
    /// choices; reading it as text is reading what the reader sees.</summary>
    internal string[] ViewMenuRowsForCheck =>
        _viewMenu.Items.OfType<MenuItem>().Select(row => row.IsChecked == true ? $"{row.Tag} ✓" : $"{row.Tag}").ToArray();

    /// <summary>The ticked rows. Two, one per group, always: a menu that ticked both layouts or neither would
    /// leave a reader guessing which one is up, and it would disagree with the layout actually on screen.</summary>
    internal string[] ViewMenuTickedForCheck =>
        _viewMenu.Items.OfType<MenuItem>().Where(row => row.IsChecked == true).Select(row => $"{row.Tag}").ToArray();

    internal bool SplitDiffForCheck => _splitDiff;

    /// <summary>Every diff's horizontal viewport has no vertical cap or scrollbar, while the Changes page owns the
    /// vertical bar. Read off the rendered row so this catches both layouts and cannot be satisfied by a setting
    /// the builder stopped using.</summary>
    internal bool ChangeDiffUsesSharedVerticalScrollForCheck(int index)
    {
        var row = ChangeRows().ElementAtOrDefault(index);
        if (row is null) return false;
        var viewers = row.GetLogicalDescendants().OfType<ScrollViewer>().ToArray();
        return viewers.Length > 0
            && viewers.All(viewer => viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                && viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
                && double.IsPositiveInfinity(viewer.MaxHeight))
            && _changesScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto;
    }

    /// <summary>Presses one row of the gear's menu by its tag, so a check picks a setting the way a reader does
    /// rather than assigning the field the row happens to set.</summary>
    internal void ClickViewMenuRowForCheck(string tag) =>
        _viewMenu.Items.OfType<MenuItem>().FirstOrDefault(row => row.Tag as string == tag)?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    internal bool ChangeOpenFileButtonForCheck => ChangeRows().FirstOrDefault()?.GetLogicalDescendants().OfType<Button>()
        .Any(button => button.Tag as string == "InspectorOpenFile"
            && button.Content is ShapePath icon && icon.Data is not null) == true;
    internal string FirstChangePathForCheck => ChangeRows().FirstOrDefault()?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Classes.Contains("inspector-path"))?.Text ?? "";
    /// <summary>Which file rows the reader had opened, as the panel keeps them across repaints. A check drives
    /// this by clicking a row rather than by setting the field, so what it asserts is the wiring.</summary>
    internal int OpenChangeCountForCheck => _openedChanges.Count;

    internal bool DurableDiffControlsForCheck
    {
        get
        {
            var controls = _changesList.GetLogicalDescendants().OfType<ComboBox>().ToArray();
            var diffViewports = _changesList.GetLogicalDescendants().OfType<ScrollViewer>().ToArray();
            return controls.Any(control => Equals(control.Tag, "InspectorHistorySelector"))
                && controls.All(control => !Equals(control.Tag, "InspectorDiffViewSelector"))
                && _changesScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                && diffViewports.Any(viewer => viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                    && viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled);
        }
    }

    internal void SelectHistoryRevisionForCheck(int index)
    {
        var selector = _changesList.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(control => Equals(control.Tag, "InspectorHistorySelector"));
        if (selector is not null)
            selector.SelectedIndex = index;
    }

    internal static string TextForCheck(TextBlock block) => block.Text
        ?? string.Concat(block.Inlines?.Select(inline => inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Select(child => child switch
            {
                Run nestedRun => nestedRun.Text,
                _ => "",
            })),
            _ => "",
        }) ?? []);

    internal string? SelectedRevisionForCheck(string path) => _selectedRevisions.GetValueOrDefault(path);
    /// <summary>Every string in one change row, joined: the verdict mark, the path, the counts, and once opened,
    /// the diff lines themselves — which is how a check tells the frozen preview from a resolved one.</summary>
    internal string ChangeRowTextForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(TextForCheck)) : "";
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
    internal bool SessionChangesSurfaceVisibleForCheck => _changesScroll.IsVisible;
    internal string ChangeJumpTextForCheck => JumpText(_changesList);
    internal bool ChangeJumpPresentForCheck => ChangeJumpTextForCheck.Length > 0;

    /// <summary>Presses the jump the reader would press. The click leaves the panel as an event, so a fixture pane
    /// with no host can still prove *which* tab was asked for, and a live pane can prove the column opened.</summary>
    internal void ClickChangeJumpForCheck() => ClickJump(_changesList);
    /// <summary>Presses one row's unfold button — the reader's "show me the file", reached the way a reader
    /// reaches it. The claim a check can then make is about the click, not about a field somebody set.</summary>
    internal void ClickUnfoldForCheck(int index)
    {
        if (ChangeRows().ElementAtOrDefault(index)is not { } row)
            return;
        row.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == "InspectorUnfoldFile")
            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>That button's tip, which is also its state: it names the reading the *next* click gives, so a tip
    /// stuck on one value is a row whose meaning never flips.</summary>
    internal string UnfoldTipForCheck(int index) => ChangeRows().ElementAtOrDefault(index)is { } row
        ? row.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Tag as string == "InspectorUnfoldFile") is { } button
                ? ToolTip.GetTip(button)?.ToString() ?? ""
                : ""
        : "";

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
    internal bool RepoOpenFileButtonForCheck(int index) => _repoBody.GetLogicalDescendants().OfType<Button>()
        .Where(button => button.Tag as string == "InspectorOpenFile").ElementAtOrDefault(index)?.Content is ShapePath icon
        && icon.Data is not null;

    /// <summary>Every string in one row, joined: the two status letters, the path, and the mark when this
    /// conversation also wrote it. Read as text because that is what the reader sees, and a fixture state makes
    /// the whole tab assertable without a repository behind it.</summary>
    internal string RepoRowTextForCheck(int index) => RepositoryRows().ElementAtOrDefault(index)is { } row ? string.Join(" ~ ", row.GetLogicalDescendants().OfType<TextBlock>().Select(TextForCheck)) : "";
    internal int RepoRowCountForCheck => RepositoryRows().Count;
    internal bool RepoComparisonRangeButtonsAbsentForCheck => RepositoryRows().SelectMany(row => row.GetLogicalDescendants())
        .All(control => control is not Button button || button.Tag as string is not ("InspectorGitIndex" or "InspectorGitWorkingTree"));

    internal bool RepoCommitComparisonControlsAbsentForCheck => RepositoryRows().SelectMany(row => row.GetLogicalDescendants())
        .All(control => control is not TextBox
            && (control is not Button button || button.Tag as string is not ("InspectorGitCommitRef" or "InspectorGitCommitCompare")));

    internal bool RepoRowExpandedForCheck(int index) => RepositoryRows().ElementAtOrDefault(index)is { } row
        && row.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault(control => control.Margin == new Avalonia.Thickness(0, 4, 0, 6))?.IsVisible == true;
    internal void ClickRepoRowForCheck(int index)
    {
        if (RepositoryRows().ElementAtOrDefault(index)is not { } row)
            return;
        row.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("inspector-change-header"))?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Compatibility helper; repository paths now toggle their complete comparison inline.</summary>
    internal void ClickRepoUnfoldForCheck(int index) => ClickRepoRowForCheck(index);

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
