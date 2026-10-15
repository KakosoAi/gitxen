// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Gitxen.GitHub;
using Gitxen.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Gitxen.ViewModels;

/// <summary>A run read in full: its jobs, and what can be done to it.</summary>
public sealed class RunDetailViewModel
{
    public RunDetailViewModel(WorkflowRunDetail detail, bool canWrite)
    {
        Model = detail;
        Header = new RunRow(detail.Run);
        Jobs = [.. detail.Jobs.Select(job => new JobRow(job))];

        bool finished = detail.Run.IsFinished;
        bool hasJobs = detail.Jobs.Count > 0;

        CanRerun = finished && canWrite;
        CanRerunFailed = finished && canWrite && detail.Jobs.Any(job => job.Outcome == RunOutcome.Failed);
        CanCancel = !finished && canWrite;

        // A run that fails before any job starts — a workflow file that will not parse, a trigger
        // GitHub refused — has no jobs and no log at all. Offering to show one would only produce an
        // error, so the buttons are not offered and the page says what happened instead.
        CanViewLog = hasJobs;
        CanViewFailedLog = hasJobs && detail.Jobs.Any(job => job.Outcome == RunOutcome.Failed);
    }

    public WorkflowRunDetail Model { get; }

    public RunRow Header { get; }

    public IReadOnlyList<JobRow> Jobs { get; }

    public bool CanRerun { get; }

    public bool CanRerunFailed { get; }

    public bool CanCancel { get; }

    public bool CanViewLog { get; }

    public bool CanViewFailedLog { get; }

    public Visibility JobsVisibility => GitHubStyle.If(Jobs.Count > 0);

    /// <summary>Explains an empty run rather than leaving a blank where the jobs should be.</summary>
    public Visibility NoJobsVisibility => GitHubStyle.If(Jobs.Count == 0);

    public string NoJobsText => Model.Run.IsFinished
        ? Model.Run.Outcome == RunOutcome.Failed
            ? "This run failed before any job started, so there is no log. This usually means the workflow file itself was rejected — a syntax error, or a trigger or permission GitHub would not accept."
            : "This run has no jobs."
        : "Waiting for a runner. Jobs appear here as they start.";
}

