using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Markdown.Avalonia;
using AxmolHub.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace AxmolHub;

/// <summary>
/// The right-hand review pane: the plan in full on one tab, the diff of every file this conversation touched on
/// the other. Built imperatively like the rest of the chat UI — no MVVM, style classes are the locator contract.
///
/// It is handed its content on every <see cref="Reload"/> rather than reaching into the workspace itself, so the
/// panel stays a pure view: the caller (ChatPanel) owns the conversation, the diff resolver and the markdown
/// renderer, and this only lays out what it is given. That is also what keeps it checkable — a check can Reload
/// it with a fixture and assert the frame without a live session behind it.
/// </summary>
internal sealed partial class InspectorPanel : UserControl
{
    /// <summary>Raised when the repository tab is entered, and by its own ⟳. The panel never reads git itself:
    /// it has no session, no log and no thread to wait on, and a pane that goes and does I/O is a pane a check
    /// cannot drive from a fixture.</summary>
    public event Action? RepoRefreshRequested;

    /// <summary>Raised by one of the empty-scope jumps: "this list is empty, the other one is not — the reader
    /// could want that one instead". The panel names the tab it wants and never switches itself, because landing
    /// on a tab can mean opening a column that is shut, and only the shell knows the page it is on. It is the
    /// same ask a run's <c>+N −M</c> chip makes, on the same route.</summary>
    public event Action<string>? ScopeJumpRequested;

    /// <summary>Raised on every tab the pane lands on — including the one <see cref="Reload"/> was told to show.
    /// The pane owns which surface is visible; the shell owns which one the next repaint asks for. Without this
    /// the two disagree from the moment a reader presses a header tab and the transcript moves, and the column
    /// bounces them back to the plan they were not reading.</summary>
    public event Action<string>? TabSelected;

    private readonly Button _planTab;
    private readonly Button _changesTab;
    private readonly Button _repoTab;
    private readonly Grid _header;
    private readonly ContentControl _planHost;
    private readonly ScrollViewer _planScroll;
    private readonly Button _planCopyButton;
    private readonly Path _planCopyIcon;
    private int _planCopyFeedbackVersion;
    private readonly ScrollViewer _changesScroll;
    private readonly StackPanel _changesList;
    private readonly ScrollViewer _repoScroll;
    private readonly StackPanel _repoBody;
    private readonly Grid _body;
    private bool _expanded;

    /// <summary>Paths a person had opened on the changes tab. A repaint clears the list to draw the newest
    /// snapshot, and a pane that folds back everything the reader opened on every streamed token is a pane that
    /// cannot be read while the run is still going — so the opened set outlives the rows it was drawn from.</summary>
    private readonly HashSet<string> _openedChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectedRevisions = new(StringComparer.Ordinal);

