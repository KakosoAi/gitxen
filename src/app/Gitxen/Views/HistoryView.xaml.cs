// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  The commit graph: history with its lanes and ref badges, the selected commit's details, and the
///  diff of whichever file that commit touched.
/// </summary>
/// <remarks>
///  Every operation that acts on a commit hangs off the row's own context menu. In the previous
///  layout these lived in a toolbar flyout labelled "History…", which meant picking a commit and then
///  hunting for the verb in an unrelated corner of the window.
/// </remarks>
public sealed partial class HistoryView : RepositoryPage
{
    /// <summary>
    ///  What was selected before the most recent right-click, which is the other side of a comparison.
    /// </summary>
    private CommitRowViewModel? _previousSelection;

    /// <summary>The tab this page is subscribed to, so the subscription can be dropped when it changes.</summary>
    private RepositoryTabViewModel? _watchedTab;

    /// <summary>Set once the viewer has been given its opening width, so that sizing is not saved as a choice.</summary>
    private bool _viewerSized;

    private bool _chipsQueued;
    private bool _summaryQueued;

    public HistoryView()
    {
        InitializeComponent();

        // See ChangesView: the dragged width is an option shared by every tab, not per-repository state.
        DetailsPane.Width = AppOptions.HistoryDetailsWidth;
        DetailsPane.SizeChanged += (_, e) => AppOptions.HistoryDetailsWidth = e.NewSize.Width;

        // Until it is first dragged, the viewer takes most of the list's width and leaves the lane
        // graph and the hashes showing on its left.
        FileViewer.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && Tab?.IsFileViewerOpen == true && _viewerSized)
            {
                AppOptions.HistoryViewerWidth = e.NewSize.Width;
            }
        };

        SidePane.Width = AppOptions.HistorySidePaneWidth;
        SidePane.SizeChanged += (_, e) =>
        {
            // A collapsed pane reports zero, which is not a width anyone chose.
            if (e.NewSize.Width > 0)
            {
                AppOptions.HistorySidePaneWidth = e.NewSize.Width;
            }
        };
    }

    protected override void OnTabChanged()
    {
        if (_watchedTab is not null)
        {
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;
            _watchedTab.CommitRevealRequested -= OnCommitRevealRequested;
            _watchedTab.Commits.CollectionChanged -= OnCommitsChanged;
            _watchedTab.ChangedFiles.CollectionChanged -= OnChangedFilesChanged;
        }

        _watchedTab = Tab;

        if (Tab is RepositoryTabViewModel tab)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            tab.CommitRevealRequested += OnCommitRevealRequested;
            tab.Commits.CollectionChanged += OnCommitsChanged;
            tab.ChangedFiles.CollectionChanged += OnChangedFilesChanged;
            QueueAuthorChips();
            QueueFileSummary();

            // Marks are only refreshed on a timer while this is the page on screen.
            tab.GitHub.CommitChecks.IsPageVisible = () => Visibility == Visibility.Visible && IsLoaded;
        }

        SyncSidePaneRows();
    }

    /// <summary>History is the page a repository opens on, so this is where the CI marks are first asked for.</summary>
    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.GitHub.CommitChecks.RefreshAsync();
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepositoryTabViewModel.Query))
        {
            QueueAuthorChips();
        }

        if (e.PropertyName is nameof(RepositoryTabViewModel.IsBlameOpen) or nameof(RepositoryTabViewModel.IsFileHistoryOpen))
        {
            SyncSidePaneRows();
        }

        if (e.PropertyName == nameof(RepositoryTabViewModel.IsFileViewerOpen) && Tab?.IsFileViewerOpen == true)
        {
            SizeViewer();
        }
    }

    // ---- Author chips -----------------------------------------------------------------------------

    private void OnCommitsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        QueueAuthorChips();

    /// <summary>History loads in batches, so the chips are rebuilt once the batches have stopped arriving.</summary>
    private void QueueAuthorChips()
    {
        if (_chipsQueued)
        {
            return;
        }

        _chipsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _chipsQueued = false;
            BuildAuthorChips();
        });
    }

    /// <summary>
    ///  The most active authors among the loaded commits, one chip each. While one is chosen the set is
    ///  left as it was — the list is then only theirs, and rebuilding from it would leave no way across
    ///  to someone else without clearing first.
    /// </summary>
    private void BuildAuthorChips()
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        string chosen = tab.Query.Author;

        if (chosen.Length > 0 && AuthorChips.Children.Count > 0)
        {
            foreach (ToggleButton existing in AuthorChips.Children.OfType<ToggleButton>())
            {
                existing.IsChecked = string.Equals(existing.Tag as string, chosen, StringComparison.OrdinalIgnoreCase);
            }

            return;
        }

        AuthorChips.Children.Clear();

        var top = tab.Commits
            .Where(commit => !commit.IsWorkingDirectory && commit.Author.Length > 0)
            .GroupBy(commit => commit.Author)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(author => author.Count)
            .Take(5)
            .ToList();

        // One author is not a choice.
        if (top.Count < 2)
        {
            return;
        }

        foreach ((string name, int count) in top)
        {
            Grid avatar = new() { Width = 18, Height = 18 };
            avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = AuthorStyle.FillFor(name) });
            avatar.Children.Add(new TextBlock
            {
                Text = Initials(name),
                FontSize = 8,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AuthorStyle.InitialsBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(avatar);
            content.Children.Add(new TextBlock
            {
                Text = FirstName(name),
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            });
            content.Children.Add(new TextBlock
            {
                Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                Opacity = 0.55,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            ToggleButton chip = new()
            {
                Content = content,
                Tag = name,
                Padding = new Thickness(6, 3, 10, 3),
                CornerRadius = new CornerRadius(14),
                IsChecked = string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase)
            };
            ToolTipService.SetToolTip(chip, $"Only {name}'s commits");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"{name}, {count} commits");
            chip.Click += AuthorChip_Click;
            AuthorChips.Children.Add(chip);
        }
    }

    private void AuthorChip_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab && sender is ToggleButton { Tag: string name } chip)
        {
            // Chosen again, it comes off: the filter is the chip, and the chip is the way back.
            tab.Query = tab.Query with { Author = chip.IsChecked == true ? name : "" };
        }
    }

    private static string Initials(string author)
    {
        string[] parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..1].ToUpperInvariant() : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private static string FirstName(string author) =>
        author.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? author;

    // ---- What the selected commit did, in proportion ---------------------------------------------------

    private void OnChangedFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        QueueFileSummary();

    private void QueueFileSummary()
    {
        if (_summaryQueued)
        {
            return;
        }

        _summaryQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _summaryQueued = false;
            BuildFileSummary();
        });
    }

    /// <summary>A line of counts and a bar divided by kind of change, in the colours of the file tiles.</summary>
    private void BuildFileSummary()
    {
        FileSummaryBar.Children.Clear();
        FileSummaryBar.ColumnDefinitions.Clear();

        IReadOnlyList<ChangedFileViewModel> files = Tab?.ChangedFiles.ToList() ?? [];

        if (files.Count == 0)
        {
            FileSummaryText.Text = "click one to see its diff";
            FileSummaryBar.Visibility = Visibility.Collapsed;
            return;
        }

        (FileChangeKind Kind, string Label, string Brush)[] kinds =
        [
            (FileChangeKind.Added, "added", "SystemFillColorSuccessBrush"),
            (FileChangeKind.Modified, "modified", "SystemFillColorAttentionBrush"),
            (FileChangeKind.Deleted, "deleted", "SystemFillColorCriticalBrush"),
            (FileChangeKind.Renamed, "renamed", "SystemFillColorCautionBrush"),
        ];

        List<string> parts = [files.Count == 1 ? "1 file" : $"{files.Count} files"];

        foreach ((FileChangeKind kind, string label, string brush) in kinds)
        {
            int count = files.Count(file => file.Kind == kind);

            if (count == 0)
            {
                continue;
            }

            parts.Add($"{count} {label}");

            FileSummaryBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            Border segment = new() { Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush] };
            Grid.SetColumn(segment, FileSummaryBar.ColumnDefinitions.Count - 1);
            ToolTipService.SetToolTip(segment, $"{count} {label}");
            FileSummaryBar.Children.Add(segment);
        }

        FileSummaryText.Text = string.Join("  ·  ", parts);
        FileSummaryBar.Visibility = Visibility.Visible;
    }

    private void CopySelectedHash_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.SelectedCommit is { IsWorkingDirectory: false } commit)
        {
            DataPackage package = new();
            package.SetText(commit.FullHash);
            Clipboard.SetContent(package);
        }
    }

    /// <summary>
    ///  Gives the side column's height to whichever panels are open: all of it to one, half each to two.
    /// </summary>
    /// <remarks>
    ///  In code because a row's height is a GridLength, which x:Bind cannot produce from a bool, and a
    ///  star row holding a collapsed panel would still take its half.
    /// </remarks>
    private void SyncSidePaneRows()
    {
        bool blame = Tab?.IsBlameOpen == true;
        bool history = Tab?.IsFileHistoryOpen == true;

        BlameRow.Height = blame ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FileHistoryRow.Height = history ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>
    ///  The width the viewer opens at: the one last dragged to, or else three quarters of the list,
    ///  and never so wide that the list disappears behind it.
    /// </summary>
    private void SizeViewer()
    {
        double available = CommitList.ActualWidth;
        double wanted = AppOptions.HistoryViewerWidth > 0 ? AppOptions.HistoryViewerWidth : available * 0.75;

        _viewerSized = false;
        FileViewer.Width = Math.Max(480, Math.Min(wanted, Math.Max(480, available - 120)));
        _viewerSized = true;
    }

    private void ChangedFile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab && e.ClickedItem is ChangedFileViewModel file)
        {
            tab.OpenFileViewer(file);
        }
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Only taken when there is something to close, so Esc still leaves Zen when the viewer is shut.
        args.Handled = Tab?.IsFileViewerOpen == true;
        CloseViewer();
    }

    private void CloseViewer()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            tab.IsFileViewerOpen = false;
        }

        CommitList.Focus(FocusState.Programmatic);
    }

    /// <summary>Brings a commit chosen from Blame or File history into view in the list.</summary>
    private void OnCommitRevealRequested(object? sender, CommitRowViewModel row) => CommitList.ScrollIntoView(row);

    /// <summary>Opens what GitHub Actions ran for the commit: the run, or the commit's checks when there are several.</summary>
    private async void CommitChecks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommitRowViewModel { Checks: CommitChecks checks } row }
            && Tab?.Host is HostedRepository host
            && Uri.TryCreate(checks.Url(host, row.FullHash), UriKind.Absolute, out Uri? url))
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private async void CheckRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string address } && Uri.TryCreate(address, UriKind.Absolute, out Uri? url))
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    /// <summary>
    ///  Right-clicking a row selects it before its menu opens, so the operations act on the commit
    ///  actually clicked rather than on whatever was selected beforehand.
    /// </summary>
    private void CommitRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab
            && sender is FrameworkElement { DataContext: CommitRowViewModel commit })
        {
            // Remembered before the selection moves, so "compare with the selected commit" has
            // something to mean once the right-click has changed what is selected.
            _previousSelection = tab.SelectedCommit;
            tab.SelectedCommit = commit;
        }
    }

    private void ScopeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        RevisionScope scope = ReferenceEquals(sender.SelectedItem, AllBranchesScope)
            ? RevisionScope.AllBranches
            : RevisionScope.CurrentBranch;

        // Assigning Query reloads, so only assign when it actually differs — the selector raises this
        // while restoring its own selection too.
        if (tab.Query.Scope != scope)
        {
            tab.Query = tab.Query with { Scope = scope };
        }
    }

    /// <summary>
    ///  Collects the search terms git will apply while walking history.
    /// </summary>
    /// <remarks>
    ///  Deliberately separate from the filter box. The filter narrows the rows already loaded, which
    ///  is instant but can only ever match the page you have; these terms are passed to
    ///  <c>git log</c>, so they find commits that were never read.
    /// </remarks>
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        TextBox message = new() { Header = "Message contains", Text = tab.Query.MessageContains };
        TextBox author = new() { Header = "Author", Text = tab.Query.Author };
        TextBox content = new() { Header = "Code (searches what each commit changed, as a regular expression)", Text = tab.Query.ContainingText };
        RadioButtons contentMode = new()
        {
            ItemsSource = new[]
            {
                "Added or removed — where it appeared or disappeared",
                "Any changed line matching — every edit that touched it"
            },
            SelectedIndex = tab.Query.MatchChangedLines ? 1 : 0,
            Margin = new Thickness(0, -6, 0, 0)
        };
        TextBox path = new() { Header = "Limit to path", Text = tab.Query.Path };

        StackPanel panel = new() { Spacing = 12, Width = 420 };
        panel.Children.Add(message);
        panel.Children.Add(author);
        panel.Children.Add(content);
        panel.Children.Add(contentMode);
        panel.Children.Add(path);
        panel.Children.Add(new TextBlock
        {
            Text = "Leave a field empty to ignore it. Searching re-reads history from git, so it may "
                + "take a moment on a large repository.",
            Style = (Style)Application.Current.Resources["PageSubtitleStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        ContentDialog dialog = new()
        {
            Title = "Search history",
            Content = panel,
            PrimaryButtonText = "Search",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            tab.Query = tab.Query with
            {
                MessageContains = message.Text.Trim(),
                Author = author.Text.Trim(),
                ContainingText = content.Text.Trim(),
                MatchChangedLines = contentMode.SelectedIndex == 1,
                Path = path.Text.Trim()
            };
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            // Scope is a view preference rather than part of the search, so it survives clearing.
            tab.Query = new RevisionQuery(tab.Query.Scope);
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.LoadMoreAsync();
        }
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        if (await PromptAsync("Create branch", "Branch name", "Create") is string name && name.Length > 0)
        {
            await ReportAsync(tab => tab.CreateBranchAsync(name, checkout: true));
        }
    }

    private async void CreateTag_Click(object sender, RoutedEventArgs e)
    {
        if (await PromptAsync("Create tag", "Tag name", "Next") is not string name || name.Length == 0)
        {
            return;
        }

        // An empty message means a lightweight tag, which is a legitimate choice rather than a
        // cancellation — so only a cancelled second prompt stops here.
        if (await PromptAsync("Create tag", "Annotation message (leave empty for a lightweight tag)", "Create") is string message)
        {
            await ReportAsync(tab => tab.CreateTagAsync(name, message));
        }
    }

    private async void CheckoutCommit_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.SelectedCommit is not CommitRowViewModel { IsWorkingDirectory: false } commit)
        {
            return;
        }

        if (await ConfirmAsync(
            "Check out this commit?",
            $"This leaves HEAD detached at {commit.ShortHash}. You will not be on a branch until you check one out again.",
            "Check out"))
        {
            await ReportAsync(tab => tab.CheckoutDetachedAsync(commit.FullHash));
        }
    }

    /// <summary>
    ///  Compares the right-clicked commit with the one that was already selected.
    /// </summary>
    /// <remarks>
    ///  Right-clicking selects the row first, so by the time this runs the clicked commit is the
    ///  selection and the previously selected one is what to compare against. That earlier commit is
    ///  captured before the menu opens, in <see cref="CommitRow_RightTapped"/>.
    /// </remarks>
    private void CompareWith_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        if (_previousSelection is null || ReferenceEquals(_previousSelection, tab.SelectedCommit))
        {
            tab.ReportInformation(
                "Nothing to compare with",
                "Select one commit, then right-click a second one and choose Compare.");

            return;
        }

        if (_previousSelection.IsWorkingDirectory || tab.SelectedCommit?.IsWorkingDirectory == true)
        {
            tab.ReportInformation(
                "Cannot compare that",
                "The working directory row is not a commit. Compare two commits instead.");

            return;
        }

        tab.CompareTarget = _previousSelection;
    }

    private void StopComparing_Click(object sender, RoutedEventArgs e) => Tab?.StopComparing();

    /// <summary>A plain-language explanation of the commit, written by Claude, in a dialog.</summary>
    private async void ExplainCommit_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab
            || ((sender as FrameworkElement)?.DataContext as CommitRowViewModel ?? tab.SelectedCommit) is not { IsWorkingDirectory: false, FullHash.Length: > 0 } commit)
        {
            return;
        }

        TextBlock text = new() { Text = "Asking Claude…", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        ContentDialog dialog = new()
        {
            Title = $"{commit.ShortHash}  {commit.Subject}",
            Content = new ScrollViewer { Content = text, MaxHeight = 460, Width = 560 },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };

        Task<ContentDialogResult> shown = dialog.ShowAsync().AsTask();

        try
        {
            text.Text = await tab.ExplainCommitAsync(commit.FullHash);
        }
        catch (AiException ex)
        {
            text.Text = ex.Message;
        }

        await shown;
    }

    private async void CherryPick_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.CherryPickAsync());

    private async void Revert_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.RevertAsync());

    /// <summary>
    ///  The interactive rebase editor for the commits after this one. Problems — uncommitted changes, a
    ///  merge in the way, a rebase already running — are said before anything opens.
    /// </summary>
    private async void EditCommits_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab
            || ((sender as FrameworkElement)?.DataContext as CommitRowViewModel ?? tab.SelectedCommit) is not { IsWorkingDirectory: false, FullHash.Length: > 0 } commit)
        {
            return;
        }

        RebasePlan plan = await tab.PlanRebaseAsync(commit.FullHash);

        if (plan.Problem.Length > 0)
        {
            tab.ReportInformation("Cannot edit these commits", plan.Problem);
            return;
        }

        if (await InteractiveRebasePanel.ShowAsync(XamlRoot, plan) is IReadOnlyList<RebaseStep> steps)
        {
            tab.Report(await tab.RewriteCommitsAsync(plan.BaseId, steps));
        }
    }

    private async void Rebase_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.SelectedCommit is not CommitRowViewModel { IsWorkingDirectory: false } commit)
        {
            return;
        }

        if (await ConfirmAsync(
            "Rebase onto this commit?",
            $"Replays the current branch on top of {commit.ShortHash}. Conflicts will pause the rebase.",
            "Rebase"))
        {
            await ReportAsync(tab => tab.RebaseAsync(commit.FullHash));
        }
    }

    private async void ResetSoft_Click(object sender, RoutedEventArgs e) => await ResetAsync(GitResetMode.Soft);

    private async void ResetMixed_Click(object sender, RoutedEventArgs e) => await ResetAsync(GitResetMode.Mixed);

    private async void ResetHard_Click(object sender, RoutedEventArgs e) => await ResetAsync(GitResetMode.Hard);

    private async Task ResetAsync(GitResetMode mode)
    {
        // Hard reset throws away uncommitted work, so it gets a confirmation the others don't need.
        if (mode == GitResetMode.Hard && !await ConfirmAsync(
            "Hard reset?",
            "This discards all uncommitted changes in the working directory. It cannot be undone.",
            "Reset --hard"))
        {
            return;
        }

        await ReportAsync(tab => tab.ResetToSelectedAsync(mode));
    }

    private async void BisectStart_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.BisectAsync("start"));

    private async void BisectGood_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.BisectAtSelectedAsync("good"));

    private async void BisectBad_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.BisectAtSelectedAsync("bad"));

    private async void BisectReset_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.BisectAsync("reset"));

    /// <summary>Opens the selected commit on the hosting service, where the review lives.</summary>
    private async void OpenCommitOnHost_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        if (tab.Host is not HostedRepository host)
        {
            tab.ReportInformation(
                "No hosting service",
                "This repository has no remote that resolves to a browsable address.");

            return;
        }

        if (tab.SelectedCommit is not CommitRowViewModel { IsWorkingDirectory: false } commit)
        {
            return;
        }

        await Windows.System.Launcher.LaunchUriAsync(new Uri(host.CommitUrl(commit.FullHash)));
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(Tab?.SelectedCommit?.FullHash, "Full hash copied.");

    private void CopySubject_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(Tab?.SelectedCommit?.Subject, "Subject copied.");

    private void CopyToClipboard(string? text, string confirmation)
    {
        if (Tab is not RepositoryTabViewModel tab || string.IsNullOrEmpty(text))
        {
            return;
        }

        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);

        tab.ReportInformation("Copied", confirmation);
    }
}