/// <summary>The Actions section: workflow runs, and the selected one's jobs.</summary>
public sealed class GitHubActionsViewModel : ObservableObject
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    private readonly GitHubRepositoryViewModel _github;
    private readonly List<RunRow> _all = [];
    private readonly DispatcherQueueTimer? _timer;

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;
    private RunRow? _selected;
    private RunDetailViewModel? _detail;
    private bool _thisBranchOnly;
    private string _search = "";
    private bool _isLoading;
    private bool _isDetailLoading;
    private string _message = "";

    public GitHubActionsViewModel(GitHubRepositoryViewModel github)
    {
        _github = github;

        // Created here, on the UI thread the tab is built on. It ticks for as long as the tab lives,
        // but does nothing unless the page is showing and something is actually running.
        _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();

        if (_timer is not null)
        {
            _timer.Interval = _pollInterval;
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    /// <summary>Set by the page: whether it is the one on screen. Polling is pointless for a page nobody is looking at.</summary>
    public Func<bool>? IsPageVisible { get; set; }

    public ObservableCollection<RunRow> Items { get; } = [];

    public RunRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionVisibility));
                OnPropertyChanged(nameof(NoSelectionVisibility));

                _ = LoadDetailAsync();
            }
        }
    }

    public RunDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(DetailVisibility));
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public Visibility SelectionVisibility => GitHubStyle.If(HasSelection);

    public Visibility NoSelectionVisibility => GitHubStyle.If(!HasSelection && !IsLoading);

    public Visibility DetailVisibility => GitHubStyle.If(Detail is not null);

    /// <summary>Only runs on the branch that is checked out.</summary>
    public bool ThisBranchOnly
    {
        get => _thisBranchOnly;
        set
        {
            if (SetProperty(ref _thisBranchOnly, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(NoSelectionVisibility));
                OnPropertyChanged(nameof(EmptyVisibility));
            }
        }
    }

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        private set => SetProperty(ref _isDetailLoading, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(EmptyVisibility));
            }
        }
    }

    public Visibility EmptyVisibility => GitHubStyle.If(Items.Count == 0 && !IsLoading);

    public string EmptyText => Message.Length > 0
        ? Message
        : Search.Length > 0 ? "No runs match that search."
        : ThisBranchOnly ? $"No workflow runs on {_github.Tab.Branch}." : "No workflow runs yet.";

    public string CountText => Items.Count == _all.Count
        ? $"{_all.Count} run{(_all.Count == 1 ? "" : "s")}"
        : $"{Items.Count} of {_all.Count}";

    /// <summary>Whether anything on the page is still going, which is what makes polling worthwhile.</summary>
    private bool AnythingRunning => _all.Any(row => !row.Model.IsFinished);

    public void Stop() => _timer?.Stop();

    // ---- Reading ----

    /// <param name="quiet">A background refresh: no spinner, and an error is not worth interrupting anyone for.</param>
    public async Task LoadAsync(bool quiet = false)
    {
        if (!await _github.EnsureReadyAsync())
        {
            return;
        }

        if (_listCts is not null)
        {
            await _listCts.CancelAsync();
        }

        CancellationTokenSource cts = new();
        _listCts = cts;

        if (!quiet)
        {
            IsLoading = true;
            Message = "";
        }

        try
        {
            string branch = ThisBranchOnly ? _github.Tab.Branch : "";

            (IReadOnlyList<WorkflowRun> items, GhResult result) = await _github.Client.ListRunsAsync(AppOptions.GitHubListSize, branch, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                if (!quiet)
                {
                    Message = result.Message.Length > 0 ? result.Message : "GitHub did not answer.";
                }

                return;
            }

            long keep = Selected?.Id ?? 0;

            _all.Clear();
            _all.AddRange(items.Select(run => new RunRow(run)));
            ApplyFilter();

            RunRow? again = Items.FirstOrDefault(row => row.Id == keep);

            if (again is not null && Selected is not null && !ReferenceEquals(Selected, again))
            {
                // The same run, freshly read. Swapping the selection would re-read its jobs, which
                // is exactly what a polling refresh of a running run is for.
                _selected = again;
                OnPropertyChanged(nameof(Selected));
                _ = LoadDetailAsync(quiet: true);
            }
            else if (Selected is null)
            {
                Selected = Items.FirstOrDefault();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                Message = ex.Message;
            }
        }
        finally
        {
            if (!quiet && ReferenceEquals(_listCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>A timer tick has nowhere to return a task to, so it is the one place an async void belongs.</summary>
    private async void OnTick(DispatcherQueueTimer sender, object args) => await PollAsync();

    private async Task PollAsync()
    {
        if (IsPageVisible?.Invoke() != true || _github.Gate != GitHubGate.Ready || !AnythingRunning || IsLoading)
        {
            return;
        }

        await LoadAsync(quiet: true);
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();

        IEnumerable<RunRow> shown = search.Length == 0
            ? _all
            : _all.Where(row =>
                row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Workflow.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Model.Branch.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Model.Event.Contains(search, StringComparison.OrdinalIgnoreCase));

        long keep = Selected?.Id ?? 0;

        Items.Clear();

        foreach (RunRow row in shown)
        {
            Items.Add(row);
        }

        if (keep != 0 && Items.FirstOrDefault(row => row.Id == keep) is RunRow same)
        {
            _selected = same;
            OnPropertyChanged(nameof(Selected));
        }

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
    }

    private async Task LoadDetailAsync(bool quiet = false)
    {
        if (_detailCts is not null)
        {
            await _detailCts.CancelAsync();
        }

        if (Selected is not RunRow row)
        {
            Detail = null;
            return;
        }

        CancellationTokenSource cts = new();
        _detailCts = cts;

        if (!quiet)
        {
            IsDetailLoading = true;
        }

        try
        {
            (WorkflowRunDetail? detail, GhResult result) = await _github.Client.GetRunAsync(row.Id, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (detail is not null)
            {
                Detail = new RunDetailViewModel(detail, _github.CanWrite);
            }
            else if (!quiet)
            {
                Detail = null;
                _github.Report(new GitOperationResult($"Read run {row.Id}", false, result.Message));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                _github.Report(new GitOperationResult($"Read run {row.Id}", false, ex.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                IsDetailLoading = false;
            }
        }
    }

    // ---- Doing things ----

    private async Task<GitOperationResult> OperateAsync(string description, Func<GitHubClient, Task<GhResult>> operation)
    {
        GitOperationResult result = await _github.RunAsync(description, operation);

        _github.Report(result);

        // A re-run starts asynchronously on GitHub's side; a moment for it to appear in the list.
        await Task.Delay(1500);
        await LoadAsync(quiet: true);
        return result;
    }

    public Task<GitOperationResult> RerunAsync(RunRow row, bool failedOnly) =>
        OperateAsync(failedOnly ? $"Re-run failed jobs of run {row.Model.Number}" : $"Re-run {row.Model.Number}", client => client.RerunAsync(row.Id, failedOnly));

    public Task<GitOperationResult> CancelAsync(RunRow row) =>
        OperateAsync($"Cancel run {row.Model.Number}", client => client.CancelRunAsync(row.Id));

    public Task<GitOperationResult> RunWorkflowAsync(string workflow, string branch) =>
        OperateAsync($"Run {workflow}", client => client.RunWorkflowAsync(workflow, branch));

    /// <summary>A run's log, cleaned up for reading; empty text with a reason when there is none.</summary>
    public async Task<(bool Ok, string Text)> GetLogAsync(RunRow row, bool failedOnly)
    {
        GhResult? result = await _github.RunRawAsync(client => client.GetRunLogAsync(row.Id, failedOnly));

        if (result is not { Succeeded: true })
        {
            return (false, result?.Message ?? _github.GateMessage);
        }

        return (true, GitHubParsers.FormatRunLog(result.Output));
    }

    /// <summary>The workflows the repository has, for the Run workflow picker.</summary>
    public async Task<IReadOnlyList<Workflow>> GetWorkflowsAsync()
    {
        if (!await _github.EnsureReadyAsync())
        {
            return [];
        }

        (IReadOnlyList<Workflow> items, _) = await _github.Client.ListWorkflowsAsync();
        return items;
    }
}