    public InspectorPanel()
    {
        ClipToBounds = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        // ── Header: three tabs, and nothing else. The pane's two controls — lift the pane over the window, and
        //    shut the column — used to live at this header's right end, which is exactly where they could not be
        //    reached: the × was unreachable while the pane covered the window, and both were unreachable while
        //    the column was shut. They are now the title bar's, where they outlive whichever surface is up (see
        //    MainWindow.axaml). The tabs read as text buttons whose selected one carries an underline plate
        //    rather than as a chrome-heavy TabControl. ──
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(12, 10, 8, 8) };
        _header = header;
        _planTab = TabButton("InspectorTabPlan");
        _changesTab = TabButton("InspectorTabChanges");
        _repoTab = TabButton("InspectorTabRepo");
        _planTab.Click += (_, _) => SelectTab("plan");
        _changesTab.Click += (_, _) => SelectTab("changes");
        // Asking for a read is the panel's business; doing one is not. The caller owns git, the session and the
        // thread this lands on, so the tab only says that the person wanted the repository looked at again.
        _repoTab.Click += (_, _) =>
        {
            SelectTab("repo");
            RepoRefreshRequested?.Invoke();
        };
        header.Children.Add(_planTab);
        Grid.SetColumn(_changesTab, 1);
        _changesTab.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_changesTab);
        Grid.SetColumn(_repoTab, 2);
        _repoTab.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_repoTab);
        root.Children.Add(header);

        // ── Body: the three tab surfaces stacked, only one visible. All live in the tree so switching tabs does
        //    not rebuild them, and a check can read any of them without selecting it first. ──
        _body = new Grid();
        Grid.SetRow(_body, 1);

        _planHost = new ContentControl { Margin = new Thickness(12, 0, 12, 12) };
        // The plan is the one tab whose text runs edge to edge, so it scrolls in the pane's own scroller like the
        // two tabs below it: the viewer's own bar is drawn over the document's right edge and eats the tail of
        // every line (Hub's ScrollBar is opaque, always visible, 10px). The host's margin is what clears it.
        _planCopyIcon = new Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        _planCopyIcon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Copy"));
        _planCopyIcon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        _planCopyButton = new Button
        {
            Content = _planCopyIcon,
            Width = 32,
            Height = 32,
            MinWidth = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 4, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        _planCopyButton.Classes.Add("inspector-plan-copy");
        _planCopyButton.Bind(ToolTip.TipProperty, new DynamicResourceExtension("CopyMarkdown"));
        _planCopyButton.Click += async (_, _) => await CopyPlanMarkdownAsync();
        var planSurface = new Grid();
        planSurface.Children.Add(_planHost);
        planSurface.Children.Add(_planCopyButton);
        _planScroll = new ScrollViewer
        {
            Content = planSurface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _body.Children.Add(_planScroll);

        _changesList = new StackPanel { Spacing = 6 };
        _changesScroll = new ScrollViewer
        {
            Content = _changesList,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _changesScroll.IsVisible = false;
        _body.Children.Add(_changesScroll);

        _repoBody = new StackPanel { Spacing = 6 };
        _repoScroll = new ScrollViewer
        {
            Content = _repoBody,
            Margin = new Thickness(12, 0, 6, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _repoScroll.IsVisible = false;
        _body.Children.Add(_repoScroll);

        root.Children.Add(_body);
        Content = root;
        SelectTab("plan");
    }

    private static Button TabButton(string textKey)
    {
        var button = new Button { Classes = { "inspector-tab" }, Content = HubStrings.Get(textKey), Tag = textKey };
        return button;
    }

    /// <summary>Told by the shell which of its two homes the pane is in, because the two are not the same
    /// reading. A plan laid across a whole window needs the chat column's line length rather than the window's —
    /// a document that runs 1200px per line is not faster to read, it is a scan for the end of each sentence. The
    /// diff keeps every pixel: side-by-side changes are exactly what the room is for. Which of the two surfaces is
    /// up is the title bar's to say, not this pane's: it draws no control for it (see the constructor).</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _planHost.MaxWidth = expanded ? HubMetrics.ColumnMaxWidth : double.PositiveInfinity;
        _planHost.HorizontalAlignment = expanded ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }

    /// <summary>Copies the displayed plan as Markdown and shows a brief success glyph only after the clipboard
    /// write completes. Clipboard failures leave the copy icon unchanged.</summary>
    private async System.Threading.Tasks.Task CopyPlanMarkdownAsync()
    {
        if (_planHost.Content is not MarkdownScrollViewer { Tag: string markdown }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, markdown);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception)
        {
            return;
        }

        var version = ++_planCopyFeedbackVersion;
        if (Application.Current?.TryGetResource("Hub.Icon.CopySuccess", null, out var success) == true
            && success is Geometry successGeometry)
            _planCopyIcon.Data = successGeometry;

        DispatcherTimer.RunOnce(() =>
        {
            if (_planCopyFeedbackVersion == version)
                _planCopyIcon.Data = Application.Current?.TryGetResource("Hub.Icon.Copy", null, out var original) == true
                    ? original as Geometry
                    : null;
        }, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Shows one tab surface and marks its button. All three stay in the tree, so painting a new plan
    /// does not rebuild a diff somebody had opened, and a check can read a tab it never selected. Anything that
    /// is not one of the two action tabs lands on the plan, which is the pane's rest state. The shell is told
    /// every time, including when it was the one that named the tab: one route, so a header click and a repaint
    /// cannot disagree about where the reader is standing.</summary>
    private void SelectTab(string tab)
    {
        var changes = string.Equals(tab, "changes", StringComparison.Ordinal);
        var repo = string.Equals(tab, "repo", StringComparison.Ordinal);
        var plan = !changes && !repo;
        _planTab.Classes.Set("selected", plan);
        _changesTab.Classes.Set("selected", changes);
        _repoTab.Classes.Set("selected", repo);
        _planScroll.IsVisible = plan;
        _changesScroll.IsVisible = changes;
        _repoScroll.IsVisible = repo;
        _planCopyButton.IsVisible = plan && _planHost.Content is MarkdownScrollViewer;
        TabSelected?.Invoke(tab);
    }

    /// <summary>Paints the three tabs from a snapshot and lands on the asked-for one. The diff resolver is lazy
    /// per file: a session of twelve writes must not read twelve files to show a list, only the one a person
    /// opens. The repository state arrives already read — it is the one input here that cost a process, so the
    /// caller owns when to take it and this only lays out what it got.</summary>
    public void Reload(
        string? planMarkdown,
        IReadOnlyList<ChangedFile> changes,
        Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        GitRepositoryState? repository,
        string tab,
        Action<string>? quote = null,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(planMarkdown))
        {
            _planHost.Content = EmptyLine("InspectorPlanEmpty");
        }
        else
        {
            // 引用 from the plan's own right-click menu goes wherever the caller says — the composer, for the one
            // caller there is. The pane has no editor and must not grow one: it is a reading surface.
            var viewer = MarkdownMessageRenderer.Render(planMarkdown!, quote);
            MarkdownMessageRenderer.HandScrollingToHost(viewer);
            _planHost.Content = viewer;
        }

        _changesList.Children.Clear();
        if (changes.Count == 0)
        {
            _changesList.Children.Add(EmptyLine("InspectorChangesEmpty"));
            // A jump earns its line only from a read that already says the tree is dirty. With nothing read,
            // "the repository holds changes" would be a claim this surface never verified, and an invitation to
            // go and find out anyway.
            if (repository is { IsReadable: true } readable && readable.Entries.Count > 0)
                _changesList.Children.Add(Jump("repo", "InspectorChangesJumpRepoFormat", readable.Entries.Count));
        }
        else
        {
            foreach (var change in changes)
                _changesList.Children.Add(BuildChangeRow(change, resolveDiff, resolveSnapshot));
        }

        ReloadRepository(repository, changes);
        SelectTab(tab);
    }

    private void ReloadRepository(GitRepositoryState? repository, IReadOnlyList<ChangedFile> changes)
    {
        _repoBody.Children.Clear();
        // The caveat is the tab's first line and it is never conditional. This list is the repository's, not the
        // session's, and a reader who assumes otherwise will mis-credit a week of their own work to the assistant
        // — which is exactly the confusion the separate tab exists to avoid, so it has to be said even when the
        // repository is empty and the sentence looks like decoration.
        _repoBody.Children.Add(Note("InspectorRepoCaveat"));

        if (repository is null)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoReading"));
            _repoBody.Children.Add(RepositoryHead(null));
            return;
        }

        _repoBody.Children.Add(RepositoryHead(repository));
        if (!repository.IsReadable)
        {
            _repoBody.Children.Add(EmptyLine(RepositoryWhyKey(repository.Outcome)));
            return;
        }
        if (repository.Entries.Count == 0)
        {
            _repoBody.Children.Add(EmptyLine("InspectorRepoEmpty"));
            // The case this exists for: the assistant just ran `git commit`. The working tree is clean, the
            // session's own writes are still on the transcript, and a reader who is told only "matches HEAD" has
            // no way back to what they came here to see.
            if (changes.Count > 0)
                _repoBody.Children.Add(Jump("changes", "InspectorRepoJumpSessionFormat", changes.Count));
            return;
        }
        foreach (var entry in repository.Entries)
            _repoBody.Children.Add(BuildRepositoryRow(entry, repository, TouchedBySession(repository, changes)));
        if (repository.Truncated)
        {
            _repoBody.Children.Add(EmptyLine(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                HubStrings.Get("InspectorRepoTruncatedFormat"), repository.Entries.Count)));
        }
    }

    /// <summary>Why the tab cannot answer, in one of six sentences. Kept as a table rather than a default arm so
    /// a new outcome has to be given words here before it can show an empty list somewhere.</summary>
    private static string RepositoryWhyKey(GitReadOutcome outcome) => outcome switch
    {
        GitReadOutcome.NoWorkspace => "InspectorRepoNoWorkspace",
        GitReadOutcome.NotARepository => "InspectorRepoNotGit",
        GitReadOutcome.GitMissing => "InspectorRepoGitMissing",
        GitReadOutcome.UnsafeRepository => "InspectorRepoUnsafe",
        GitReadOutcome.Failed => "InspectorRepoFailed",
        _ => "InspectorRepoEmpty",
    };

    /// <summary>The branch line and the one way to ask for the read again. The glyph is git's own idea — a
    /// branch — and the count only appears when the session is off its upstream, because "0 ahead · 0 behind"
    /// would be a fact nobody needed telling.</summary>
    private Control RepositoryHead(GitRepositoryState? state)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StringBuilder();
        if (state is { IsReadable: true } readable)
        {
            text.Append(string.IsNullOrEmpty(readable.Branch) ? "—" : readable.Branch);
            if (readable.Ahead > 0 || readable.Behind > 0)
            {
                text.Append(" · ").Append(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    HubStrings.Get("InspectorRepoAheadBehindFormat"), readable.Ahead, readable.Behind));
            }
        }
        else
        {
            text.Append("—");
        }
        var head = Themed(new TextBlock
        {
            Text = text.ToString(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-note" },
        }, "Hub.TextSecondary");
        ToolTip.SetTip(head, HubStrings.Get("InspectorTabRepo"));
        row.Children.Add(head);

        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Refresh"));
        var refresh = new Button { Classes = { "viewer-action" }, Tag = "InspectorRepoRefresh", Content = icon };
        ToolTip.SetTip(refresh, HubStrings.Get("InspectorRepoRefresh"));
        refresh.Click += (_, _) => RepoRefreshRequested?.Invoke();
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);
        return row;
    }

    /// <summary>Which of these dirty paths this conversation also wrote. The two lists use different anchors —
    /// git speaks from the repository root, <see cref="ChatChanges"/> from the session's folder — so a workspace
    /// that is a subfolder matches by suffix rather than never matching at all. The mark is the one place the two
    /// tabs are allowed to talk to each other, and it adds no claim: it says a file is in both lists.</summary>
    private static HashSet<string> TouchedBySession(GitRepositoryState state, IReadOnlyList<ChangedFile> changes)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var relative = change.RelativePath.Replace('\\', '/');
            foreach (var entry in state.Entries)
            {
                if (string.Equals(entry.RelativePath, relative, StringComparison.Ordinal)
                    || entry.RelativePath.EndsWith("/" + relative, StringComparison.Ordinal))
                    touched.Add(entry.RelativePath);
            }
        }
        return touched;
    }

    private Control BuildRepositoryRow(GitStatusEntry entry, GitRepositoryState state, HashSet<string> touched)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };
        var header = new Button { Classes = { "inspector-change-header" }, Tag = entry.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        // git's own two columns, not Hub's four verdicts: this is the repository's state and the letters are what
        // a developer reads a status line in. Monospace keeps the pair the width it is in `git status` itself.
        var mark = new TextBlock
        {
            Text = entry.Mark,
            Classes = { "inspector-verdict" },
            FontFamily = ThemedFont("Hub.Font.Mono"),
        };
        mark.Classes.Add(entry.Untracked ? "created" : "edited");
        grid.Children.Add(mark);

        var nameColumn = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        nameColumn.Children.Add(Themed(new TextBlock
        {
            Text = entry.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-path" },
        }, "Hub.TextPrimary"));
        if (touched.Contains(entry.RelativePath))
        {
            nameColumn.Children.Add(Themed(new TextBlock
            {
                Text = HubStrings.Get("InspectorRepoSessionAlsoTouched"),
                TextWrapping = TextWrapping.Wrap,
                Classes = { "inspector-note" },
            }, "Hub.TextTertiary"));
        }
        Grid.SetColumn(nameColumn, 1);
        nameColumn.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(nameColumn);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
            {
                var diff = GitRepository.DiffFor(state, entry);
                detail.Content = diff is { Length: > 0 }
                    ? BuildDiffBlock(diff)
                    : EmptyLine("InspectorRepoNoDiff");
            }
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
        };

        container.Children.Add(header);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>A muted line of prose in the body. Used twice (the caveat and the shared-path mark), so it is one
    /// style rather than a hand-placed size in two spots.</summary>
    private static Control Note(string textKey) => Themed(new TextBlock
    {
        Text = HubStrings.Get(textKey),
        TextWrapping = TextWrapping.Wrap,
        Classes = { "inspector-note" },
    }, "Hub.TextTertiary");

    private static Control EmptyLine(string textKey)
        => Themed(new TextBlock
        {
            Text = HubStrings.Get(textKey),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "inspector-empty" },
        }, "Hub.TextTertiary");

    /// <summary>A sentence that is also a button: the empty-state jump. Two decisions in its construction. It
    /// carries <c>inspector-jump</c> and never <c>inspector-empty</c>, because the notice accessor reads this body
    /// by that class and a jump that borrowed the class would silently rewrite what the tab is said to end on.
    /// And its text is an explicit child rather than a bound foreground, so the hover rule in the shell's styles
    /// can reach it — a <see cref="Themed"/> brush would be a local binding, and a local binding outranks a
    /// selector, which is how a control that should brighten when pointed at would stay grey.</summary>
    private Control Jump(string targetTab, string textKey, int count)
    {
        var text = new TextBlock
        {
            Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, HubStrings.Get(textKey), count),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-jump" },
        };
        var button = new Button { Classes = { "inspector-jump" }, Tag = targetTab, Content = text };
        button.Click += (_, _) => ScopeJumpRequested?.Invoke(targetTab);
        return button;
    }

    private Control BuildChangeRow(ChangedFile change, Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var container = new StackPanel { Spacing = 0, Classes = { "inspector-change" } };

        var (added, removed) = PreviewCounts(change);

        var header = new Button { Classes = { "inspector-change-header" }, Tag = change.RelativePath };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var verdict = new TextBlock
        {
            Text = VerdictMark(change.Verdict),
            Classes = { "inspector-verdict", VerdictClass(change.Verdict) },
        };
        grid.Children.Add(verdict);

        var name = Themed(new TextBlock
        {
            Text = change.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-path" },
        }, "Hub.TextPrimary");
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(name);

        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (added > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorAddedFormat"), added),
            Classes = { "inspector-count-added" },
        }, "Hub.Success"));
        if (removed > 0) counts.Children.Add(Themed(new TextBlock
        {
            Text = string.Format(HubStrings.Get("InspectorRemovedFormat"), removed),
            Classes = { "inspector-count-removed" },
        }, "Hub.Danger"));
        Grid.SetColumn(counts, 2);
        grid.Children.Add(counts);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        // Re-opened rather than rebuilt: a repaint of the same path carries the reader's own expansion forward,
        // so a run that keeps writing does not fold away the file they are in the middle of looking at.
        if (_openedChanges.Contains(change.RelativePath))
        {
            detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
            detail.IsVisible = true;
            header.Classes.Add("expanded");
        }
        header.Click += (_, _) =>
        {
            if (!detail.IsVisible && detail.Content is null)
                detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
            detail.IsVisible = !detail.IsVisible;
            header.Classes.Set("expanded", detail.IsVisible);
            if (detail.IsVisible) _openedChanges.Add(change.RelativePath);
            else _openedChanges.Remove(change.RelativePath);
        };

        container.Children.Add(header);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>The row's <c>+N −M</c> comes from the selected latest revision, with the transcript's recorded
    /// count retained for older revisions that predate durable change metadata.</summary>
    private static (int Added, int Removed) PreviewCounts(ChangedFile change)
        => change.Lines is { } lines
            ? (lines.Added, lines.Removed)
            : (0, 0);

    private Control BuildDiffDetail(ChangedFile change, Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var body = new StackPanel { Spacing = 6 };
        var history = change.History ?? [];
        var remembered = _selectedRevisions.GetValueOrDefault(change.RelativePath, "");
        var selectedId = history.Any(revision => revision.Id == remembered)
            ? remembered
            : history.LastOrDefault()?.Id ?? change.ChangeId ?? "";
        if (history.Count > 1)
        {
            var selector = new ComboBox
            {
                Tag = "InspectorHistorySelector",
                ItemsSource = history.Select(revision => new ComboBoxItem
                {
                    Content = revision.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    Tag = revision.Id,
                }).ToArray(),
                SelectedIndex = Math.Max(0, history.ToList().FindIndex(revision => revision.Id == selectedId)),
                MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(selector);
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                _selectedRevisions[change.RelativePath] = id;
                body.Children[1] = BuildSelectedDiff(change with { SelectedRevisionId = id }, resolveDiff,
                    resolveSnapshot);
            };
        }
        var selected = change with { SelectedRevisionId = selectedId };
        if (history.Count > 1)
        {
            var index = history.ToList().FindIndex(revision => revision.Id == selectedId);
            if (index >= 0 && body.Children[0] is ComboBox combo) combo.SelectedIndex = index;
        }
        body.Children.Add(BuildSelectedDiff(selected, resolveDiff, resolveSnapshot));
        return body;
    }

    private Control BuildSelectedDiff(ChangedFile change,
        Func<ChangedFile, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var (diff, state) = resolveDiff(change);
        return diff is null
            ? EmptyLine(DiffUnavailableKey(change, state))
            : BuildDiffBlock(diff);
    }

    private static string DiffUnavailableKey(ChangedFile change, UndoCopyState state) => state switch
    {
        UndoCopyState.CreatedFile => "InspectorDiffCreatedFile",
        UndoCopyState.PendingSnapshot => "InspectorDiffPendingSnapshot",
        UndoCopyState.MissingSnapshot => "InspectorDiffMissingSnapshot",
        UndoCopyState.EvictedOrSpent => change.Verdict == ChangeVerdict.Unchanged
            ? "InspectorDiffRefusedLine"
            : "InspectorDiffCopyGone",
        _ => "InspectorDiffUnreadable",
    };

    /// <summary>Renders a unified diff line by line, adds and deletes on their own tinted plates. A horizontal
    /// scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.</summary>
    private static Control BuildDiffBlock(string diff)
    {
        var lines = new StackPanel { Spacing = 0 };
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var block = new Border();
            block.Classes.Add("inspector-diff-line");
            var lineClass = DiffLineClass(line);
            if (lineClass.Length > 0) block.Classes.Add(lineClass);
            var text = Themed(new SelectableTextBlock
            {
                Text = line.Length == 0 ? " " : line,
                FontFamily = ThemedFont("Hub.Font.Mono"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
            }, "Hub.TextSecondary");
            block.Child = text;
            lines.Children.Add(block);
        }

        return new ScrollViewer
        {
            Content = new Border { Classes = { "inspector-diff" }, Child = lines },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            ClipToBounds = true,
        };
    }

    private static Control BuildSnapshotBlock(string text)
    {
        var viewer = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
        };
        return new ScrollViewer
        {
            Content = new Border { Classes = { "inspector-diff" }, Child = viewer },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            ClipToBounds = true,
        };
    }

    private static string DiffLineClass(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            return "diff-header";
        if (line.StartsWith("@@", StringComparison.Ordinal)) return "diff-hunk";
        if (line.StartsWith('+')) return "diff-add";
        if (line.StartsWith('-')) return "diff-del";
        return "";
    }

    private static string VerdictMark(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "＋",
        ChangeVerdict.Edited => "✎",
        ChangeVerdict.Unchanged => "·",
        _ => "⊘",
    };

    private static string VerdictClass(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Created => "created",
        ChangeVerdict.Edited => "edited",
        ChangeVerdict.Unchanged => "unchanged",
        _ => "refused",
    };

    // ── theming helpers: brushes and fonts bind through DynamicResourceExtension so they follow a theme switch
    //    that happens after this panel is already on screen. ──

    private static T Themed<T>(T control, string brushKey) where T : Control
    {
        if (control is TextBlock text)
            text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brushKey));
        return control;
    }

    private static FontFamily ThemedFont(string key)
        => Application.Current?.TryGetResource(key, null, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;
internal bool IsRepositoryTabVisible=> _repoScroll.IsVisible;
}
