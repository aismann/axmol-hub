using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
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
    internal Func<GitRepositoryState, GitStatusEntry, GitFileRange, Task<ChatFileSnapshot?>>? ReadComparisonRequested { get; set; }
    internal Func<string, string, string, ChangesEditBuffer?>? OpenEditBuffer { get; set; }
    internal event Action? FilesSaved;
    private string _conversationId = "";
    private readonly Dictionary<string, ChangesEditSession> _editSessions = new(StringComparer.Ordinal);
    private ChangesEditSession EditSession => _editSessions.TryGetValue(_conversationId, out var session)
        ? session : _editSessions[_conversationId] = new ChangesEditSession();
    private readonly Button _editDiff;
    private readonly Button _cancelEdit;
    private readonly Button _saveEdit;
    private readonly TextBlock _editStatus;

    /// <summary>Raised by one of the empty-scope jumps: "this list is empty, the other one is not — the reader
    /// could want that one instead". The panel names the tab it wants and never switches itself, because landing
    /// on a tab can mean opening a column that is shut, and only the shell knows the page it is on. It is the
    /// same ask a run's <c>+N −M</c> chip makes, on the same route.</summary>

    /// <summary>Raised on every tab the pane lands on — including the one <see cref="Reload"/> was told to show.
    /// The pane owns which surface is visible; the shell owns which one the next repaint asks for. Without this
    /// the two disagree from the moment a reader presses a header tab and the transcript moves, and the column
    /// bounces them back to the plan they were not reading.</summary>
    public event Action<string>? TabSelected;

    private readonly Button _planTab;
    private readonly Button _changesTab;
    private readonly StackPanel _tabs;
    private readonly Button _viewSettings;
    private readonly MenuFlyout _viewMenu;
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

    /// <summary>Which of the two layouts a diff block draws. Unified is what the pane has always shown and what
    /// the column's default width is comfortable at; side-by-side is what a wide column — or the pane lifted over
    /// the window — is for, and the reader asks for it from the header gear. Not persisted: it is a way of reading
    /// the diff in front of you, like which revision is selected, not a statement about the next session.</summary>
    private bool _splitDiff;

    /// <summary>How to redraw each diff that is already open, so a layout change reaches the blocks on screen
    /// instead of only the ones opened afterwards. Registered per row by <see cref="Reload"/> and dropped with the
    /// rows it was drawn from, which is why the list is cleared there rather than filtered here.</summary>
    private readonly List<Action> _diffRebuilds = [];

    /// <summary>Paths a person had opened on the changes tab. A repaint clears the list to draw the newest
    /// snapshot, and a pane that folds back everything the reader opened on every streamed token is a pane that
    /// cannot be read while the run is still going — so the opened set outlives the rows it was drawn from.</summary>
    private readonly HashSet<string> _openedChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectedRevisions = new(StringComparer.Ordinal);

    /// <summary>Paths whose row is showing the whole file rather than its hunks — the row's unfold button, in the
    /// place GitHub puts the same control. It outlives a repaint for the reason <see cref="_openedChanges"/> does:
    /// a run that keeps writing must not fold a file back to three lines of context under the reader, and folding
    /// is a second click.</summary>
    private readonly HashSet<string> _expandedFiles = new(StringComparer.Ordinal);

    public InspectorPanel()
    {
        ClipToBounds = true;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };

        // ── Header: the page's own gear at the far right, the copy plate beside it, and the changes editing pair
        //    when it is on. The two tabs left this row for the title bar (see TabStrip): the strip a reader
        //    switches a surface with is one strip, in the same place in both of the pane's homes, and a tab inside
        //    this header would have to be moved between them. The control that used to sit beside them — the
        //    「会话改动 ▾」 scope picker — was a page setting wearing a tab's clothes: it, and the diff layout above
        //    it, answer one question ("how is this page read"), so they are one control with four rows in it now.
        //    (The pane's lift/shut pair left this header even earlier — see MainWindow.axaml.) ──
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(12, 10, 8, 8) };
        _header = header;
        _planTab = TabButton("InspectorTabPlan");
        _changesTab = TabButton("InspectorTabChanges");
        _planTab.Click += (_, _) => SelectTab("plan");
        _changesTab.Click += (_, _) =>
        {
            SelectTab("repo");
            RepoRefreshRequested?.Invoke();
        };
        // The tabs are not in this header, and not in this pane's tree at all: the shell hosts the strip in the
        // title bar's row, in the column the pane's own left edge is, so "the tabs start where the pane starts" is
        // a layout answer rather than a pair of coordinates kept in step by hand. The plate is painted in the bar's
        // own colour because the strip is drawn *on* that bar, where the page title can otherwise run underneath
        // it: the plate ends the title at the tabs instead of printing two texts on top of each other, and it is
        // the only hundred-odd pixels of the bar that stop dragging the window.
        _tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _tabs.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("Hub.TitleBarBackground"));
        _tabs.Children.Add(_planTab);
        _tabs.Children.Add(_changesTab);
        var settingsIcon = new Path
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        settingsIcon.Bind(Path.DataProperty, new DynamicResourceExtension("Hub.Icon.Settings"));
        settingsIcon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        // The plate matches the plan's copy button beside it — same 32px box, same empty border — because the two
        // sit in one header and a reader should not have to work out that both are buttons.
        _viewSettings = new Button
        {
            Content = settingsIcon,
            Name = "InspectorViewSettings",
            Tag = "InspectorViewSettings",
            Width = 32,
            Height = 32,
            MinWidth = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _viewSettings.Bind(ToolTip.TipProperty, new DynamicResourceExtension("InspectorSettingsTip"));
        // A menu rather than a context menu, built fresh on every open: that is how exactly one tick is drawn per
        // group without keeping a second copy of the state in sync — the same idiom the diff viewer's gear uses.
        _viewMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        FlyoutBase.SetAttachedFlyout(_viewSettings, _viewMenu);
        _viewSettings.Click += (_, _) =>
        {
            if (_viewMenu.IsOpen)
            {
                _viewMenu.Hide();
                return;
            }
            BuildViewMenu(_viewMenu);
            FlyoutBase.ShowAttachedFlyout(_viewSettings);
        };
        Grid.SetColumn(_viewSettings, 2);
        header.Children.Add(_viewSettings);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var pencil = new Path
        {
            Data = Geometry.Parse("M16 3 21 8 8 21 3 21 3 16ZM14 5 19 10"),
            Width = 15, Height = 15, Stretch = Stretch.Uniform, Fill = Brushes.Transparent,
            StrokeThickness = 1.6,
        };
        pencil.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        _editDiff = new Button { Content = pencil, Tag = "InspectorEditDiff", Classes = { "viewer-action" } };
        _editDiff.Bind(ToolTip.TipProperty, new DynamicResourceExtension("InspectorEditDiff"));
        _cancelEdit = new Button { Content = HubStrings.Get("InspectorCancelEdit"), Tag = "InspectorCancelEdit" };
        _saveEdit = new Button { Content = HubStrings.Get("InspectorSaveEdit"), Tag = "InspectorSaveEdit" };
        _editStatus = Themed(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) }, "Hub.TextSecondary");
        _editDiff.Click += (_, _) =>
        {
            EditSession.Begin();
            _editStatus.Text = null;
            foreach (var rebuild in _diffRebuilds.ToArray()) rebuild();
            UpdateEditActions();
        };
        _cancelEdit.Click += (_, _) =>
        {
            EditSession.Cancel();
            _editStatus.Text = null;
            foreach (var rebuild in _diffRebuilds.ToArray()) rebuild();
            UpdateEditActions();
        };
        _saveEdit.Click += (_, _) =>
        {
            try
            {
                var failure = EditSession.Save();
                _editStatus.Text = failure is null ? null : HubStrings.Get("InspectorEditSaveFailed") + " " + failure;
                if (failure is null)
                {
                    foreach (var rebuild in _diffRebuilds.ToArray()) rebuild();
                    FilesSaved?.Invoke();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _editStatus.Text = HubStrings.Get("InspectorEditSaveFailed") + " " + ex.Message;
            }
            UpdateEditActions();
        };
        editActions.Children.Add(_editDiff);
        editActions.Children.Add(_cancelEdit);
        editActions.Children.Add(_saveEdit);
        Grid.SetColumn(editActions, 3);
        header.Children.Add(editActions);
        root.Children.Add(header);
        Grid.SetRow(_editStatus, 1);
        root.Children.Add(_editStatus);

        // ── Body: the three tab surfaces stacked, only one visible. All live in the tree so switching tabs does
        //    not rebuild them, and a check can read any of them without selecting it first. ──
        _body = new Grid();
        Grid.SetRow(_body, 2);

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
            Margin = new Thickness(4, 0, 0, 0),
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
        Grid.SetColumn(_planCopyButton, 1);
        header.Children.Add(_planCopyButton);
        _planScroll = new ScrollViewer
        {
            Content = _planHost,
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

    /// <summary>The Plan/Changes strip, handed to the shell once and hosted by it — never drawn inside this pane.
    /// Both of the pane's homes put it in the same title-bar slot (see <c>InspectorTabSlot</c> in MainWindow.axaml),
    /// which is what keeps a reader's switch between the two surfaces where it is while the pane is lifted over the
    /// window, and what keeps the tabs' left edge on the pane's own edge in either state without this file
    /// measuring anything. The shell reads it through <c>SetInspectorPane</c>, the one place the pane changes
    /// hands.</summary>
    public Control TabStrip => _tabs;

    private static Button TabButton(string textKey)
    {
        var button = new Button { Classes = { "inspector-tab" }, Content = HubStrings.Get(textKey), Tag = textKey, Name = textKey };
        return button;
    }

    private Control FileActions(string path)
    {
        var icon = new Path
        {
            Data = Geometry.Parse("M14 3h7v7M21 3 11 13M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"),
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        icon.Bind(Path.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        var button = new Button
        {
            Content = icon,
            Classes = { "inspector-file-menu" },
            Tag = "InspectorOpenFile",
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, HubStrings.Get("InspectorOpenFile"));
        button.Click += (_, _) => OpenFileRequested?.Invoke(path);
        return button;
    }

    /// <summary>One row's unfold button: GitHub's control of the same name, in the same place, doing the same
    /// thing — the file's whole text instead of its hunks, and back. A sibling of the header rather than a child,
    /// because the header itself opens and closes the row and a button buried in a button is a target the pointer
    /// cannot aim at. The glyph and the tip flip together, so the button says which way the next click goes.</summary>
    private Button UnfoldButton(string path, Action<bool> toggled)
    {
        var icon = new Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
        };
        var button = new Button
        {
            Content = icon,
            Classes = { "inspector-file-menu" },
            Tag = "InspectorUnfoldFile",
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var whole = _expandedFiles.Contains(path);
        // The ink is bound once and the glyph swapped, rather than two paths and one hidden: a row repaints its
        // diff on this click already, and a second icon would be a second control to keep in step.
        icon.Bind(Shape.StrokeProperty, new DynamicResourceExtension("Hub.TextSecondary"));
        void Paint()
        {
            icon.Bind(Path.DataProperty, new DynamicResourceExtension(whole ? "Hub.Icon.Fold" : "Hub.Icon.Unfold"));
            ToolTip.SetTip(button, HubStrings.Get(whole ? "InspectorCollapseFile" : "InspectorExpandFile"));
        }
        Paint();
        button.Click += (_, _) =>
        {
            whole = !whole;
            if (whole) _expandedFiles.Add(path);
            else _expandedFiles.Remove(path);
            Paint();
            toggled(whole);
        };
        return button;
    }

    /// <summary>Told by the shell which of its two homes the pane is in, because the two are not the same
    /// reading. A plan laid across a whole window needs the chat column's line length rather than the window's —
    /// a document that runs 1200px per line is not faster to read, it is a scan for the end of each sentence. The
    /// diff keeps every pixel: side-by-side changes are exactly what the room is for. The tabs are the one control
    /// this state does not reach: they live in the title bar (<see cref="TabStrip"/>), and the shell moves their
    /// slot rather than the strip — so being lifted changes where the pane reads, never what it is read with.
    /// </summary>
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

    /// <summary>Fills the header gear's menu: two groups over one page — how a diff is laid out, and which of the
    /// two change readings is up. Built fresh on every open, so the tick sits on what is true at that moment and
    /// there is no second copy of either choice to hold in step: the menu, the tabs and the diff blocks all read
    /// the same two fields.</summary>
    private void BuildViewMenu(MenuFlyout menu)
    {
        var repository = IsRepositoryTabVisible;
        menu.Items.Clear();
        menu.Items.Add(MenuCaption("DiffViewSection"));
        menu.Items.Add(MenuChoice("DiffSplitMode", "InspectorMenuSplit", _splitDiff, () =>
        {
            menu.Hide();
            SetDiffView(split: true);
        }));
        menu.Items.Add(MenuChoice("DiffInlineMode", "InspectorMenuUnified", !_splitDiff, () =>
        {
            menu.Hide();
            SetDiffView(split: false);
        }));
    }

    /// <summary>Switches the layout of every diff the pane shows, the ones already open included: a layout is a
    /// way of reading the same diff, so changing it with three files open must not mean collapsing and reopening
    /// them. A border row carries no diff, and a row that was never opened carries no content to redraw.</summary>
    private void SetDiffView(bool split)
    {
        if (_splitDiff == split) return;
        _splitDiff = split;
        foreach (var rebuild in _diffRebuilds) rebuild();
    }

    private static MenuItem MenuChoice(string textKey, string tag, bool current, Action pick)
    {
        var item = new MenuItem
        {
            Header = MenuRow.Build(null, HubStrings.Get(textKey), current),
            Tag = tag,
            IsChecked = current,
        };
        item.Click += (_, _) => pick();
        return item;
    }

    private static MenuItem MenuCaption(string textKey) => new()
    {
        Header = HubStrings.Get(textKey),
        Tag = textKey,
        Classes = { "menu-caption" },
        IsHitTestVisible = false,
        Focusable = false,
    };

    private void SelectTab(string tab)
    {
        // Changes is always the repository-wide view. Map the legacy session scope to the same tab so an old
        // saved shell state or an in-flight jump cannot resurrect the session-only list.
        var plan = !string.Equals(tab, "changes", StringComparison.Ordinal)
            && !string.Equals(tab, "repo", StringComparison.Ordinal);
        var selected = plan ? "plan" : "repo";
        _planTab.Classes.Set("selected", plan);
        _changesTab.Classes.Set("selected", !plan);
        _planScroll.IsVisible = plan;
        _changesScroll.IsVisible = false;
        _repoScroll.IsVisible = !plan;
        _planCopyButton.IsVisible = plan && _planHost.Content is MarkdownScrollViewer;
        UpdateEditActions();
        TabSelected?.Invoke(selected);
    }

    /// <summary>Paints the three tabs from a snapshot and lands on the asked-for one. The diff resolver is lazy
    /// per file: a session of twelve writes must not read twelve files to show a list, only the one a person
    /// opens. The repository state arrives already read — it is the one input here that cost a process, so the
    /// caller owns when to take it and this only lays out what it got.</summary>
    public void Reload(
        string? planMarkdown,
        IReadOnlyList<ChangedFile> changes,
        Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        GitRepositoryState? repository,
        string tab,
        Action<string>? quote = null,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot = null,
        string conversationId = "")
    {
        if (_conversationId != conversationId)
        {
            _conversationId = conversationId;
            _openedChanges.Clear();
            _selectedRevisions.Clear();
            _expandedFiles.Clear();
            _editStatus.Text = null;
        }
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

        // The rows about to be thrown away were the only holders of how to redraw their diffs; a stale closure
        // here would redraw a control that no longer has a parent.
        _diffRebuilds.Clear();
        _changesList.Children.Clear();
        if (changes.Count == 0)
        {
            _changesList.Children.Add(EmptyLine("InspectorChangesEmpty"));
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
            return;
        }
        foreach (var entry in repository.Entries)
            _repoBody.Children.Add(BuildRepositoryRow(entry, repository));
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

    private Control BuildRepositoryRow(GitStatusEntry entry, GitRepositoryState state)
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

        var nameColumn = Themed(new TextBlock
        {
            Text = entry.RelativePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "inspector-path" },
            VerticalAlignment = VerticalAlignment.Center,
        }, "Hub.TextPrimary");
        Grid.SetColumn(nameColumn, 1);
        nameColumn.Margin = new Thickness(6, 0, 6, 0);
        grid.Children.Add(nameColumn);

        header.Content = grid;

        var detail = new ContentControl { IsVisible = false, Margin = new Thickness(0, 4, 0, 6) };
        void ShowDiff(string? diff)
        {
            var detailBody = new StackPanel { Spacing = 5 };
            detailBody.Children.Add(diff is { Length: > 0 } ? BuildDiffBlock(entry.RelativePath, diff) : EmptyLine("InspectorRepoNoDiff"));
            detail.Content = detailBody;
        }
        var selectedRange = GitFileRange.WorkingTreeAgainstHead;
        var generation = 0;
        void BuildRepoDetail(bool whole)
        {
            var request = ++generation;
            if (ReadComparisonRequested is { } read)
            {
                var owner = _conversationId;
                var range = selectedRange;
                detail.Content = EmptyLine("InspectorRepoReading");
                _ = FillComparison();
                async Task FillComparison()
                {
                    try
                    {
                        var snapshot = await read(state, entry, range);
                        if (request != generation || owner != _conversationId) return;
                        if (snapshot is null) { ShowDiff(GitRepository.DiffFor(state, entry)); return; }
                        var body = new StackPanel { Spacing = 5 };
                        if (range == GitFileRange.WorkingTreeAgainstHead
                            && EditableDiff(entry.RelativePath, snapshot.Before) is { } editable)
                        {
                            body.Children.Add(editable);
                            if (editable is ChangesDiffEditView) { detail.Content = body; return; }
                        }
                        var diff = FileDiff.Unified(snapshot.Before, snapshot.After, entry.RelativePath,
                            whole ? ChatChanges.InspectorLimits.Expanded() : ChatChanges.InspectorLimits);
                        body.Children.Add(BuildDiffBlock(entry.RelativePath, diff, snapshot));
                        detail.Content = body;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        if (request == generation && owner == _conversationId)
                            detail.Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap };
                    }
                }
                return;
            }
            if (!whole || WholeDiffRequested is not { } ask)
            {
                ShowDiff(GitRepository.DiffFor(state, entry));
                return;
            }
            // The unread side of a repository file is git's to read, so this is the one row that says what it is
            // waiting on. A read that fails falls back to the hunks rather than to an empty row: they are still true.
            detail.Content = EmptyLine("InspectorRepoReading");
            _ = FillWholeRepoDiff(ask, state, entry, diff =>
            {
                // The row may have been redrawn, folded, or repainted while git was reading. Only the row that is
                // still expanded and still showing this content gets the answer.
                if (!_expandedFiles.Contains(entry.RelativePath) || detail.Content is null) return;
                ShowDiff(diff);
            });
        }
        // A layout change reaches this row too, but only once it has been opened: an unopened file has no diff on
        // screen to redraw, and reading one to keep it in step would be a git read nobody asked for.
        _diffRebuilds.Add(() =>
        {
            if (detail.Content is not null) BuildRepoDetail(_expandedFiles.Contains(entry.RelativePath));
        });
        header.Click += (_, _) =>
        {
            if (detail.IsVisible)
            {
                detail.IsVisible = false;
                header.Classes.Set("expanded", false);
                return;
            }
            BuildRepoDetail(_expandedFiles.Contains(entry.RelativePath));
            detail.IsVisible = true;
            header.Classes.Set("expanded", true);
        };

        var repositoryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        repositoryRow.Children.Add(header);
        var repositoryFileActions = FileActions(state.Root is { Length: > 0 } root
            ? System.IO.Path.Combine(root, entry.RelativePath) : entry.RelativePath);
        Grid.SetColumn(repositoryFileActions, 1);
        repositoryRow.Children.Add(repositoryFileActions);
        container.Children.Add(repositoryRow);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>Waits for the shell's whole-file answer and hands it back on the UI thread it was asked from. A
    /// failure and an unanswered read are the same thing here — null — because the caller already has the hunks
    /// to fall back to, and a click that produced an exception dialog over a file preview would be worse than one
    /// that showed less.</summary>
    private static async Task FillWholeRepoDiff(Func<GitRepositoryState, GitStatusEntry, Task<string?>> ask,
        GitRepositoryState state, GitStatusEntry entry, Action<string?> done)
    {
        string? diff = null;
        try
        {
            diff = await ask(state, entry).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diff = null;
        }
        done(diff);
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

    /// <summary>A change row opened for reading rather than for its diff. The panel names files but has no session
    /// and does no I/O of its own, so the path goes out exactly as the repository refresh ask does.</summary>
    internal event Action<string>? OpenFileRequested;

    /// <summary>The whole-file diff of one repository entry, asked for when a repository row is unfolded. Unlike
    /// a session's change — whose two texts the transcript already holds as snapshots — a repository file's other
    /// side lives in git, and this pane does no I/O of its own: the ask goes out exactly as the refresh and the
    /// comparison asks do, and the shell answers with a diff or null.</summary>
    internal Func<GitRepositoryState, GitStatusEntry, Task<string?>>? WholeDiffRequested { get; set; }

    private Control BuildChangeRow(ChangedFile change, Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
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
        _diffRebuilds.Add(() =>
        {
            if (detail.Content is not null) detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
        });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        row.Children.Add(header);
        var unfoldButton = UnfoldButton(change.RelativePath, whole =>
        {
            // Unfolding a row that was still folded also opens it: the click asked to see more, and a row that
            // answered by staying shut would read as a click that missed.
            if (!detail.IsVisible)
            {
                detail.IsVisible = true;
                header.Classes.Set("expanded", true);
                _openedChanges.Add(change.RelativePath);
            }
            detail.Content = BuildDiffDetail(change, resolveDiff, resolveSnapshot);
        });
        Grid.SetColumn(unfoldButton, 1);
        row.Children.Add(unfoldButton);
        var fileMenuButton = FileActions(change.RelativePath);
        Grid.SetColumn(fileMenuButton, 2);
        row.Children.Add(fileMenuButton);
        container.Children.Add(row);
        container.Children.Add(detail);
        return container;
    }

    /// <summary>The row's <c>+N −M</c> comes from the selected latest revision, with the transcript's recorded
    /// count retained for older revisions that predate durable change metadata.</summary>
    private static (int Added, int Removed) PreviewCounts(ChangedFile change)
        => change.Lines is { } lines
            ? (lines.Added, lines.Removed)
            : (0, 0);

    private Control BuildDiffDetail(ChangedFile change, Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
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
            // Keep content as data: a Control here becomes ComboBox's VisualBrush snapshot, which can capture
            // its unstyled black foreground before the popup attaches. Template both surfaces independently.
            var historyText = new FuncDataTemplate<string>((value, _) => Themed(new TextBlock
            {
                Text = value,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "inspector-history-text" },
            }, "Hub.TextPrimary"));
            var selector = new ComboBox
            {
                Tag = "InspectorHistorySelector",
                Classes = { "inspector-history" },
                FontSize = 11,
                SelectionBoxItemTemplate = historyText,
                VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = history.Select(revision => new ComboBoxItem
                {
                    Content = revision.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    ContentTemplate = historyText,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    MinHeight = 24,
                    Padding = new Thickness(8, 4),
                    Tag = revision.Id,
                }).ToArray(),
                SelectedIndex = Math.Max(0, history.ToList().FindIndex(revision => revision.Id == selectedId)),
                MinHeight = 24,
                Height = 26,
                Padding = new Thickness(7, 3),
                Margin = new Thickness(0),
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
        Func<ChangedFile, bool, (string? Diff, UndoCopyState State)> resolveDiff,
        Func<ChangedFile, (ChatFileSnapshot? Snapshot, UndoCopyState State)>? resolveSnapshot)
    {
        var (diff, state) = resolveDiff(change, _expandedFiles.Contains(change.RelativePath));
        if (diff is null) return EmptyLine(DiffUnavailableKey(change, state));

        var body = new StackPanel { Spacing = 6 };
        var snapshot = resolveSnapshot?.Invoke(change).Snapshot;
        var latest = change.History?.LastOrDefault()?.Id ?? change.ChangeId;
        var historic = change.SelectedRevisionId is { Length: > 0 } id && latest is not null && id != latest;
        if (snapshot is not null && !historic && EditableDiff(change.RelativePath, snapshot.Before) is { } editable)
        {
            body.Children.Add(editable);
            if (editable is ChangesDiffEditView) return body;
        }
        else if (EditSession.Editing) body.Children.Add(Note("InspectorEditSnapshotRequired"));
        if (snapshot is not null && !historic && !EditSession.Editing
            && OpenEditBuffer?.Invoke(_conversationId, change.RelativePath, snapshot.Before) is { } current)
        {
            // Latest changes follow the workspace after either editor saves. Historical snapshots stay immutable.
            snapshot = snapshot with { After = current.Text };
            diff = FileDiff.Unified(snapshot.Before, snapshot.After, change.RelativePath,
                _expandedFiles.Contains(change.RelativePath) ? ChatChanges.InspectorLimits.Expanded() : ChatChanges.InspectorLimits);
        }
        body.Children.Add(BuildDiffBlock(change.RelativePath, diff, snapshot));
        return body;
    }

    private void UpdateEditActions()
    {
        var changes = _repoScroll.IsVisible;
        _editDiff.IsVisible = changes && !EditSession.Editing;
        _editDiff.IsEnabled = OpenEditBuffer is not null && _conversationId.Length > 0;
        _cancelEdit.IsVisible = changes && EditSession.Editing;
        _saveEdit.IsVisible = changes && EditSession.Editing;
        _saveEdit.IsEnabled = EditSession.IsDirty;
        _editStatus.IsVisible = changes && !string.IsNullOrEmpty(_editStatus.Text);
    }

    private Control? EditableDiff(string path, string before)
    {
        if (!EditSession.Editing) return null;
        if (!EditSession.Buffers.TryGetValue(path, out var buffer))
        {
            buffer = OpenEditBuffer?.Invoke(_conversationId, path, before);
            if (buffer is null) return Note("InspectorEditUnsupported");
            EditSession.Buffers.Add(path, buffer);
        }
        return new ChangesDiffEditView(buffer, _splitDiff, UpdateEditActions);
    }

    internal static string DiffUnavailableKey(ChangedFile change, UndoCopyState state) => state switch
    {
        UndoCopyState.CreatedFile => "InspectorDiffCreatedFile",
        UndoCopyState.PendingSnapshot => "InspectorDiffPendingSnapshot",
        UndoCopyState.MissingSnapshot => "InspectorDiffMissingSnapshot",
        UndoCopyState.EvictedOrSpent => change.Verdict == ChangeVerdict.Unchanged
            ? "InspectorDiffRefusedLine"
            : "InspectorDiffCopyGone",
        _ => "InspectorDiffUnreadable",
    };

    /// <summary>One diff, in whichever of the two layouts the header gear is set to. Both read the same text and
    /// both use the <c>inspector-diff-*</c> classes, so the theme and a check need not care which one is up.</summary>
    private Control BuildDiffBlock(string path, string diff, ChatFileSnapshot? snapshot = null)
    {
        using var syntax = new ChangesDiffSyntax(path, diff, snapshot);
        return _splitDiff ? BuildSplitDiffBlock(diff, syntax) : BuildUnifiedDiffBlock(diff, syntax);
    }

    private static ScrollViewer DiffViewport() => new()
    {
        // Keep horizontal scrolling for long source lines, but let this viewer measure the full diff height.
        // Vertical scrolling belongs to _changesScroll so several expanded files share one page scrollbar.
        AllowAutoHide = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        ClipToBounds = true,
    };

    /// <summary>Renders a unified diff with separate old/new line-number gutters and an explicit change marker.
    /// A horizontal scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.</summary>
    private static DiffSelectableTextBlock DiffCodeText(ScrollViewer viewer, string displayText, string codeText,
        IReadOnlyList<ChangesDiffSyntax.Span>? sections)
    {
        var text = Themed(new DiffSelectableTextBlock(viewer)
        {
            Background = Brushes.Transparent,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
        }, "Hub.TextSecondary");
        text.Bind(SelectableTextBlock.SelectionBrushProperty, new DynamicResourceExtension("Hub.CodeSelection"));
        if (displayText.Length == 0) displayText = " ";
        if (sections is null || sections.Count == 0 || codeText.Length == 0 || displayText.Length != codeText.Length)
        {
            text.Text = displayText;
            return text;
        }

        var syntaxRuns = new List<(Avalonia.Controls.Documents.Run Run, IBrush? Brush)>();
        text.ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var (run, brush) in syntaxRuns)
                run.Foreground = DarkSyntax.BrushForTheme(brush, text.ActualThemeVariant);
        };
        var cursor = 0;
        foreach (var section in sections)
        {
            var start = Math.Clamp(section.Start, cursor, codeText.Length);
            var end = Math.Clamp(section.Start + section.Length, start, codeText.Length);
            if (start > cursor) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(displayText[cursor..start]));
            if (end > start)
            {
                var run = new Avalonia.Controls.Documents.Run(displayText[start..end]);
                if (section.Brush is { } foreground)
                {
                    run.Foreground = DarkSyntax.BrushForTheme(foreground, text.ActualThemeVariant);
                    syntaxRuns.Add((run, foreground));
                }
                text.Inlines!.Add(run);
                cursor = end;
            }
        }
        if (cursor < displayText.Length) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(displayText[cursor..]));
        return text;
    }

    /// <summary>Renders a unified diff with separate old/new line-number gutters and an explicit change marker.
    /// A horizontal scroller rather than wrapping: code that wraps loses the column alignment a diff is read by.
    /// <para>Also the renderer the transcript folds a patch edit open with, which is why the gutters can be left
    /// out: a patch envelope anchors its hunks on context instead of positions, so numbers beside it would be
    /// invented here rather than read from anywhere.</para></summary>
    internal static Control BuildUnifiedDiffBlock(string diff, ChangesDiffSyntax syntax, bool numbered = true)
    {
        var lines = new StackPanel { Spacing = 0 };
        var viewer = DiffViewport();
        var oldLine = 0;
        var newLine = 0;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var lineClass = DiffLineClass(line);
            var sourceLine = lineClass == "diff-del" ? oldLine : newLine;
            var numbers = numbered ? DiffLineNumbers(line, lineClass, ref oldLine, ref newLine) : "";
            var block = new Border();
            block.Classes.Add("inspector-diff-line");
            if (lineClass.Length > 0) block.Classes.Add(lineClass);

            if (lineClass == "diff-hunk")
                (oldLine, newLine) = ParseHunkLineNumbers(line);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            if (numbered)
            {
                var gutter = Themed(new TextBlock
                {
                    Text = numbers,
                    FontFamily = ThemedFont("Hub.Font.Mono"),
                    FontSize = 10,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Classes = { "inspector-diff-gutter" },
                }, "Hub.TextTertiary");
                row.Children.Add(gutter);
            }

            var marker = Themed(new TextBlock
            {
                Text = lineClass == "diff-add" ? "+" : lineClass == "diff-del" ? "−" : " ",
                FontFamily = ThemedFont("Hub.Font.Mono"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "inspector-diff-marker" },
            }, lineClass == "diff-add" ? "Hub.Success" : lineClass == "diff-del" ? "Hub.Danger" : "Hub.TextTertiary");
            Grid.SetColumn(marker, 1);
            row.Children.Add(marker);

            var displayLine = lineClass is "diff-add" or "diff-del"
                ? line[1..]
                : line.StartsWith(' ') && numbered
                    ? line[1..]
                    : line;
            var codeText = line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' ')
                ? line[1..] : line;
            var text = DiffCodeText(viewer, displayLine, codeText,
                numbers.Length > 0 ? syntax.Line(lineClass == "diff-del", sourceLine) : null);
            text.Bind(SelectableTextBlock.SelectionBrushProperty, new DynamicResourceExtension("Hub.CodeSelection"));
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            block.Child = row;
            lines.Children.Add(block);
        }

        viewer.Content = new Border { Classes = { "inspector-diff" }, Child = lines };
        return viewer;
    }

    /// <summary>Half a row of the side-by-side layout: one line with the number it carries on its own side, and the
    /// class that decides its tint. A run of removals and the insertions that replace it share a row.</summary>
    private readonly record struct DiffHalf(string Number, string Text, string Kind);

    /// <summary>The same diff read as two facing columns — removed text on the left, added text on the right —
    /// which is what a wide column is for: a changed line and its replacement sit on one row instead of one above
    /// the other. Built from the diff text alone, not from before/after files: this pane is handed a diff and never
    /// a working tree, and a repository row has nothing else to read.
    ///
    /// All rows go into one grid rather than one grid per row, because equal columns are what makes the two sides
    /// line up — a grid per row would size each row's halves to that row's own text. A hunk's removals are held
    /// until the insertions that follow them have been read, so pair n of each lands on one row; a context line
    /// carries both numbers and appears in both halves, and a line with no counterpart leaves its half blank.</summary>
    private static Control BuildSplitDiffBlock(string diff, ChangesDiffSyntax syntax)
    {
        var viewer = DiffViewport();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var removed = new List<DiffHalf>();
        var added = new List<DiffHalf>();
        var oldLine = 0;
        var newLine = 0;
        var rowIndex = 0;

        void AddRow(Control left, Control? right = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(left, rowIndex);
            grid.Children.Add(left);
            if (right is not null)
            {
                Grid.SetRow(right, rowIndex);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }
            rowIndex++;
        }

        void FlushPair()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                AddRow(
                    i < removed.Count ? DiffHalfCell(viewer, removed[i], syntax) : BlankHalf(),
                    i < added.Count ? DiffHalfCell(viewer, added[i], syntax) : null);
            }
            removed.Clear();
            added.Clear();
        }

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var lineClass = DiffLineClass(line);
            if (lineClass == "diff-add")
            {
                added.Add(new DiffHalf($"{newLine++}", line[1..], lineClass));
                continue;
            }
            if (lineClass == "diff-del")
            {
                // git prints every removal before the insertion that replaces it, so a removal arriving after one
                // means the pair the reader is looking at is already complete.
                if (added.Count > 0) FlushPair();
                removed.Add(new DiffHalf($"{oldLine++}", line[1..], lineClass));
                continue;
            }
            FlushPair();
            if (lineClass == "diff-hunk") (oldLine, newLine) = ParseHunkLineNumbers(line);
            if (lineClass.Length == 0 && line.Length == 0) continue;
            if (line.StartsWith(' ') && oldLine > 0)
            {
                // Context: the same line of the file, so it belongs in both halves under both numbers.
                var text = line[1..];
                AddRow(
                    DiffHalfCell(viewer, new DiffHalf($"{oldLine++}", text, ""), syntax, old: true),
                    DiffHalfCell(viewer, new DiffHalf($"{newLine++}", text, ""), syntax));
                continue;
            }
            // Everything that is not a body line — the file's header, the hunk's, the "no newline" note — spans
            // both columns: it describes the pair rather than belonging to one side of it.
            var band = DiffHalfCell(viewer, new DiffHalf("", line, lineClass));
            Grid.SetColumnSpan(band, 2);
            AddRow(band);
        }
        FlushPair();

        viewer.Content = new Border { Classes = { "inspector-diff" }, Child = grid };
        return viewer;
    }

    private static Border DiffHalfCell(ScrollViewer viewer, DiffHalf half, ChangesDiffSyntax? syntax = null, bool old = false)
    {
        var block = new Border();
        block.Classes.Add("inspector-diff-line");
        if (half.Kind.Length > 0) block.Classes.Add(half.Kind);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var gutter = Themed(new TextBlock
        {
            Text = half.Number,
            MinWidth = 30,
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 10,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-diff-gutter" },
        }, "Hub.TextTertiary");
        row.Children.Add(gutter);

        var marker = Themed(new TextBlock
        {
            Text = half.Kind == "diff-add" ? "+" : half.Kind == "diff-del" ? "−" : " ",
            FontFamily = ThemedFont("Hub.Font.Mono"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "inspector-diff-marker" },
        }, half.Kind == "diff-add" ? "Hub.Success" : half.Kind == "diff-del" ? "Hub.Danger" : "Hub.TextTertiary");
        Grid.SetColumn(marker, 1);
        row.Children.Add(marker);

        var text = DiffCodeText(viewer, half.Text, half.Text,
            int.TryParse(half.Number, out var number) ? syntax?.Line(old || half.Kind == "diff-del", number) : null);
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        block.Child = row;
        return block;
    }

    /// <summary>The half of a row whose side has no line: the removal nothing replaced, or the insertion that
    /// replaced nothing. It keeps the row's plate and height, so the facing line does not look like a short line
    /// and the two columns keep their rhythm.</summary>
    private static Border BlankHalf()
    {
        var block = new Border();
        block.Classes.Add("inspector-diff-line");
        block.Child = new TextBlock { Text = " " };
        return block;
    }

    /// <summary>Extends a line selection while its pointer is held at the horizontal edge of the diff viewport.
    /// ScrollViewer does not move a selection endpoint when its content scrolls under a stationary pointer, so the
    /// timer updates both the offset and the text hit-test while leaving ordinary pointer selection to the base.</summary>
    private sealed class DiffSelectableTextBlock(ScrollViewer viewer) : SelectableTextBlock
    {
        // A derived control otherwise looks up a theme under its own type, losing the base selection brush,
        // I-beam cursor and copy flyout. Keep the standard text-selection surface while adding edge scrolling.
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        private const double EdgeSize = 24;
        private DispatcherTimer? _autoScrollTimer;
        private IPointer? _dragPointer;
        private Point _pointerInViewer;
        private int _selectionAnchor;
        private int _direction;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragPointer = e.Pointer;
            _selectionAnchor = SelectionStart;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragPointer != e.Pointer || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _pointerInViewer = e.GetPosition(viewer);
            var width = viewer.Bounds.Width;
            _direction = _pointerInViewer.X < EdgeSize ? -1
                : _pointerInViewer.X > width - EdgeSize ? 1
                : 0;
            if (_direction == 0 || viewer.Extent.Width <= viewer.Viewport.Width)
            {
                _autoScrollTimer?.Stop();
                return;
            }

            _autoScrollTimer ??= CreateAutoScrollTimer();
            if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            StopAutoScroll();
            _dragPointer = null;
            base.OnPointerReleased(e);
        }

        private DispatcherTimer CreateAutoScrollTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => ScrollSelectionAtEdge();
            return timer;
        }

        private void ScrollSelectionAtEdge()
        {
            if (_dragPointer is null || _direction == 0 || !IsPointerCaptured())
            {
                StopAutoScroll();
                return;
            }

            var maximum = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
            var next = Math.Clamp(viewer.Offset.X + _direction * 12, 0, maximum);
            if (Math.Abs(next - viewer.Offset.X) < 0.1)
            {
                StopAutoScroll();
                return;
            }

            viewer.Offset = new Vector(next, viewer.Offset.Y);
            var edgeX = _direction < 0 ? EdgeSize : viewer.Bounds.Width - EdgeSize;
            if (viewer.TranslatePoint(new Point(edgeX, _pointerInViewer.Y), this) is { } point)
            {
                var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
                SelectionStart = _selectionAnchor;
                SelectionEnd = Math.Clamp(hit.TextPosition, 0, TextForCheck(this).Length);
            }
        }

        private bool IsPointerCaptured() => _dragPointer?.Captured == this;

        private void StopAutoScroll()
        {
            _direction = 0;
            _autoScrollTimer?.Stop();
        }
    }

    private static string DiffLineNumbers(string line, string lineClass, ref int oldLine, ref int newLine)
    {
        if (lineClass == "diff-hunk") return "";
        if (lineClass == "diff-header") return "";
        if (lineClass == "diff-add") return $"     {newLine++,4}";
        if (lineClass == "diff-del") return $"{oldLine++,4}     ";
        if (line.StartsWith(' ') && oldLine > 0 && newLine > 0)
            return $"{oldLine++,4} {newLine++,4}";
        return "";
    }

    private static (int Old, int New) ParseHunkLineNumbers(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, "^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)(?:,\\d+)? @@");
        return match.Success
            && int.TryParse(match.Groups[1].Value, out var oldLine)
            && int.TryParse(match.Groups[2].Value, out var newLine)
            ? (oldLine, newLine)
            : (0, 0);
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
