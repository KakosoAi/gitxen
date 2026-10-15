// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Gitxen.GitHub;
using Gitxen.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gitxen.ViewModels;

/// <summary>
///  A pull request read in full, ready to show.
/// </summary>
public sealed partial class PullRequestDetailViewModel
{
    public PullRequestDetailViewModel(PullRequestDetail detail, bool canWrite)
    {
        Model = detail;
        Header = new PullRequestRow(detail.Summary);

        // The description often opens with an HTML comment from the repository's template — guidance
        // to the author, invisible on GitHub. Shown as text it is a paragraph of noise above the words
        // that matter.
        string body = HtmlComment().Replace(detail.Body, "").Trim();
        Body = body.Length > 0 ? body : "No description provided.";
        HasBody = body.Length > 0;

        Checks = [.. detail.Checks.Select(check => new CheckRow(check))];
        Reviews = [.. detail.Reviews.Where(review => review.State != ReviewState.Pending).Select(review => new ReviewRow(review))];
        Comments = [.. detail.Comments.Select(comment => new CommentRow(comment))];
        Files = [.. detail.Files.Select(file => new FileRow(file))];

        PullRequest pr = detail.Summary;
        bool open = pr.State == PullRequestState.Open;

        CanCheckout = true;
        CanMerge = open && !pr.IsDraft && canWrite;
        CanMarkReady = open && pr.IsDraft && canWrite;
        CanClose = open && canWrite;
        CanReopen = pr.State == PullRequestState.Closed && canWrite;
        CanReview = open;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    public PullRequestDetail Model { get; }

    /// <summary>The list's own row, which already knows how to say the state and draw the labels.</summary>
    public PullRequestRow Header { get; }

    public string Body { get; }

    public bool HasBody { get; }

    public IReadOnlyList<CheckRow> Checks { get; }

    public IReadOnlyList<ReviewRow> Reviews { get; }

    public IReadOnlyList<CommentRow> Comments { get; }

    public IReadOnlyList<FileRow> Files { get; }

    public bool CanCheckout { get; }

    public bool CanMerge { get; }

    public bool CanMarkReady { get; }

    public bool CanClose { get; }

    public bool CanReopen { get; }

    public bool CanReview { get; }

    public string Author => Model.Summary.Author?.Display ?? "unknown";

    public string Opened => $"opened {TimeText.Relative(Model.Summary.CreatedAt)}";

    /// <summary>"alice wants to merge feature into main", or how it ended.</summary>
    public string Sentence => Model.Summary.State switch
    {
        PullRequestState.Merged => $"{Model.MergedBy?.Display ?? Author} merged {Model.Summary.HeadBranch} into {Model.Summary.BaseBranch} {(Model.MergedAt is DateTimeOffset at ? TimeText.Relative(at) : "")}".TrimEnd(),
        PullRequestState.Closed => $"{Author} closed this without merging {Model.Summary.HeadBranch}",
        _ => $"{Author} wants to merge {Model.Summary.HeadBranch} into {Model.Summary.BaseBranch}"
    };

    /// <summary>"3 passed · 1 failed · 2 running": what the checks came to, in words.</summary>
    public string ChecksSummary
    {
        get
        {
            if (Model.Checks.Count == 0)
            {
                return "No checks";
            }

            List<string> parts = [];
            AddCount(parts, CheckOutcome.Success, "passed");
            AddCount(parts, CheckOutcome.Failure, "failed");
            AddCount(parts, CheckOutcome.Pending, "running");
            AddCount(parts, CheckOutcome.Skipped, "skipped");
            AddCount(parts, CheckOutcome.Cancelled, "cancelled");

            return string.Join(" · ", parts);
        }
    }

    public Visibility ChecksVisibility => GitHubStyle.If(Checks.Count > 0);

    public Visibility ReviewsVisibility => GitHubStyle.If(Reviews.Count > 0);

    public Visibility CommentsVisibility => GitHubStyle.If(Comments.Count > 0);

    /// <summary>What GitHub says stands between this and a merge, in plain words.</summary>
    public string MergeStatus => Model.Summary.State != PullRequestState.Open
        ? ""
        : Model.MergeStateStatus.ToUpperInvariant() switch
        {
            "CLEAN" => "Ready to merge",
            "DIRTY" => "Has merge conflicts",
            "BLOCKED" => "Blocked — a required review or check is missing",
            "BEHIND" => "Behind the base branch",
            "UNSTABLE" => "Some checks are not passing",
            "DRAFT" => "Still a draft",
            "HAS_HOOKS" => "Ready to merge",
            _ => ""
        };

    public Visibility MergeStatusVisibility => GitHubStyle.If(MergeStatus.Length > 0);

    public Brush MergeStatusBrush => Model.MergeStateStatus.ToUpperInvariant() switch
    {
        "CLEAN" or "HAS_HOOKS" => GitHubStyle.Good,
        "DIRTY" => GitHubStyle.Bad,
        _ => GitHubStyle.Warn
    };

    public string Reviewers => Model.RequestedReviewers.Count == 0
        ? ""
        : "Review requested from " + string.Join(", ", Model.RequestedReviewers);

    public Visibility ReviewersVisibility => GitHubStyle.If(Reviewers.Length > 0);

    private void AddCount(List<string> parts, CheckOutcome outcome, string word)
    {
        int count = Model.Checks.Count(check => check.Outcome == outcome);

        if (count > 0)
        {
            parts.Add($"{count} {word}");
        }
    }
}

/// <summary>
///  The Pull requests section: a list, and the selected one in full.
/// </summary>
public sealed class GitHubPullRequestsViewModel : ObservableObject
{
    private static readonly string[] _states = ["open", "closed", "merged", "all"];

