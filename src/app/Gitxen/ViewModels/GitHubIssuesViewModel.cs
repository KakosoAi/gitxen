// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Gitxen.GitHub;
using Gitxen.Services;
using Microsoft.UI.Xaml;

namespace Gitxen.ViewModels;

/// <summary>An issue read in full, ready to show.</summary>
public sealed partial class IssueDetailViewModel
{
    public IssueDetailViewModel(IssueDetail detail, bool canWrite)
    {
        Model = detail;
        Header = new IssueRow(detail.Summary);

        // Issue templates open with guidance in HTML comments, which GitHub does not show.
        string body = HtmlComment().Replace(detail.Body, "").Trim();
        Body = body.Length > 0 ? body : "No description provided.";
        HasBody = body.Length > 0;

        Comments = [.. detail.Comments.Select(comment => new CommentRow(comment))];

        CanClose = detail.Summary.State == IssueState.Open && canWrite;
        CanReopen = detail.Summary.State == IssueState.Closed && canWrite;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    public IssueDetail Model { get; }

    public IssueRow Header { get; }

    public string Body { get; }

    public bool HasBody { get; }

    public IReadOnlyList<CommentRow> Comments { get; }

    public Visibility CommentsVisibility => GitHubStyle.If(Comments.Count > 0);

    public bool CanClose { get; }

    public bool CanReopen { get; }

    public string Opened =>
        $"{Model.Summary.Author?.Display ?? "unknown"} opened this {TimeText.Relative(Model.Summary.CreatedAt)}";
}

/// <summary>The Issues section: a list, and the selected one in full.</summary>
public sealed class GitHubIssuesViewModel : ObservableObject
{
    private static readonly string[] _states = ["open", "closed", "all"];

    private readonly GitHubRepositoryViewModel _github;
    private readonly List<IssueRow> _all = [];

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;
    private IssueRow? _selected;
    private IssueDetailViewModel? _detail;
    private int _stateIndex;
    private bool _mineOnly;
    private string _search = "";
    private bool _isLoading;
    private bool _isDetailLoading;
    private string _message = "";

    public GitHubIssuesViewModel(GitHubRepositoryViewModel github)
    {
        _github = github;
    }

    public ObservableCollection<IssueRow> Items { get; } = [];

    public IssueRow? Selected
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

    public IssueDetailViewModel? Detail
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

    /// <summary>0 open, 1 closed, 2 all.</summary>
    public int StateIndex
    {
        get => _stateIndex;
        set
        {
            if (SetProperty(ref _stateIndex, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public bool MineOnly
    {
        get => _mineOnly;
        set
        {
            if (SetProperty(ref _mineOnly, value))
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
        : Search.Length > 0 ? "No issues match that search." : $"No {(StateIndex == 2 ? "" : _states[Math.Clamp(StateIndex, 0, 2)] + " ")}issues.";

    public string CountText => Items.Count == _all.Count
        ? $"{_all.Count} issue{(_all.Count == 1 ? "" : "s")}"
        : $"{Items.Count} of {_all.Count}";

    /// <summary>Whether the repository has issues switched on; a repository can turn them off.</summary>
    public bool IssuesEnabled => _github.Repository?.HasIssues ?? true;

    public async Task LoadAsync()
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

        IsLoading = true;
        Message = "";

        try
        {
            (IReadOnlyList<Issue> items, GhResult result) = await _github.Client.ListIssuesAsync(
                _states[Math.Clamp(StateIndex, 0, _states.Length - 1)],
                AppOptions.GitHubListSize,
                MineOnly ? "@me" : "",
                cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                Message = result.Message.Length > 0 ? result.Message : "GitHub did not answer.";
                return;
            }

            int keep = Selected?.Number ?? 0;

            _all.Clear();
            _all.AddRange(items.Select(issue => new IssueRow(issue)));
            ApplyFilter();

            Selected = Items.FirstOrDefault(row => row.Number == keep) ?? Items.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();

        IEnumerable<IssueRow> shown = search.Length == 0
            ? _all
            : _all.Where(row =>
                row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.NumberText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Labels.Any(label => label.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (row.Model.Author?.Login.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        IssueRow? keep = Selected;

        Items.Clear();

        foreach (IssueRow row in shown)
        {
            Items.Add(row);
        }

        if (keep is not null && Items.Contains(keep))
        {
            Selected = keep;
        }

        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
    }

    private async Task LoadDetailAsync()
    {
        if (_detailCts is not null)
        {
            await _detailCts.CancelAsync();
        }

        if (Selected is not IssueRow row)
        {
            Detail = null;
            return;
        }

        CancellationTokenSource cts = new();
        _detailCts = cts;
        IsDetailLoading = true;

        try
        {
            (IssueDetail? detail, GhResult result) = await _github.Client.GetIssueAsync(row.Number, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            Detail = detail is null ? null : new IssueDetailViewModel(detail, _github.CanWrite);

            if (detail is null)
            {
                _github.Report(new GitOperationResult($"Read #{row.Number}", false, result.Message));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _github.Report(new GitOperationResult($"Read #{row.Number}", false, ex.Message));
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
        await LoadAsync();
        return result;
    }

    public Task<GitOperationResult> CreateAsync(string title, string body) =>
        OperateAsync("Open issue", client => client.CreateIssueAsync(title, body));

    public Task<GitOperationResult> CommentAsync(IssueRow row, string body) =>
        OperateAsync($"Comment on #{row.Number}", client => client.CommentOnIssueAsync(row.Number, body));

    /// <param name="reason"><c>completed</c> or <c>not planned</c>.</param>
    public Task<GitOperationResult> CloseAsync(IssueRow row, string reason) =>
        OperateAsync($"Close #{row.Number}", client => client.CloseIssueAsync(row.Number, reason));

    public Task<GitOperationResult> ReopenAsync(IssueRow row) =>
        OperateAsync($"Reopen #{row.Number}", client => client.ReopenIssueAsync(row.Number));
}
