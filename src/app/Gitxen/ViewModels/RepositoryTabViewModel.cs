// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Gitxen.Git;
using Gitxen.Graph;
using Gitxen.Models;
using Gitxen.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.ViewModels;

/// <summary>
///  One open repository. Each tab owns its own state so tabs stay fully independent, mirroring how
///  the WinForms app gives every tab its own IGitUICommands/GitModule.
/// </summary>
public sealed class RepositoryTabViewModel : ShellTab
{
    /// <summary>
    ///  How many commits to read per page, and how many diff lines to render. Configurable in
    ///  Settings; capped at all because this repository alone has ~17,000 commits, which is more
    ///  than an unvirtualized bound collection should hold.
    /// </summary>
    private static int MaxCommits => AppOptions.MaxCommits;

    /// <summary>
    ///  Every loaded commit. <see cref="Commits"/> is the filtered projection actually bound to the
    ///  list, so filtering never discards rows we would have to re-read from git.
    /// </summary>
    private readonly List<CommitRowViewModel> _allCommits = [];

    /// <summary>
    ///  How many of <see cref="_allCommits"/> are real commits, excluding the working-directory row.
    /// </summary>
    /// <remarks>
    ///  Kept as a counter rather than recounted. It is read once per commit while a page streams in,
    ///  and counting the list each time made loading a page quadratic — roughly two million predicate
    ///  calls on the UI thread for a 2,000-commit page.
    /// </remarks>
    private int _loadedCommitCount;

    private readonly RepositoryLoader _loader;
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>Rebuilt from scratch on a full reload; kept across pages so lanes stay continuous.</summary>
    private CommitGraphBuilder _graphBuilder = new();

    /// <summary>
    ///  Refs keyed by full commit hash, read once per reload so attaching badges to a streamed row is
    ///  a dictionary lookup rather than a git call.
    /// </summary>
    private IReadOnlyDictionary<string, List<RefBadge>> _refsByCommit = new Dictionary<string, List<RefBadge>>();

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _changedFilesCts;
    private UiMode _mode;
    private string _branch = "";
    private string _upstream = "";
    private int _aheadCount;
    private int _behindCount;
    private int _pendingCount;
    private RepositoryOverview? _overview;
    private SpeedSettings? _speed;
    private bool _speedChecked;
    private bool _isSpeedSuggestionOpen;
    private Func<GitOperationResult>? _pendingUndo;
    private CancellationTokenSource? _transferCancel;
    private bool _isTransferring;
    private string _transferTitle = "";
    private string _transferPhase = "";
    private string _transferDetail = "";
    private double _transferPercent;
    private bool _transferIndeterminate;
    private string _status = "";
    private string _resultTitle = "";
    private string _resultMessage = "";
    private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;
    private bool _isResultOpen;
    private string _filter = "";
    private RevisionQuery _query = RevisionQuery.Default;
    private bool _isLoading;
    private bool _hasMoreCommits;
    private bool _isBusy;
    private string? _selectedBranch;
    private bool _suppressBranchCheckout;
    private RepositoryOperation _operation = RepositoryOperation.None;
    private bool _needsIdentity;
    private HostedRepository? _host;
    private CommitRowViewModel? _selectedCommit;
    private CommitRowViewModel? _compareTarget;
    private ChangedFileViewModel? _selectedChangedFile;
    private bool _isFileViewerOpen;
    private bool _isBlameOpen = AppOptions.HistoryBlameOpen;
    private bool _isFileHistoryOpen = AppOptions.HistoryFileHistoryOpen;

    /// <summary>A file to keep selected when the changed-files list is next rebuilt; see <see cref="RevealCommit"/>.</summary>
    private string? _preferredFileName;

    public RepositoryTabViewModel(string workingDir, UiMode mode)
    {
        _loader = new RepositoryLoader(workingDir);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _mode = mode;
        WorkingDir = workingDir;
        Title = GetRepositoryName(workingDir);
        Changes = new WorkingDirectoryViewModel(_loader, LoadAsync);
        GitHub = new GitHubRepositoryViewModel(this);
        Blame = new BlameViewModel(this);
        FileHistory = new FileHistoryViewModel(this);
        AppOptions.TabDetailsChanged += OnTabDetailsChanged;
        AppOptions.GraphChanged += OnGraphChanged;

        // What makes reusing a listing safe. The repository can be changed from a terminal or
        // another client while this tab is open, so anything read from refs or config is only
        // reusable for as long as nothing under .git has moved.
        _watcher = new RepositoryWatcher(workingDir);
        _watcher.Changed += (_, _) => _dispatcherQueue.TryEnqueue(InvalidateListings);
    }

    private readonly RepositoryWatcher _watcher;

    /// <summary>
    ///  Which listings have been read since the repository last changed.
    /// </summary>
    /// <remarks>
    ///  Every section re-read itself on every visit, so walking Branches, History, Branches ran
    ///  for-each-ref twice for an answer that had not moved. Keyed by the loading method's own name,
    ///  and emptied wholesale whenever the watcher reports a change — invalidating precisely would
    ///  mean mapping each path under .git to the listings it feeds, which is far more ways to be
    ///  subtly wrong than re-reading a handful of cheap commands is worth.
    /// </remarks>
    private readonly HashSet<string> _freshListings = new(StringComparer.Ordinal);

    /// <summary>
    ///  Forgets every listing, so the next visit reads from git again.
    /// </summary>
    /// <remarks>
    ///  Called when the watcher reports a change, after any operation this app runs, and by the
    ///  Refresh button on each section — which has to mean "read it again", not "show me what you
    ///  already had".
    /// </remarks>
    public void InvalidateListings() => _freshListings.Clear();

    public string WorkingDir { get; }

    public override string Title { get; }

    /// <summary>The working directory, which is what distinguishes two tabs with the same folder name.</summary>
    public override string Description => WorkingDir;

    public override bool IsClosable => true;

    public override bool IsHome => false;

    /// <summary>
    ///  Navigation tag of the section last shown, so the repository reopens where it was left.
    /// </summary>
    /// <remarks>
    ///  Assigned by the view rather than derived here: the tab has no notion of pages, and which
    ///  section is showing is a fact about the view, not about the repository.
    /// </remarks>
    public string LastSection { get; set; } = "";

    /// <summary>The commits currently shown — <see cref="_allCommits"/> passed through the filter.</summary>
    public ObservableCollection<CommitRowViewModel> Commits { get; } = [];

    /// <summary>Files touched by the selected commit, shown alongside the history.</summary>
    public ObservableCollection<ChangedFileViewModel> ChangedFiles { get; } = [];

    /// <summary>The History page's diff panel. The Changes page owns a separate one.</summary>
    public DiffViewModel HistoryDiff { get; } = new();

    /// <summary>Staging and committing, which the Changes page drives.</summary>
    public WorkingDirectoryViewModel Changes { get; }

    /// <summary>Pull requests, issues and Actions, for a repository on GitHub.</summary>
    public GitHubRepositoryViewModel GitHub { get; }

    /// <summary>Blame for the file selected on the History page; read when that pane is showing.</summary>
    public BlameViewModel Blame { get; }

    /// <summary>The commits that changed the file selected on the History page; read when that pane is showing.</summary>
    public FileHistoryViewModel FileHistory { get; }

    // ---- Repository object pages ----------------------------------------------------------------
    // Each page loads its own collection on first navigation and on refresh, rather than every tab
    // paying for every listing up front.

    public ObservableCollection<BranchInfo> BranchDetails { get; } = [];

    public ObservableCollection<RemoteInfo> Remotes { get; } = [];

    public ObservableCollection<TagInfo> Tags { get; } = [];

    public ObservableCollection<StashInfo> Stashes { get; } = [];

    public ObservableCollection<SubmoduleInfo> Submodules { get; } = [];

    public ObservableCollection<WorktreeInfo> Worktrees { get; } = [];

    public ObservableCollection<RemoteBranchInfo> RemoteBranches { get; } = [];

    public ObservableCollection<ReflogEntry> Reflog { get; } = [];

    /// <summary>Files with unresolved merge conflicts, and the shape of each conflict.</summary>
    public ObservableCollection<ConflictedFile> Conflicts { get; } = [];

    /// <summary>Drives the Conflicts navigation badge, so the count is visible without going there.</summary>
    public int ConflictCount => Conflicts.Count;

    public Visibility ConflictVisibility => Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<GitConfigEntry> LocalConfig { get; } = [];

    public ObservableCollection<GitConfigEntry> GlobalConfig { get; } = [];

    /// <summary>The paused merge, rebase, cherry-pick, revert or bisect, if there is one.</summary>
    public RepositoryOperation Operation
    {
        get => _operation;
        private set
        {
            if (SetProperty(ref _operation, value))
            {
                OnPropertyChanged(nameof(HasOperation));
            }
        }
    }

    public bool HasOperation => Operation.IsInProgress;

    /// <summary>
    ///  Set when no committer identity is configured, which makes every commit fail with an error
    ///  that does not say where to fix it.
    /// </summary>
    public bool NeedsIdentity
    {
        get => _needsIdentity;
        private set
        {
            if (SetProperty(ref _needsIdentity, value))
            {
                OnPropertyChanged(nameof(IdentityWarningVisibility));
            }
        }
    }