    private readonly GitHubRepositoryViewModel _github;
    private readonly List<PullRequestRow> _all = [];

    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _detailCts;
    private PullRequestRow? _selected;
    private PullRequestDetailViewModel? _detail;
    private int _stateIndex;
    private bool _mineOnly;
    private string _search = "";
    private bool _isLoading;
    private bool _isDetailLoading;
    private string _message = "";

    /// <summary>The whole diff of the selected pull request, and the number it belongs to.</summary>
    private string _diffText = "";

    private int _diffFor;

    public GitHubPullRequestsViewModel(GitHubRepositoryViewModel github)
    {
        _github = github;
    }

    public ObservableCollection<PullRequestRow> Items { get; } = [];

    /// <summary>The selected file's diff, drawn by the same panel the History page uses.</summary>
    public DiffViewModel Diff { get; } = new();

    public PullRequestRow? Selected
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

    public PullRequestDetailViewModel? Detail
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

    /// <summary>0 open, 1 closed, 2 merged, 3 all. Changing it re-reads the list from GitHub.</summary>
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

    /// <summary>Only pull requests the signed-in account opened.</summary>
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

    /// <summary>Filters what has been read. Applied here rather than by GitHub: it is instant, and the list is a page anyway.</summary>
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

    /// <summary>What to say when there is nothing to list: an error, or that there is simply nothing.</summary>
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
        : Search.Length > 0 ? "No pull requests match that search." : $"No {StateWord} pull requests.";

    private string StateWord => _states[Math.Clamp(StateIndex, 0, _states.Length - 1)] is "all" ? "" : _states[Math.Clamp(StateIndex, 0, _states.Length - 1)];

    public string CountText => Items.Count == _all.Count
        ? $"{_all.Count} pull request{(_all.Count == 1 ? "" : "s")}"
        : $"{Items.Count} of {_all.Count}";

    // ---- Reading ----

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
            string state = _states[Math.Clamp(StateIndex, 0, _states.Length - 1)];
            string author = MineOnly ? "@me" : "";

            (IReadOnlyList<PullRequest> items, GhResult result) = await _github.Client.ListPullRequestsAsync(state, AppOptions.GitHubListSize, author, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                // The previous list stays where it was: a network blip should not empty the page.
                Message = result.Message.Length > 0 ? result.Message : "GitHub did not answer.";
                return;
            }

            int keep = Selected?.Number ?? 0;

            _all.Clear();
            _all.AddRange(items.Select(pr => new PullRequestRow(pr)));
            ApplyFilter();

            // Something on screen straight away rather than an empty detail pane awaiting a click.
            Selected = Items.FirstOrDefault(row => row.Number == keep) ?? Items.FirstOrDefault();

            // The check results are a second, slower read. The list is already up; the dots arrive
            // when they do.
            _ = LoadChecksAsync(state, author, cts.Token);
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