    public Visibility IdentityWarningVisibility => NeedsIdentity ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///  Where this repository lives online, from its remote URL, or null when it has no remote or the
    ///  remote is not a recognisable hosting service.
    /// </summary>
    public HostedRepository? Host
    {
        get => _host;
        private set
        {
            if (SetProperty(ref _host, value))
            {
                GitHub.Reset();
                OnPropertyChanged(nameof(HasHost));
                OnPropertyChanged(nameof(HostVisibility));
                OnPropertyChanged(nameof(HostName));
            }
        }
    }

    public bool HasHost => Host is not null;

    /// <summary>The hosting actions are hidden entirely rather than shown disabled when there is no remote.</summary>
    public Visibility HostVisibility => HasHost ? Visibility.Visible : Visibility.Collapsed;

    public string HostName => Host is null ? "" : $"{Host.Owner}/{Host.Name}";

    /// <summary>Local branch names, for the pickers that just need a name.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>
    ///  Width of the graph column. Duplicated from the row view model because the column header
    ///  spacer binds against this tab, not against a row.
    /// </summary>
    public double GraphColumnWidth => CommitGraphBuilder.ColumnWidth;

    /// <summary>
    ///  Free-text filter over the commits already loaded — an instant narrowing of what is on screen,
    ///  not a search of the repository. <see cref="Query"/> is what asks git to search.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    ///  What git is asked to walk: the scope, and any message/author/content/path search.
    /// </summary>
    /// <remarks>
    ///  Assigning this reloads, because these are filters git applies while walking history — unlike
    ///  <see cref="Filter"/>, they can find commits that were never loaded.
    /// </remarks>
    public RevisionQuery Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                OnPropertyChanged(nameof(IsShowingAllBranches));
                OnPropertyChanged(nameof(QueryVisibility));
                OnPropertyChanged(nameof(QueryDescription));
                _ = LoadAsync();
            }
        }
    }

    /// <summary>
    ///  Sets the scope without triggering a reload, for restoring a session.
    /// </summary>
    /// <remarks>
    ///  The Query setter reloads, which is right when the user changes the scope and wrong when the
    ///  caller is about to load anyway — that would read the whole history twice on every start-up.
    /// </remarks>
    public void SetInitialScope(RevisionScope scope)
    {
        _query = _query with { Scope = scope };
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(IsShowingAllBranches));
    }

    public bool IsShowingAllBranches => Query.Scope == RevisionScope.AllBranches;

    /// <summary>Shown while git-side filters are narrowing the log, so an empty list is explicable.</summary>
    public Visibility QueryVisibility => Query.HasFilters ? Visibility.Visible : Visibility.Collapsed;

    public string QueryDescription
    {
        get
        {
            List<string> parts = [];

            if (!string.IsNullOrWhiteSpace(Query.MessageContains))
            {
                parts.Add($"message contains \"{Query.MessageContains}\"");
            }

            if (!string.IsNullOrWhiteSpace(Query.Author))
            {
                parts.Add($"author \"{Query.Author}\"");
            }

            if (!string.IsNullOrWhiteSpace(Query.ContainingText))
            {
                parts.Add(Query.MatchChangedLines ? $"lines changed matching \"{Query.ContainingText}\"" : $"changes to \"{Query.ContainingText}\"");
            }

            if (!string.IsNullOrWhiteSpace(Query.Path))
            {
                parts.Add($"path \"{Query.Path}\"");
            }

            if (!string.IsNullOrWhiteSpace(Query.FromRevision))
            {
                parts.Add($"history from {(Query.FromRevision.Length > 8 ? Query.FromRevision[..8] : Query.FromRevision)}");
            }

            return parts.Count == 0 ? "" : $"Searching: {string.Join(", ", parts)}";
        }
    }

    public UiMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                if (value != UiMode.Advanced)
                {
                    // Both are Advanced-only controls. Leaving them set while they are off screen
                    // would mean the next Commit quietly amends, or signs off, with nothing showing
                    // that it was going to.
                    Changes.IsAmend = false;
                    Changes.IsSignOff = false;
                }

                OnPropertyChanged(nameof(DetailsVisibility));
                OnPropertyChanged(nameof(ChromeVisibility));
                OnPropertyChanged(nameof(SecondaryColumnVisibility));
                OnPropertyChanged(nameof(LoadMoreVisibility));
                OnPropertyChanged(nameof(AdvancedVisibility));
                OnPropertyChanged(nameof(IsPaneOpen));
            }
        }
    }

    public string Branch
    {
        get => _branch;
        private set
        {
            if (SetProperty(ref _branch, value))
            {
                OnPropertyChanged(nameof(BranchHeader));
                RaiseTabDetails();
            }
        }
    }

    /// <summary>The upstream the current branch tracks, empty when it tracks nothing.</summary>
    public string Upstream
    {
        get => _upstream;
        private set
        {
            if (SetProperty(ref _upstream, value))
            {
                OnPropertyChanged(nameof(UpstreamVisibility));
            }
        }
    }

    public Visibility UpstreamVisibility => Upstream.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Commits on the current branch that the upstream does not have.</summary>
    public int AheadCount
    {
        get => _aheadCount;
        private set
        {
            if (SetProperty(ref _aheadCount, value))
            {
                OnPropertyChanged(nameof(AheadVisibility));
                RaiseTabDetails();
            }
        }
    }

    public int BehindCount
    {
        get => _behindCount;
        private set
        {
            if (SetProperty(ref _behindCount, value))
            {
                OnPropertyChanged(nameof(BehindVisibility));
                RaiseTabDetails();
            }
        }
    }

    public Visibility AheadVisibility => AheadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BehindVisibility => BehindCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Uncommitted change count, shown as a badge on the Changes navigation item.</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (SetProperty(ref _pendingCount, value))
            {
                OnPropertyChanged(nameof(PendingVisibility));
                RaiseTabDetails();
            }
        }
    }

    public Visibility PendingVisibility => PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///  What the repository's tab shows after its name — "main · 3 changes · ↑2 ↓1" — each part
    ///  switched on or off in Settings.
    /// </summary>
    public string TabDetailText
    {
        get
        {
            List<string> parts = [];

            if (AppOptions.TabShowBranch && Branch.Length > 0)
            {
                parts.Add(Branch);
            }

            if (AppOptions.TabShowChanges && PendingCount > 0)
            {
                parts.Add(PendingCount == 1 ? "1 change" : $"{PendingCount.ToString(CultureInfo.CurrentCulture)} changes");
            }

            if (AppOptions.TabShowAheadBehind && (AheadCount > 0 || BehindCount > 0))
            {
                parts.Add(string.Join(' ', new[] { AheadCount > 0 ? $"↑{AheadCount}" : "", BehindCount > 0 ? $"↓{BehindCount}" : "" }.Where(part => part.Length > 0)));
            }

            return string.Join(" · ", parts);
        }
    }

    public Visibility TabDetailVisibility => TabDetailText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseTabDetails()
    {
        OnPropertyChanged(nameof(TabDetailText));
        OnPropertyChanged(nameof(TabDetailVisibility));
    }

    private void OnTabDetailsChanged(object? sender, EventArgs e) => RaiseTabDetails();

    /// <summary>
    ///  Lays the graph out again in the new style from the commits already loaded — no git — and
    ///  carries on from there when more are paged in.
    /// </summary>
    private void OnGraphChanged(object? sender, EventArgs e)
    {
        CommitGraphBuilder builder = new();

        foreach (CommitRowViewModel row in _allCommits)
        {
            row.Graph = row.Revision is GitCommit revision ? builder.AddCommit(revision) : GraphRow.Empty;
        }

        _graphBuilder = builder;
        OnPropertyChanged(nameof(GraphColumnWidth));
    }

    /// <summary>"main" or "main ↑2 ↓1" — the title-bar summary of where the repository stands.</summary>
    public string BranchHeader => Branch.Length == 0 ? "(no branch)" : Branch;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ---- Operation reporting --------------------------------------------------------------------
    // Results land in an InfoBar on the repository view rather than a modal dialog: a fetch that
    // found nothing should not need dismissing before the next action, and the output of the last
    // operation is worth leaving on screen while you decide what to do about it.

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public string ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    public InfoBarSeverity ResultSeverity
    {
        get => _resultSeverity;
        private set => SetProperty(ref _resultSeverity, value);
    }

    /// <summary>Two-way with the InfoBar so dismissing it closes it for good rather than reopening.</summary>
    public bool IsResultOpen
    {
        get => _isResultOpen;
        set => SetProperty(ref _isResultOpen, value);
    }

    /// <summary>
    ///  Shows the outcome of a git operation. Success with no output closes the bar instead of
    ///  announcing nothing — the refreshed list is its own confirmation.
    /// </summary>
    public void Report(GitOperationResult result)
    {
        bool hasOutput = !string.IsNullOrWhiteSpace(result.Output);

        // Whatever was undoable before is not any more: the offer is only for what just happened.
        PendingUndo = result.Succeeded ? result.Undo : null;

        if (result.Succeeded && !hasOutput && PendingUndo is null)
        {
            IsResultOpen = false;
            return;
        }

        ResultTitle = result.Succeeded ? result.Description : $"{result.Description} failed";
        ResultMessage = hasOutput ? result.Output.Trim() : "git reported a failure with no output.";
        ResultSeverity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        IsResultOpen = true;
    }

    /// <summary>git's speed-ups in this repository, once read; null until then.</summary>
    internal SpeedSettings? Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    /// <summary>The "this repository is large, speed it up?" bar.</summary>
    public bool IsSpeedSuggestionOpen
    {
        get => _isSpeedSuggestionOpen;
        set => SetProperty(ref _isSpeedSuggestionOpen, value);
    }

    public string SpeedSuggestionMessage => Speed is not SpeedSettings speed
        ? ""
        : $"{(speed.Commits >= SpeedSettings.LargeCommits ? $"{speed.Commits:N0}+ commits" : speed.Commits == 1 ? "1 commit" : $"{speed.Commits:N0} commits")} and {speed.TrackedFiles:N0} files. " +
          "git can read its history about twice as fast with a commit-graph" +
          (speed.WantsFsMonitor ? ", and check for changes faster with its file watcher." : ".") +
          " Only this repository's settings change.";

    /// <summary>
    ///  Reads the speed-up settings once per tab, in the background, and offers them when the
    ///  repository is large and they are not already on — unless that offer was turned down before.
    /// </summary>
    private async Task CheckSpeedAsync()
    {
        try
        {
            Speed = await Task.Run(_loader.ReadSpeedSettings);
            OnPropertyChanged(nameof(SpeedSuggestionMessage));
            IsSpeedSuggestionOpen = Speed.IsLarge && !Speed.CommitGraph && !AppOptions.IsSpeedUpDeclined(WorkingDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Only an offer: nothing is lost by not making it.
        }
    }

    /// <summary>Re-reads the settings, for the Maintenance card after it changes them.</summary>
    internal async Task<SpeedSettings> RefreshSpeedAsync()
    {
        Speed = await Task.Run(_loader.ReadSpeedSettings);
        OnPropertyChanged(nameof(SpeedSuggestionMessage));
        return Speed;
    }

    public async Task ApplySpeedUpsAsync(bool fileWatcher)
    {
        IsSpeedSuggestionOpen = false;
        Report(await Task.Run(() => _loader.ApplySpeedUps(fileWatcher)));
        await RefreshSpeedAsync();
    }

    public async Task StopFileWatcherAsync()
    {
        Report(await Task.Run(_loader.StopFileWatcher));
        await RefreshSpeedAsync();
    }

    public void DeclineSpeedUp()
    {
        IsSpeedSuggestionOpen = false;
        AppOptions.DeclineSpeedUp(WorkingDir);
    }

    /// <summary>The Undo for the operation the result bar is showing; null when there is nothing to take back.</summary>
    private Func<GitOperationResult>? PendingUndo
    {
        get => _pendingUndo;
        set
        {
            _pendingUndo = value;
            OnPropertyChanged(nameof(UndoVisibility));
        }
    }

    public Visibility UndoVisibility => _pendingUndo is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Takes back the last reported operation, and reports that in its place.</summary>
    public async Task UndoAsync()
    {
        if (PendingUndo is not Func<GitOperationResult> undo)
        {
            return;
        }

        PendingUndo = null;
        Report(await RunOperationAsync(_ => undo()) with { Undo = null });
    }

    /// <summary>Reports something the front-end itself decided, with no git command behind it.</summary>
    public void ReportInformation(string title, string message)
    {
        PendingUndo = null;
        ResultTitle = title;
        ResultMessage = message;
        ResultSeverity = InfoBarSeverity.Informational;
        IsResultOpen = true;
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseWorkingChanged();
            }
        }
    }

    /// <summary>True while a git operation (fetch/pull/push/checkout/commit) is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseWorkingChanged();
            }
        }
    }

    /// <summary>Anything running at all — a load or an operation — for the status bar's spinner.</summary>
    public bool IsWorking => IsBusy || IsLoading;

    public Visibility BusyVisibility => IsWorking ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseWorkingChanged()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(BusyVisibility));
    }

    /// <summary>
    ///  Bound to the branch picker. Assigning it from the UI checks that branch out; assignments made
    ///  while refreshing our own state are suppressed so they don't trigger a spurious checkout.
    /// </summary>
    public string? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (!SetProperty(ref _selectedBranch, value))
            {
                return;
            }

            if (!_suppressBranchCheckout && value is not null && value != Branch)
            {
                _ = CheckoutAsync(value);
            }
        }
    }

    public CommitRowViewModel? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            CommitRowViewModel? previous = _selectedCommit;

            if (SetProperty(ref _selectedCommit, value))
            {
                if (previous is not null)
                {
                    previous.PropertyChanged -= OnSelectedCommitPropertyChanged;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnSelectedCommitPropertyChanged;
                }

                OnPropertyChanged(nameof(HasSelectedCommit));
                OnPropertyChanged(nameof(SelectedCommitVisibility));
                OnPropertyChanged(nameof(SelectedChecksVisibility));
                _ = LoadChangedFilesAsync(value);
            }
        }
    }

    /// <summary>
    ///  A second commit to compare <see cref="SelectedCommit"/> against.
    /// </summary>
    /// <remarks>
    ///  When set, the changed-files pane and the diff panel show the difference between the two
    ///  commits rather than what the selected one changed. Reusing those two controls is why
    ///  comparing needs no separate page: the question "what is different" has the same shape whether
    ///  the answer comes from one commit or two.
    /// </remarks>
    public CommitRowViewModel? CompareTarget
    {
        get => _compareTarget;
        set
        {
            if (SetProperty(ref _compareTarget, value))
            {
                OnPropertyChanged(nameof(IsComparing));
                OnPropertyChanged(nameof(CompareVisibility));
                OnPropertyChanged(nameof(CompareDescription));
                _ = LoadChangedFilesAsync(SelectedCommit);
            }
        }
    }

    public bool IsComparing => CompareTarget is not null && SelectedCommit is not null;

    public Visibility CompareVisibility => IsComparing ? Visibility.Visible : Visibility.Collapsed;

    public string CompareDescription =>
        IsComparing ? $"Comparing {CompareTarget!.ShortHash} → {SelectedCommit!.ShortHash}" : "";

    /// <summary>Drops back to showing what the selected commit itself changed.</summary>
    public void StopComparing() => CompareTarget = null;

    public bool HasSelectedCommit => SelectedCommit is not null;

    /// <summary>The details pane's Checks section shows only for a commit GitHub Actions ran for.</summary>
    public Visibility SelectedChecksVisibility => SelectedCommit?.ChecksVisibility ?? Visibility.Collapsed;

    private void OnSelectedCommitPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The runs land after the row was selected, and again whenever a running one finishes.
        if (e.PropertyName == nameof(CommitRowViewModel.Checks))
        {
            OnPropertyChanged(nameof(SelectedChecksVisibility));
        }
    }

    /// <summary>Collapses the details pane's contents rather than showing empty fields.</summary>
    public Visibility SelectedCommitVisibility => HasSelectedCommit ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Selecting a changed file loads its diff into the History page's diff panel.</summary>
    public ChangedFileViewModel? SelectedChangedFile
    {
        get => _selectedChangedFile;
        set
        {
            if (SetProperty(ref _selectedChangedFile, value))
            {
                _ = RefreshDetailAsync();
            }
        }
    }

    /// <summary>
    ///  Whether the History page's file viewer is open: the selected file's diff, with Blame and File
    ///  history beside it.
    /// </summary>
    /// <remarks>
    ///  Closed by default, so the history fills the page. It opens when a changed file is clicked,
    ///  and nothing is read for it while it is closed — stepping through commits costs no patch reads.
    /// </remarks>
    public bool IsFileViewerOpen
    {
        get => _isFileViewerOpen;
        set
        {
            if (SetProperty(ref _isFileViewerOpen, value))
            {
                OnPropertyChanged(nameof(FileViewerVisibility));

                if (value)
                {
                    _ = RefreshDetailAsync();
                }
                else
                {
                    Blame.Cancel();
                    FileHistory.Cancel();
                }
            }
        }
    }

    public Visibility FileViewerVisibility => IsFileViewerOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows one changed file in the viewer, opening it if it is closed.</summary>
    public void OpenFileViewer(ChangedFileViewModel file)
    {
        SelectedChangedFile = file;
        IsFileViewerOpen = true;
    }

    /// <summary>
    ///  Whether Blame is open beside the History page's diff. Read only while it is: a repository
    ///  that is only ever browsed never pays for a blame.
    /// </summary>
    public bool IsBlameOpen
    {
        get => _isBlameOpen;
        set
        {
            if (SetProperty(ref _isBlameOpen, value))
            {
                AppOptions.HistoryBlameOpen = value;
                OnPropertyChanged(nameof(BlameVisibility));
                OnPropertyChanged(nameof(SidePaneVisibility));

                if (value && IsFileViewerOpen)
                {
                    _ = Blame.LoadAsync(SelectedChangedFile, SelectedCommit);
                }
                else
                {
                    Blame.Cancel();
                }
            }
        }
    }

    /// <summary>Whether File history is open beside the History page's diff.</summary>
    public bool IsFileHistoryOpen
    {
        get => _isFileHistoryOpen;
        set
        {
            if (SetProperty(ref _isFileHistoryOpen, value))
            {
                AppOptions.HistoryFileHistoryOpen = value;
                OnPropertyChanged(nameof(FileHistoryVisibility));
                OnPropertyChanged(nameof(SidePaneVisibility));

                if (value && IsFileViewerOpen)
                {
                    _ = FileHistory.LoadAsync(SelectedChangedFile, SelectedCommit);
                }
                else
                {
                    FileHistory.Cancel();
                }
            }
        }
    }

    public Visibility BlameVisibility => IsBlameOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FileHistoryVisibility => IsFileHistoryOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The column to the diff's right, shown while either panel is open.</summary>
    public Visibility SidePaneVisibility => IsBlameOpen || IsFileHistoryOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///  The diff, and whichever side panels are open, for the selected file and commit — only while the
    ///  viewer is open.
    /// </summary>
    private Task RefreshDetailAsync()
    {
        if (!IsFileViewerOpen)
        {
            return Task.CompletedTask;
        }

        List<Task> loads = [LoadHistoryDiffAsync(SelectedChangedFile)];

        if (IsBlameOpen)
        {
            loads.Add(Blame.LoadAsync(SelectedChangedFile, SelectedCommit));
        }

        if (IsFileHistoryOpen)
        {
            loads.Add(FileHistory.LoadAsync(SelectedChangedFile, SelectedCommit));
        }

        return Task.WhenAll(loads);
    }

    /// <summary>Blame of a file, for <see cref="BlameViewModel"/>.</summary>
    internal GitBlameResult ReadBlame(string fileName, string? revision, CancellationToken cancellationToken) =>
        _loader.GetBlame(fileName, revision, cancellationToken);

    /// <summary>The commits that changed a file, for <see cref="FileHistoryViewModel"/>.</summary>
    internal IReadOnlyList<GitCommit> ReadFileHistory(string fileName, string? revision, int maxCount, CancellationToken cancellationToken) =>
        _loader.GetFileHistory(fileName, maxCount, revision, cancellationToken);

    /// <summary>Asks the History page to bring a row into view after <see cref="RevealCommit"/> selected it.</summary>
    public event EventHandler<CommitRowViewModel>? CommitRevealRequested;

    /// <summary>
    ///  Selects a commit in the list, and shows what it did to a file.
    /// </summary>
    /// <remarks>
    ///  The way back from Blame and File history to the commit behind a line. The same file stays
    ///  selected, so the diff shows that commit's change to that file rather than to whichever file
    ///  happens to be first in its list.
    /// </remarks>
    /// <returns>False when the commit is not among those loaded.</returns>
    public bool RevealCommit(string commitId, string? fileName)
    {
        CommitRowViewModel? row = Commits.FirstOrDefault(commit => !commit.IsWorkingDirectory && commit.FullHash == commitId);

        if (row is null)
        {
            ReportInformation(
                "Commit not loaded",
                "That commit is older than the history loaded so far, or hidden by the filter. Load more commits or clear the filter, then try again.");

            return false;
        }

        _preferredFileName = fileName;

        if (ReferenceEquals(SelectedCommit, row))
        {
            // Nothing will reload, so the file is chosen here.
            SelectedChangedFile = ChangedFiles.FirstOrDefault(file => file.Name == fileName) ?? SelectedChangedFile;
            _preferredFileName = null;
        }
        else
        {
            SelectedCommit = row;
        }

        CommitRevealRequested?.Invoke(this, row);
        return true;
    }

    /// <summary>
    ///  Puts what GitHub Actions made of each commit on the rows already loaded. Called when the runs
    ///  have been read, and when the settings that decide whether to show them change.
    /// </summary>
    public void ApplyCommitChecks()
    {
        foreach (CommitRowViewModel row in _allCommits)
        {
            if (!row.IsWorkingDirectory)
            {
                row.ApplyChecks(GitHub.CommitChecks.For(row.FullHash));
            }
        }
    }

    /// <summary>The commit cap was hit, so there is more history to page in.</summary>
    public bool HasMoreCommits
    {
        get => _hasMoreCommits;
        private set
        {
            if (SetProperty(ref _hasMoreCommits, value))
            {
                OnPropertyChanged(nameof(LoadMoreVisibility));
            }
        }
    }

    /// <summary>Commit-details pane: Advanced only.</summary>
    public Visibility DetailsVisibility => Mode == UiMode.Advanced ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///  Anything Advanced-only: the reflog, submodule, worktree and maintenance sections, and the
    ///  amend and sign-off commit options.
    /// </summary>
    public Visibility AdvancedVisibility => Mode == UiMode.Advanced ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sections <see cref="AdvancedVisibility"/> hides, by navigation tag.</summary>
    /// <remarks>
    ///  Named here rather than in the view so that the view and the navigation markup cannot drift
    ///  apart: a section hidden in the markup but missing from this list would stay selected with no
    ///  item to show for it.
    /// </remarks>
    public static IReadOnlyList<string> AdvancedSections { get; } = ["reflog", "submodules", "worktrees"];

    /// <summary>Column headers, branch/path line: everything except Zen.</summary>
    public Visibility ChromeVisibility => Mode == UiMode.Zen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Author and date columns — dropped in Zen so only the message remains.</summary>
    public Visibility SecondaryColumnVisibility => Mode == UiMode.Zen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Zen collapses the navigation pane to nothing; the other modes leave it open.</summary>
    public bool IsPaneOpen => Mode != UiMode.Zen;

    /// <summary>
    ///  Zen drops the pane to its minimal form so the commit list is all that is left; every other
    ///  mode shows the full left pane.
    /// </summary>
    public NavigationViewPaneDisplayMode PaneDisplayMode =>
        Mode == UiMode.Zen ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;

    public Visibility LoadMoreVisibility =>
        HasMoreCommits && Mode != UiMode.Zen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///  Reloads the commit list, streaming rows in as <c>git log</c> produces them so the window stays
    ///  responsive on large repositories rather than blocking until the whole history is read.
    /// </summary>
    public Task LoadAsync() => LoadPageAsync(append: false);

    /// <summary>Appends the next page of older commits.</summary>
    public Task LoadMoreAsync() => LoadPageAsync(append: true);

    // ---- Object page loads ----------------------------------------------------------------------

    public Task LoadBranchesAsync() => ReplaceAsync(BranchDetails, _loader.GetBranches);

    public Task LoadRemoteBranchesAsync() => ReplaceAsync(RemoteBranches, _loader.GetRemoteBranches);

    public Task LoadLocalConfigAsync() =>
        ReplaceAsync(LocalConfig, () => _loader.GetConfiguration(GitConfigScope.Local));

    public Task LoadGlobalConfigAsync() =>
        ReplaceAsync(GlobalConfig, () => _loader.GetConfiguration(GitConfigScope.Global), watched: false);

    /// <summary>
    ///  The Overview's figures. Kept between visits and handed back to the loader, which reuses them
    ///  for as long as <c>HEAD</c> has not moved.
    /// </summary>
    internal async Task<RepositoryOverview> GetOverviewAsync()
    {
        RepositoryOverview? previous = _overview;
        RepositoryOverview overview = await Task.Run(() => _loader.ReadOverview(previous));
        _overview = overview;
        return overview;
    }

    /// <summary>Makes the next <see cref="GetOverviewAsync"/> read everything again.</summary>
    internal void ForgetOverview() => _overview = null;

    /// <summary>Which of these repository-relative paths git ignores. For the Files section.</summary>
    internal Task<HashSet<string>> GetIgnoredAsync(IReadOnlyList<string> relativePaths) =>
        Task.Run(() => _loader.IgnoredPaths(relativePaths));

    /// <summary>
    ///  Points the loader's network commands at the GitHub account chosen for this repository, so push
    ///  and pull act as the same account the GitHub pages do.
    /// </summary>
    /// <remarks>
    ///  Only an account that has been chosen — by hand, or found automatically because gh's active one
    ///  could not see the repository. With no choice, git's own credential helper answers as before.
    ///  gh is asked for the token once per account; it does not change while gh stays signed in.
    /// </remarks>
    private async Task EnsureGitHubCredentialAsync()
    {
        string login = Host is { IsGitHub: true } ? await GetChosenGitHubAccountAsync() : "";

        if (login.Length == 0 || !GitHubCredential.IsValidLogin(login))
        {
            _loader.GitHubCredential = null;
            return;
        }

        if (_loader.GitHubCredential?.Login == login)
        {
            return;
        }

        string? token = await Gitxen.GitHub.GitHubClient.GetAccountTokenAsync(GitHubService.CreateCli(WorkingDir), login);
        _loader.GitHubCredential = token is null ? null : new GitHubCredential(login, token);
    }

    /// <summary>
    ///  The GitHub account chosen for this repository: its own <c>credential.https://github.com.username</c>
    ///  first — what <c>gx account use</c> and the account menu both write, and what git's credential
    ///  helpers read — then the choice Gitxen found automatically. Empty when neither.
    /// </summary>
    internal async Task<string> GetChosenGitHubAccountAsync()
    {
        string configured = await GetConfigValueAsync(GitConfigScope.Local, GitHubAccountChoice.CredentialUserKey);

        return configured.Length > 0 || Host is not { IsGitHub: true } host
            ? configured
            : AppOptions.GitHubAccountFor($"{host.Owner}/{host.Name}");
    }

    /// <summary>Drops the token held for network commands, so the next one looks the account up again.</summary>
    internal void ForgetGitHubCredential() => _loader.GitHubCredential = null;

    public Task<string> GetConfigValueAsync(GitConfigScope scope, string key) =>
        Task.Run(() => _loader.GetConfigValue(scope, key));

    /// <summary>
    ///  Writes a setting. Unlike the git operations, this does not reload the commit list — nothing
    ///  about history changes when a name or a line-ending rule does.
    /// </summary>
    public async Task<GitOperationResult> SetConfigValueAsync(GitConfigScope scope, string key, string value)
    {
        GitOperationResult result = await Task.Run(() => _loader.SetConfigValue(scope, key, value));

        // The config page saves and immediately reloads itself. The watcher reports the write too,
        // but not before that reload asks for the listing, so without this the page would redisplay
        // the values it had before saving.
        InvalidateListings();

        return result;
    }

    /// <summary>Capped: the reflog is a recovery aid, not a history to page through.</summary>
    public Task LoadReflogAsync() => ReplaceAsync(Reflog, () => _loader.GetReflog(maxCount: 200));

    public Task LoadRemotesAsync() => ReplaceAsync(Remotes, _loader.GetRemotes);

    public Task LoadTagsAsync() => ReplaceAsync(Tags, _loader.GetTags);

    public Task LoadStashesAsync() => ReplaceAsync(Stashes, _loader.GetStashes);

    public Task LoadSubmodulesAsync() => ReplaceAsync(Submodules, _loader.GetSubmodules, watched: false);

    public Task LoadWorktreesAsync() => ReplaceAsync(Worktrees, _loader.GetWorktrees);

    public Task<GitOperationResult> FetchAsync() => RunTransferAsync("Fetching", loader => loader.Fetch());

    public Task<GitOperationResult> PullAsync() => RunTransferAsync("Pulling", loader => loader.Pull());

    public Task<GitOperationResult> PushAsync()
    {
        string branch = Branch;
        return RunTransferAsync("Pushing", loader => loader.Push(branch));
    }

    public Task<GitOperationResult> CommitAllAsync(string message) =>
        RunOperationAsync(loader => loader.CommitAll(message));

    /// <summary>Undoes the last commit, keeping its changes staged so they can be recommitted.</summary>
    public Task<GitOperationResult> UndoLastCommitAsync() =>
        RunOperationAsync(loader => loader.UndoLastCommit());

    public Task<string> GetLastCommitMessageAsync() => Task.Run(_loader.GetLastCommitMessage);

    public Task<GitOperationResult> CheckoutBranchAsync(string branch) =>
        RunOperationAsync(loader => loader.Checkout(branch));

    public Task<GitOperationResult> CheckoutDetachedAsync(string reference) =>
        RunOperationAsync(loader => loader.CheckoutDetached(reference));

    public Task<GitOperationResult> CreateBranchAsync(string branchName, bool checkout)
    {
        // Branch from the selected commit when there is one, otherwise from HEAD.
        if (SelectedCommit?.Revision?.Id is not string commitId)
        {
            return RunOperationAsync(loader => loader.CreateBranchFromHead(branchName, checkout));
        }

        return RunOperationAsync(loader => loader.CreateBranchAt(branchName, commitId, checkout));
    }

    /// <summary>Creates a branch at any resolvable reference — a reflog selector, a remote branch.</summary>
    public Task<GitOperationResult> CreateBranchAtAsync(string branchName, string reference, bool checkout) =>
        RunOperationAsync(loader => loader.CreateBranchAt(branchName, reference, checkout));

    public Task<GitOperationResult> ResetToAsync(string reference, GitResetMode mode) =>
        RunOperationAsync(loader => loader.ResetTo(reference, mode));

    /// <summary>
    ///  Branches that look finished with. On GitHub, the branches of merged pull requests are asked for
    ///  too, so squash- and rebase-merged branches are found as well as plainly merged ones.
    /// </summary>
    internal async Task<IReadOnlyList<CleanupCandidate>> FindCleanupCandidatesAsync()
    {
        HashSet<string> mergedOnGitHub = new(StringComparer.Ordinal);

        try
        {
            if (AppOptions.GitHubEnabled && Host is { IsGitHub: true } && await GitHub.EnsureReadyAsync())
            {
                (IReadOnlyList<Gitxen.GitHub.PullRequest> merged, _) = await GitHub.Client.ListPullRequestsAsync("merged", 200);
                mergedOnGitHub.UnionWith(merged.Select(pr => pr.HeadBranch).Where(name => name.Length > 0));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // Without GitHub's answer the local checks still stand.
        }

        return await Task.Run(() => _loader.FindCleanupCandidates(mergedOnGitHub));
    }

    /// <summary>The commits after <paramref name="baseId"/> to edit, or why they cannot be.</summary>
    internal Task<RebasePlan> PlanRebaseAsync(string baseId) => Task.Run(() => _loader.PlanRebase(baseId));

    /// <summary>Runs an edited plan (oldest first); the result offers Undo.</summary>
    internal Task<GitOperationResult> RewriteCommitsAsync(string baseId, IReadOnlyList<RebaseStep> steps) =>
        RunOperationAsync(loader => loader.RewriteCommits(baseId, steps));

    /// <summary>Every commit that changed one line, newest first, from the commit blame names for it.</summary>
    internal Task<IReadOnlyList<GitLineChange>> ReadLineHistoryAsync(GitBlameLine line) =>
        Task.Run(() => _loader.ReadLineHistory(line));

    /// <summary>A commit message for what is staged, written by Claude. Throws <see cref="AiException"/>.</summary>
    internal async Task<string> WriteCommitMessageAsync()
    {
        (string patch, IReadOnlyList<string> subjects) = await Task.Run(() => (_loader.StagedPatch(), _loader.RecentSubjects()));

        if (patch.Length == 0)
        {
            throw new AiException("Nothing is staged yet. Stage the changes to describe first.");
        }

        return await AiAssistant.WriteCommitMessageAsync(patch, subjects);
    }

    /// <summary>A pull request title and description for <paramref name="head"/> into <paramref name="baseBranch"/>.</summary>
    internal async Task<(string Title, string Body)> WritePullRequestAsync(string baseBranch, string head)
    {
        (string commits, string diff) = await Task.Run(() => _loader.PullRequestMaterial(baseBranch, head));

        if (commits.Length == 0 && diff.Length == 0)
        {
            throw new AiException($"{head} has nothing that {baseBranch} does not already have.");
        }

        return await AiAssistant.WritePullRequestAsync(baseBranch, head, commits, diff);
    }

    /// <summary>A plain-language explanation of a commit.</summary>
    internal async Task<string> ExplainCommitAsync(string commitId)
    {
        string material = await Task.Run(() => _loader.CommitWithPatch(commitId));
        return await AiAssistant.ExplainCommitAsync(material);
    }

    /// <summary>The secret and large-file guard over what is staged.</summary>
    internal Task<IReadOnlyList<ScanFinding>> ScanStagedAsync() => Task.Run(_loader.ScanStaged);

    /// <summary>The same guard over the commits a push of the current branch would send.</summary>
    internal Task<IReadOnlyList<ScanFinding>> ScanOutgoingAsync()
    {
        string branch = Branch;
        return Task.Run(() => _loader.ScanOutgoing(branch));
    }

    /// <summary>Takes flagged files out of the commit; ones that hold secrets by nature go into .gitignore.</summary>
    internal async Task<GitOperationResult> UnstageFlaggedAsync(IReadOnlyList<ScanFinding> findings)
    {
        IReadOnlyList<string> paths = [.. findings.Select(finding => finding.Path).Distinct(StringComparer.Ordinal)];
        IReadOnlyList<string> ignore = [.. findings.Where(finding => finding.Kind == FindingKind.SensitiveFile).Select(finding => finding.Path).Distinct(StringComparer.Ordinal)];
        GitOperationResult result = await RunOperationAsync(loader => loader.UnstageAndIgnore(paths, ignore));
        await Changes.RefreshAsync();
        return result;
    }

    public Task<GitOperationResult> DeleteBranchesAsync(IReadOnlyList<string> branchNames) =>
        RunOperationAsync(loader => loader.DeleteBranches(branchNames));

    public Task<GitOperationResult> DeleteBranchAsync(string branchName, bool force) =>
        RunOperationAsync(loader => loader.DeleteBranch(branchName, force));

    public Task<GitOperationResult> RenameBranchAsync(string oldName, string newName) =>
        RunOperationAsync(loader => loader.RenameBranch(oldName, newName));

    public Task<GitOperationResult> SetUpstreamAsync(string branch, string upstream) =>
        RunOperationAsync(loader => loader.SetUpstream(branch, upstream));

    public Task<GitOperationResult> MergeBranchAsync(string branchName) =>
        RunOperationAsync(loader => loader.MergeBranch(branchName));

    public Task<GitOperationResult> MergeBranchAsync(string branchName, MergeOptions options) =>
        RunOperationAsync(loader => loader.MergeBranch(branchName, options));

    public Task<GitOperationResult> PushAsync(PushOptions options) =>
        RunTransferAsync("Pushing", loader => loader.Push(options));

    public Task<GitOperationResult> PullAsync(PullOptions options) =>
        RunTransferAsync("Pulling", loader => loader.Pull(options));

    public Task<GitOperationResult> DeleteRemoteBranchAsync(string remote, string branch) =>
        RunTransferAsync("Deleting on the remote", loader => loader.DeleteRemoteBranch(remote, branch));

    public Task<GitOperationResult> CleanAsync(bool includeDirectories, bool includeIgnored, bool dryRun) =>
        RunOperationAsync(loader => loader.Clean(includeDirectories, includeIgnored, dryRun));

    public Task<GitOperationResult> CollectGarbageAsync() =>
        RunOperationAsync(loader => loader.CollectGarbage());

    public Task<GitOperationResult> ArchiveAsync(string reference, string outputPath) =>
        RunOperationAsync(loader => loader.Archive(reference, outputPath));

    public Task<string> GetStashDiffAsync(string reference) =>
        Task.Run(() => _loader.GetStashDiff(reference));

    /// <summary>Tracked paths for the go-to palette; read fresh so it is never stale.</summary>
    public Task<IReadOnlyList<string>> GetTrackedFilesAsync() =>
        Task.Run(() => _loader.GetTrackedFiles());

    public Task<GitOperationResult> StashSaveAsync(string message) =>
        RunOperationAsync(loader => loader.StashSave(message));

    public Task<GitOperationResult> StashSaveAsync(string message, bool includeUntracked, bool keepIndex) =>
        RunOperationAsync(loader => loader.StashSave(message, includeUntracked, keepIndex));

    public Task<GitOperationResult> StashPopAsync() =>
        RunOperationAsync(loader => loader.StashPop());

    public Task<GitOperationResult> StashApplyAsync(string reference) =>
        RunOperationAsync(loader => loader.StashApply(reference));

    public Task<GitOperationResult> StashPopAsync(string reference) =>
        RunOperationAsync(loader => loader.StashPop(reference));

    public Task<GitOperationResult> StashDropAsync(string reference) =>
        RunOperationAsync(loader => loader.StashDrop(reference));

    /// <summary>
    ///  Operations that act on the selected commit. They report rather than throw when nothing is
    ///  selected, so the menu items never need to be disabled.
    /// </summary>
    public Task<GitOperationResult> CherryPickAsync() =>
        RunOnSelectedCommitAsync("Cherry-pick", (loader, commit) => loader.CherryPick(commit));

    public Task<GitOperationResult> RevertAsync() =>
        RunOnSelectedCommitAsync("Revert", (loader, commit) => loader.Revert(commit));

    public Task<GitOperationResult> ResetToSelectedAsync(GitResetMode mode) =>
        RunOnSelectedCommitAsync($"Reset ({mode})", (loader, commit) => loader.ResetTo(commit, mode));

    public Task<GitOperationResult> BisectAtSelectedAsync(string action) =>
        RunOnSelectedCommitAsync($"Bisect {action}", (loader, commit) => loader.Bisect(action, commit));

    public Task<GitOperationResult> RebaseAsync(string onto) =>
        RunOperationAsync(loader => loader.Rebase(onto));

    public Task<GitOperationResult> ContinueOperationAsync(string operation, string action) =>
        RunOperationAsync(loader => loader.ContinueOperation(operation, action));

    public Task<GitOperationResult> BisectAsync(string action) =>
        RunOperationAsync(loader => loader.Bisect(action));

    /// <summary>Re-reads the conflicts and refreshes the count the navigation badge shows.</summary>
    public async Task LoadConflictsAsync()
    {
        try
        {
            ApplyConflicts(await Task.Run(_loader.GetConflicts));
        }
        catch (Exception ex)
        {
            // A listing page is not worth a crash; the same rule ReplaceAsync follows.
            Status = ex.Message;
        }
    }

    /// <summary>
    ///  Publishes a conflict listing that has already been read.
    /// </summary>
    /// <remarks>
    ///  Separate from the read so that a reload, which needs several git commands sequenced around
    ///  the index, can do the reading itself. See LoadPageAsync.
    /// </remarks>
    private void ApplyConflicts(IReadOnlyList<ConflictedFile> conflicts)
    {
        Conflicts.Clear();

        foreach (ConflictedFile conflict in conflicts)
        {
            Conflicts.Add(conflict);
        }

        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(ConflictVisibility));
    }

    /// <summary>
    ///  Resolves one conflict by taking a side. Reloads afterwards, since resolving the last one
    ///  changes what the operation banner should say.
    /// </summary>
    public Task<GitOperationResult> TakeSideAsync(ConflictedFile file, bool ours) =>
        RunOperationAsync(loader => loader.TakeSide(file, ours));

    public Task<GitOperationResult> TakeSideForAllAsync(bool ours) =>
        RunOperationAsync(loader => loader.TakeSideForAll(ours));

    /// <summary>Stages a file the user resolved by hand, which is what git means by resolved.</summary>
    public Task<GitOperationResult> MarkConflictResolvedAsync(string path) =>
        RunOperationAsync(loader => loader.MarkResolved(path));

    /// <summary>Runs the merge tool. Blocks until the tool closes, so it is kept off the UI thread.</summary>
    public Task<GitOperationResult> LaunchMergeToolAsync(string path) =>
        RunOperationAsync(loader => loader.LaunchMergeTool(path));

    public Task<string> ReadConflictedTextAsync(string path) =>
        Task.Run(() => _loader.ReadConflictedText(path));

    public Task<IReadOnlyList<string>> GetConflictedFilesAsync() =>
        Task.Run(_loader.GetConflictedFiles);

    public Task<GitOperationResult> MarkResolvedAsync(string fileName) =>
        RunOperationAsync(loader => loader.MarkResolved(fileName));

    public Task<GitOperationResult> CreateTagAsync(string name, string message) =>
        RunOperationAsync(loader => loader.CreateTag(name, SelectedCommit?.Revision?.Id, message));

    public Task<GitOperationResult> DeleteTagAsync(string name) =>
        RunOperationAsync(loader => loader.DeleteTag(name));

    public Task<GitOperationResult> PushTagAsync(string name) =>
        RunTransferAsync("Pushing a tag", loader => loader.PushTag(name));

    public Task<GitOperationResult> AddRemoteAsync(string name, string url) =>
        RunOperationAsync(loader => loader.AddRemote(name, url));

    public Task<GitOperationResult> RemoveRemoteAsync(string name) =>
        RunOperationAsync(loader => loader.RemoveRemote(name));

    public Task<GitOperationResult> FetchRemoteAsync(string remote, bool prune) =>
        RunTransferAsync("Fetching", loader => loader.FetchRemote(remote, prune));

    public Task<GitOperationResult> UpdateSubmodulesAsync() =>
        RunTransferAsync("Updating submodules", loader => loader.UpdateSubmodules());

    public Task<GitOperationResult> UpdateSubmoduleAsync(string path) =>
        RunTransferAsync("Updating a submodule", loader => loader.UpdateSubmodule(path));

    public Task<GitOperationResult> SyncSubmodulesAsync() =>
        RunOperationAsync(loader => loader.SyncSubmodules());

    public Task<GitOperationResult> AddWorktreeAsync(string path, string branch) =>
        RunOperationAsync(loader => loader.AddWorktree(path, branch));

    public Task<GitOperationResult> RemoveWorktreeAsync(string path) =>
        RunOperationAsync(loader => loader.RemoveWorktree(path));

    public Task<GitOperationResult> PruneWorktreesAsync() =>
        RunOperationAsync(loader => loader.PruneWorktrees());

    /// <summary>Names of the files that a commit would include, for confirmation before committing.</summary>
    public Task<IReadOnlyList<string>> GetPendingChangesAsync() =>
        Task.Run<IReadOnlyList<string>>(() => _loader.GetWorkingDirectoryChanges().Select(file => file.Path).ToList());

    public void Close()
    {
        AppOptions.TabDetailsChanged -= OnTabDetailsChanged;
        AppOptions.GraphChanged -= OnGraphChanged;
        GitHub.Dispose();
        Blame.Cancel();
        FileHistory.Cancel();
        _watcher.Dispose();
        _loadCts?.Cancel();
        _changedFilesCts?.Cancel();
        HistoryDiff.Clear();
        Changes.Diff.Clear();
    }

    private async Task LoadPageAsync(bool append)
    {
        Cancel(ref _loadCts);
        CancellationTokenSource cts = new();
        _loadCts = cts;

        // The working-directory row isn't part of the history, so it never counts towards paging.
        int skip = append ? _loadedCommitCount : 0;

        IsLoading = true;
        Status = "";
        HasMoreCommits = false;

        if (!append)
        {
            _allCommits.Clear();
            _loadedCommitCount = 0;
            FileHistory.Invalidate();
            Commits.Clear();
            ChangedFiles.Clear();

            // Lanes are only meaningful relative to the commits already placed, so a full reload
            // starts the graph over.
            _graphBuilder = new CommitGraphBuilder();
        }

        try
        {
            // Read the branch and refs first: once the commit cap is hit the stream is cancelled,
            // which would otherwise abort these too.
            if (!append)
            {
                // The setup reads, each of which is its own git process. Started together
                // rather than awaited one at a time: none of them needs another's answer, and on
                // Windows each costs most of a process launch (60-155ms on this repository), so
                // running them in sequence spent the better part of a second before `git log` had
                // even started. They are applied below in the order they were applied before, on the
                // UI thread, so anything reading these properties sees them settle as it always did.
                Task<string> branch = Task.Run(_loader.GetCurrentBranch, cts.Token);
                Task<IReadOnlyDictionary<string, List<RefBadge>>> refs = Task.Run(_loader.GetRefsByCommit, cts.Token);
                Task<IReadOnlyList<RemoteInfo>> remotes = Task.Run(_loader.GetRemotes, cts.Token);
                Task<IReadOnlyList<BranchInfo>> branches = Task.Run(_loader.GetBranches, cts.Token);
                Task<(string Name, string Email)> identity = Task.Run(_loader.GetEffectiveIdentity, cts.Token);

                // The three reads that consult the index are chained to each other rather than
                // started alongside the rest: each runs `git status` or `git diff`, both of which
                // refresh the index and can take index.lock to write it back, and two of those at
                // once can leave one of them failing on the lock. Chained, they still overlap the
                // five reads above, which only touch refs and config.
                Task<(RepositoryOperation Operation, IReadOnlyList<ConflictedFile> Conflicts, IReadOnlyList<string> Pending)> workingTree
                    = Task.Run(
                        () =>
                        {
                            IReadOnlyList<ConflictedFile> conflicted = _loader.GetConflicts();
                            RepositoryOperation current = _loader.GetCurrentOperation(conflicted.Count);
                            IReadOnlyList<string> changed = [.. _loader.GetWorkingDirectoryChanges().Select(file => file.Path)];

                            return (current, conflicted, changed);
                        },
                        cts.Token);

                Branch = await branch;
                _refsByCommit = await refs;
                ApplyHost(await remotes);
                ApplyBranches(await branches);

                (RepositoryOperation operation, IReadOnlyList<ConflictedFile> conflicts, IReadOnlyList<string> pending) = await workingTree;
                Operation = operation;
                ApplyConflicts(conflicts);

                (string name, string email) = await identity;
                NeedsIdentity = name.Length == 0 || email.Length == 0;

                if (!_speedChecked)
                {
                    _speedChecked = true;
                    _ = CheckSpeedAsync();
                }

                AddWorkingDirectoryRow(pending);
            }

            // The completion callback is enqueued behind every batch the observer dispatched, so by
            // the time it runs the collection is fully populated — the DispatcherQueue is FIFO.
            StreamingObserver observer = new(_dispatcherQueue, cts.Token, batch => AddCommitBatch(batch, skip + MaxCommits), OnStreamFinished);

            // One row more than the page holds. Asking for exactly the page would leave "is there
            // more?" unanswerable without a second read, whereas the surplus row trips the cap in
            // AddCommitBatch, which is already what raises HasMoreCommits.
            RevisionQuery query = Query;
            await Task.Run(() => _loader.StreamRevisions(observer, skip, MaxCommits + 1, query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the tab closed.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
            }
        }
    }

    /// <summary>Adds a batch on the UI thread. Returns false once the page is full.</summary>
    private bool AddCommitBatch(IReadOnlyList<GitCommit> batch, int maxCommits)
    {
        foreach (GitCommit revision in batch)
        {
            if (_loadedCommitCount >= maxCommits)
            {
                return false;
            }

            CommitRowViewModel row = new(revision)
            {
                Graph = _graphBuilder.AddCommit(revision),
                Refs = _refsByCommit.TryGetValue(revision.Id, out List<RefBadge>? refs) ? refs : []
            };

            // What is already known about the commit, so a reload does not blank the marks and wait
            // for the next read to bring them back.
            row.ApplyChecks(GitHub.CommitChecks.For(revision.Id));

            _allCommits.Add(row);
            _loadedCommitCount++;

            if (PassesFilter(row))
            {
                Commits.Add(row);
            }
        }

        return true;
    }

    private void AddWorkingDirectoryRow(IReadOnlyList<string> pending)
    {
        PendingCount = pending.Count;

        if (pending.Count == 0)
        {
            return;
        }

        CommitRowViewModel row = CommitRowViewModel.CreateWorkingDirectory(pending.Count);
        _allCommits.Insert(0, row);

        if (PassesFilter(row))
        {
            Commits.Insert(0, row);
        }
    }

    private bool PassesFilter(CommitRowViewModel row) =>
        string.IsNullOrWhiteSpace(Filter) || row.IsWorkingDirectory || row.Matches(Filter);

    private void ApplyFilter()
    {
        CommitRowViewModel? previous = SelectedCommit;

        Commits.Clear();
        foreach (CommitRowViewModel row in _allCommits.Where(PassesFilter))
        {
            Commits.Add(row);
        }

        // Keep the current selection if it survived the filter, otherwise fall back to the top.
        SelectedCommit = previous is not null && Commits.Contains(previous) ? previous : Commits.FirstOrDefault();
    }

    /// <summary>
    ///  Runs on the UI thread once every streamed batch has been applied — either because the log
    ///  ended, or because the page was filled.
    /// </summary>
    private void OnStreamFinished(bool limitReached)
    {
        SelectedCommit ??= Commits.FirstOrDefault();

        // Marks for the commits just listed. Quietly does nothing for a repository that is not on
        // GitHub, and is throttled for one that is, so a burst of reloads is a single read.
        _ = GitHub.CommitChecks.RefreshAsync();

        if (limitReached)
        {
            // Stop git reading a history we're not going to show yet.
            _loadCts?.Cancel();
            HasMoreCommits = true;
        }

        Status = _loadedCommitCount == 0
            ? "No commits found."
            : $"{_loadedCommitCount.ToString("N0", CultureInfo.InvariantCulture)} commits";
    }

    /// <summary>
    ///  Reloads one object collection off the UI thread. Failures leave the collection empty and are
    ///  reported through <see cref="Status"/> rather than thrown — a listing page is not worth a crash.
    /// </summary>
    /// <param name="watched">
    ///  Whether everything this listing reads lives under <c>.git</c>, and is therefore covered by
    ///  the watcher. False for the two that read from outside it — the global config in the user's
    ///  home directory, and submodules, which are declared in the working tree's .gitmodules — so
    ///  those keep going to git on every visit.
    /// </param>
    /// <param name="key">The calling method's name, which is what identifies the listing.</param>
    private async Task ReplaceAsync<T>(
        ObservableCollection<T> target,
        Func<IReadOnlyList<T>> read,
        bool watched = true,
        [CallerMemberName] string key = "")
    {
        // Only reuse when changes are actually being reported. With no watcher there is no signal
        // that an answer went stale, and a stale listing is a wrong answer, not a slow one.
        bool reusable = watched && _watcher.IsWatching;

        if (reusable && !_freshListings.Add(key))
        {
            return;
        }

        try
        {
            IReadOnlyList<T> items = await Task.Run(read);

            target.Clear();
            foreach (T item in items)
            {
                target.Add(item);
            }
        }
        catch (Exception ex)
        {
            // Nothing was loaded, so nothing may be treated as loaded: leaving the key in place
            // would leave the section permanently empty until something else invalidated it.
            _freshListings.Remove(key);
            Status = ex.Message;
        }
    }

    /// <summary>
    ///  Runs a git operation off the UI thread and reloads afterwards. Returns the result so the view
    ///  can report it; never throws. Private because the loader it hands out is internal — callers
    ///  use the named operations above.
    /// </summary>
    private async Task<GitOperationResult> RunOperationAsync(Func<RepositoryLoader, GitOperationResult> operation)
    {
        if (IsBusy)
        {
            return new GitOperationResult("Busy", false, "Another git operation is still running.");
        }

        IsBusy = true;

        try
        {
            await EnsureGitHubCredentialAsync();
            GitOperationResult result = await Task.Run(() => operation(_loader));

            // The watcher will report this too, but not before the reload below goes looking for
            // listings — so the tab forgets them itself, at the moment it knows they moved.
            InvalidateListings();

            // Even a failed operation can have changed things (a partial fetch, a checkout that
            // touched the index), so always resync.
            await LoadAsync();
            return result;
        }
        catch (OperationCanceledException)
        {
            // git was stopped part-way. Whatever it had finished is real, so the tab still resyncs.
            InvalidateListings();
            await LoadAsync();
            return new GitOperationResult("Cancelled", true, "Stopped before it finished. Anything git had not completed was left as it was.");
        }
        catch (Exception ex)
        {
            return new GitOperationResult("Error", false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///  An operation that talks to a remote, shown with its progress and a Cancel button while it runs.
    /// </summary>
    private async Task<GitOperationResult> RunTransferAsync(string title, Func<RepositoryLoader, GitOperationResult> operation)
    {
        if (IsBusy)
        {
            return new GitOperationResult("Busy", false, "Another git operation is still running.");
        }

        using CancellationTokenSource cancel = new();
        ProgressPump pump = new(_dispatcherQueue, ShowProgress);

        _transferCancel = cancel;
        TransferTitle = title;
        TransferPhase = "Starting…";
        TransferDetail = "";
        TransferPercent = 0;
        TransferIndeterminate = true;
        IsTransferring = true;
        _loader.Transfer = new TransferContext(pump.Report, cancel.Token);

        try
        {
            return await RunOperationAsync(operation);
        }
        finally
        {
            _loader.Transfer = null;
            _transferCancel = null;
            IsTransferring = false;
        }
    }

    private void ShowProgress(GitProgress progress)
    {
        if (!IsTransferring)
        {
            return;
        }

        TransferPhase = ProgressPump.Phase(progress);
        TransferDetail = ProgressPump.Detail(progress);
        TransferIndeterminate = progress.Percent is null;
        TransferPercent = progress.Percent ?? 0;
    }

    /// <summary>Stops the transfer that is running, if any: git and its network helper are killed.</summary>
    public void CancelTransfer() => _transferCancel?.Cancel();

    public bool IsTransferring
    {
        get => _isTransferring;
        private set
        {
            if (SetProperty(ref _isTransferring, value))
            {
                OnPropertyChanged(nameof(TransferVisibility));
            }
        }
    }

    public Visibility TransferVisibility => _isTransferring ? Visibility.Visible : Visibility.Collapsed;

    public string TransferTitle
    {
        get => _transferTitle;
        private set => SetProperty(ref _transferTitle, value);
    }

    public string TransferPhase
    {
        get => _transferPhase;
        private set => SetProperty(ref _transferPhase, value);
    }

    public string TransferDetail
    {
        get => _transferDetail;
        private set => SetProperty(ref _transferDetail, value);
    }

    public double TransferPercent
    {
        get => _transferPercent;
        private set => SetProperty(ref _transferPercent, value);
    }

    public bool TransferIndeterminate
    {
        get => _transferIndeterminate;
        private set => SetProperty(ref _transferIndeterminate, value);
    }

    private Task<GitOperationResult> RunOnSelectedCommitAsync(string description, Func<RepositoryLoader, string, GitOperationResult> operation)
    {
        if (SelectedCommit?.Revision?.Id is not string commit)
        {
            return Task.FromResult(new GitOperationResult(description, false, "Select a commit first."));
        }

        return RunOperationAsync(loader => operation(loader, commit));
    }

    private async Task CheckoutAsync(string branch)
    {
        GitOperationResult result = await RunOperationAsync(loader => loader.Checkout(branch));

        if (!result.Succeeded)
        {
            Status = $"Checkout failed: {result.Output}";
        }
    }

    /// <summary>
    ///  Refreshes the branch picker and the ahead/behind counters from one <c>for-each-ref</c> read,
    ///  which reports both the names and the tracking state.
    /// </summary>
    private async Task RefreshBranchesAsync(CancellationToken cancellationToken) =>
        ApplyBranches(await Task.Run(_loader.GetBranches, cancellationToken));

    private void ApplyBranches(IReadOnlyList<BranchInfo> branches)
    {
        BranchDetails.Clear();
        Branches.Clear();

        foreach (BranchInfo branch in branches.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
        {
            Branches.Add(branch.Name);
        }

        foreach (BranchInfo branch in branches)
        {
            BranchDetails.Add(branch);
        }

        BranchInfo? current = branches.FirstOrDefault(branch => branch.IsCurrent);
        Upstream = current?.Upstream ?? "";
        AheadCount = current?.Ahead ?? 0;
        BehindCount = current?.Behind ?? 0;

        // Reflect the checked-out branch without treating it as a request to check something out.
        _suppressBranchCheckout = true;
        SelectedBranch = Branches.Contains(Branch) ? Branch : null;
        _suppressBranchCheckout = false;
    }

    /// <summary>
    ///  Works out where this repository lives online, preferring "origin" and otherwise taking the
    ///  first remote that parses into something browsable.
    /// </summary>
    private void ApplyHost(IReadOnlyList<RemoteInfo> remotes)
    {
        RemoteInfo? preferred = remotes.FirstOrDefault(remote => remote.Name == "origin") ?? remotes.FirstOrDefault();

        Host = preferred is null ? null : GitHostLinks.Parse(preferred.FetchUrl);
    }

    /// <summary>Continue, skip or abort whatever is paused, using that operation's own subcommand.</summary>
    public Task<GitOperationResult> ResolveOperationAsync(string action)
    {
        string command = Operation.CommandName;

        return command.Length == 0
            ? Task.FromResult(new GitOperationResult("Resolve", false, "Nothing is in progress."))
            : RunOperationAsync(loader => loader.ContinueOperation(command, action));
    }

    /// <summary>
    ///  Fetches in the background so the ahead/behind counts mean something.
    /// </summary>
    /// <remarks>
    ///  Those counts come from the remote-tracking refs, which only move when something fetches — so
    ///  without this the toolbar can report "up to date" indefinitely while the remote moves on.
    ///  Skipped while another operation is running, and it refreshes only the branch state rather than
    ///  reloading the commit list, so it never disturbs what is on screen.
    /// </remarks>
    public async Task AutoFetchAsync()
    {
        if (IsBusy || IsLoading)
        {
            return;
        }

        try
        {
            await EnsureGitHubCredentialAsync();
            GitOperationResult result = await Task.Run(_loader.FetchQuietly);

            // A fetch moves the remote-tracking refs, so anything read from refs is now stale.
            InvalidateListings();

            if (result.Succeeded)
            {
                await RefreshBranchesAsync(CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // A background fetch failing is not worth telling anyone about: no network, no remote,
            // or credentials needed. The next manual fetch will say so properly.
        }
    }

    private async Task LoadChangedFilesAsync(CommitRowViewModel? commit)
    {
        Cancel(ref _changedFilesCts);

        // Moving to another commit invalidates whichever file's diff was on screen.
        SelectedChangedFile = null;
        ChangedFiles.Clear();

        if (commit is null)
        {
            return;
        }

        CancellationTokenSource cts = new();
        _changedFilesCts = cts;

        try
        {
            bool isWorkingDirectory = commit.IsWorkingDirectory;
            GitCommit? revision = commit.Revision;

            // Resolved to a non-nullable local before the lambda captures it: null-state analysis does
            // not flow into a closure, so the comparison branch would not compile against it.
            Func<IReadOnlyList<GitChange>> read;

            if (isWorkingDirectory)
            {
                read = _loader.GetWorkingDirectoryChanges;
            }
            else if (CompareTarget?.Revision?.Id is string compareFrom && revision is not null)
            {
                string to = revision.Id;
                read = () => _loader.GetChangedFilesBetween(compareFrom, to, cts.Token);
            }
            else
            {
                read = () => _loader.GetChangedFiles(revision!, cts.Token);
            }

            IReadOnlyList<GitChange> files = await Task.Run(read, cts.Token);

            // The selection may have moved on while we were reading.
            if (!ReferenceEquals(SelectedCommit, commit))
            {
                return;
            }

            foreach (GitChange file in files)
            {
                ChangedFiles.Add(new ChangedFileViewModel(file, isWorkingDirectory));
            }

            // Show something straight away rather than an empty diff pane awaiting a click. When a file
            // was asked for — the way back from Blame or File history — that one, if this commit
            // touched it.
            SelectedChangedFile = (_preferredFileName is null ? null : ChangedFiles.FirstOrDefault(file => file.Name == _preferredFileName))
                ?? ChangedFiles.FirstOrDefault();
            _preferredFileName = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_changedFilesCts, cts))
            {
                _changedFilesCts = null;
            }
        }
    }

    private Task LoadHistoryDiffAsync(ChangedFileViewModel? file)
    {
        if (file is null || SelectedCommit is not CommitRowViewModel commit)
        {
            HistoryDiff.Clear();
            return Task.CompletedTask;
        }

        if (commit.IsWorkingDirectory)
        {
            return HistoryDiff.LoadAsync(
                file.Name,
                token => Task.Run(() => _loader.GetWorkingDirectoryDiff(file.Name, file.OldName, file.IsStaged, untracked: file.IsNew), token));
        }

        // As above: narrowed into a local so the closure captures a non-nullable value.
        if (CompareTarget?.Revision?.Id is string compareFrom && commit.Revision is GitCommit revision)
        {
            return HistoryDiff.LoadAsync(
                file.Name,
                token => _loader.GetDiffTextBetweenAsync(compareFrom, revision.Id, file.Name, file.OldName, token));
        }

        return HistoryDiff.LoadAsync(
            file.Name,
            token => _loader.GetDiffTextAsync(commit.Revision!, file.Name, file.OldName, token));
    }

    /// <remarks>
    ///  The token sources are deliberately not disposed: background work may still be observing the
    ///  token after cancellation, and disposing it out from under them throws ObjectDisposedException.
    ///  They hold no timer, so letting the GC take them is harmless.
    /// </remarks>
    private static void Cancel(ref CancellationTokenSource? cts)
    {
        CancellationTokenSource? previous = cts;
        cts = null;
        previous?.Cancel();
    }

    private static string GetRepositoryName(string workingDir)
    {
        string trimmed = workingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    ///  Marshals each batch the revision reader emits onto the UI thread. Batching per callback
    ///  (rather than per revision) keeps the ObservableCollection churn manageable.
    /// </summary>
    private sealed class StreamingObserver : IObserver<IReadOnlyList<GitCommit>>
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<IReadOnlyList<GitCommit>, bool> _addBatch;
        private readonly Action<bool> _onFinished;

        /// <summary>UI-thread only, so it needs no synchronisation.</summary>
        private bool _finished;

        public StreamingObserver(
            DispatcherQueue dispatcherQueue,
            CancellationToken cancellationToken,
            Func<IReadOnlyList<GitCommit>, bool> addBatch,
            Action<bool> onFinished)
        {
            _dispatcherQueue = dispatcherQueue;
            _cancellationToken = cancellationToken;
            _addBatch = addBatch;
            _onFinished = onFinished;
        }

        public void OnCompleted()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Queued behind all the batch callbacks above, so the collection is complete by now.
            _dispatcherQueue.TryEnqueue(() => Finish(limitReached: false));
        }

        public void OnError(Exception error)
        {
            // Surfaced by the awaited Task.Run instead: GetLog rethrows on the calling thread.
        }

        public void OnNext(IReadOnlyList<GitCommit> value)
        {
            if (_cancellationToken.IsCancellationRequested || value.Count == 0)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_cancellationToken.IsCancellationRequested || _finished)
                {
                    return;
                }

                if (!_addBatch(value))
                {
                    Finish(limitReached: true);
                }
            });
        }

        private void Finish(bool limitReached)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _onFinished(limitReached);
        }
    }
}