    private async Task LoadChecksAsync(string state, string author, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyDictionary<int, ChecksState> checks = await _github.Client.GetPullRequestChecksAsync(state, AppOptions.GitHubListSize, author, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (PullRequestRow row in _all)
            {
                if (checks.TryGetValue(row.Number, out ChecksState value))
                {
                    row.ApplyChecks(value);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Text.Json.JsonException)
        {
            // Dots that never arrive are a small loss; the list itself is already there.
        }
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();

        IEnumerable<PullRequestRow> shown = search.Length == 0
            ? _all
            : _all.Where(row =>
                row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.NumberText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Branches.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (row.Model.Author?.Login.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        PullRequestRow? keep = Selected;

        Items.Clear();

        foreach (PullRequestRow row in shown)
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

        if (Selected is not PullRequestRow row)
        {
            Detail = null;
            Diff.Clear();
            return;
        }

        CancellationTokenSource cts = new();
        _detailCts = cts;
        IsDetailLoading = true;

        try
        {
            (PullRequestDetail? detail, GhResult result) = await _github.Client.GetPullRequestAsync(row.Number, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            Diff.Clear();
            _diffFor = 0;
            Detail = detail is null ? null : new PullRequestDetailViewModel(detail, _github.CanWrite);

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

    /// <summary>
    ///  Shows one file's part of the pull request's diff.
    /// </summary>
    /// <remarks>
    ///  GitHub gives the whole diff in one piece, so it is read once per pull request and cut up here.
    ///  The pieces are found by the file's path at the end of each <c>diff --git a/x b/x</c> header
    ///  rather than by parsing the header, since a path with a space in it makes that ambiguous.
    /// </remarks>
    public async Task ShowFileDiffAsync(FileRow? file)
    {
        if (file is null || Selected is not PullRequestRow row)
        {
            Diff.Clear();
            return;
        }

        if (_diffFor != row.Number)
        {
            GhResult? result = await _github.RunRawAsync(client => client.GetPullRequestDiffAsync(row.Number));

            if (result is not { Succeeded: true })
            {
                _github.Report(new GitOperationResult($"Read the diff of #{row.Number}", false, result?.Message ?? _github.GateMessage));
                return;
            }

            _diffText = result.Output;
            _diffFor = row.Number;
        }

        string block = ExtractFile(_diffText, file.Path);

        await Diff.LoadAsync(file.Path, _ => Task.FromResult(block.Length > 0 ? block : "Binary file, or no textual changes."));
    }

    /// <summary>The part of a unified diff that belongs to one file, header included.</summary>
    public static string ExtractFile(string diff, string path)
    {
        StringBuilder block = new();
        bool inside = false;

        foreach (string line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = line.EndsWith(" b/" + path, StringComparison.Ordinal);
            }

            if (inside)
            {
                block.Append(line).Append('\n');
            }
        }

        return block.ToString();
    }

    // ---- Doing things ----

    private async Task<GitOperationResult> OperateAsync(string description, Func<GitHubClient, Task<GhResult>> operation, bool changesRepository = false)
    {
        GitOperationResult result = await _github.RunAsync(description, operation);

        _github.Report(result);

        if (changesRepository)
        {
            await _github.RefreshRepositoryAsync();
        }

        await LoadAsync();
        return result;
    }

    public Task<GitOperationResult> CheckoutAsync(PullRequestRow row) =>
        OperateAsync($"Check out #{row.Number}", client => client.CheckoutPullRequestAsync(row.Number), changesRepository: true);

    public Task<GitOperationResult> MergeAsync(PullRequestRow row, MergeMethod method, bool deleteBranch) =>
        OperateAsync($"Merge #{row.Number}", client => client.MergePullRequestAsync(row.Number, method, deleteBranch), changesRepository: deleteBranch);

    public Task<GitOperationResult> CloseAsync(PullRequestRow row) =>
        OperateAsync($"Close #{row.Number}", client => client.ClosePullRequestAsync(row.Number));

    public Task<GitOperationResult> ReopenAsync(PullRequestRow row) =>
        OperateAsync($"Reopen #{row.Number}", client => client.ReopenPullRequestAsync(row.Number));

    public Task<GitOperationResult> MarkReadyAsync(PullRequestRow row) =>
        OperateAsync($"Mark #{row.Number} ready for review", client => client.MarkPullRequestReadyAsync(row.Number));

    public Task<GitOperationResult> CommentAsync(PullRequestRow row, string body) =>
        OperateAsync($"Comment on #{row.Number}", client => client.CommentOnPullRequestAsync(row.Number, body));

    public Task<GitOperationResult> ReviewAsync(PullRequestRow row, ReviewAction action, string body) =>
        OperateAsync($"Review #{row.Number}", client => client.ReviewPullRequestAsync(row.Number, action, body));

    public Task<GitOperationResult> CreateAsync(string title, string body, string baseBranch, string head, bool draft) =>
        OperateAsync("Open pull request", client => client.CreatePullRequestAsync(title, body, baseBranch, head, draft));
}
