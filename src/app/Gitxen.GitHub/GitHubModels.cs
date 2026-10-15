// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.GitHub;

/// <summary>A licence GitHub offers for new repositories.</summary>
/// <param name="Key">Its identifier, such as <c>mit</c>.</param>
public sealed record LicenseInfo(string Key, string Name);

/// <summary>The account a client acts as: <c>api user</c>.</summary>
/// <param name="Name">The profile's display name; empty when none is set.</param>
public sealed record GitHubAccount(long Id, string Login, string Name)
{
    /// <summary>
    ///  The address GitHub gives every account for commits, which links them to the profile without
    ///  publishing a real address.
    /// </summary>
    public string NoReplyEmail => $"{Id}+{Login}@users.noreply.github.com";
}

// The shapes GitHub's data takes once gh has printed it and it has been read. Deliberately plain
// records: nothing here knows about a window, a brush or a view model, and nothing is mutable, so a
// list that has been fetched cannot change underneath the page showing it.

public sealed record GitHubUser(string Login, string Name, bool IsBot)
{
    /// <summary>The login; the display name is often empty and is only worth showing when it is not.</summary>
    public string Display => Login.Length > 0 ? Login : Name;
}

public sealed record GitHubLabel(string Name, string Color, string Description);

/// <summary>A repository as GitHub knows it.</summary>
public sealed record GitHubRepository(
    string NameWithOwner,
    string Url,
    bool IsPrivate,
    string DefaultBranch,
    string ViewerPermission,
    bool HasIssues)
{
    /// <summary>Whether the signed-in account can change things here, which decides whether to offer to.</summary>
    public bool CanWrite => ViewerPermission is "ADMIN" or "MAINTAIN" or "WRITE";
}

// ---- Pull requests ----------------------------------------------------------------------------

public enum PullRequestState
{
    Open,
    Closed,
    Merged
}

public enum ReviewDecision
{
    None,
    ReviewRequired,
    Approved,
    ChangesRequested
}

public enum MergeableState
{
    Unknown,
    Mergeable,
    Conflicting
}

/// <summary>The outcome of a set of checks, boiled down to what a list row can show.</summary>
public enum ChecksState
{
    /// <summary>No checks ran, or none has said anything either way.</summary>
    None,
    Pending,
    Passing,
    Failing
}

public sealed record PullRequest(
    int Number,
    string Title,
    PullRequestState State,
    bool IsDraft,
    GitHubUser? Author,
    string HeadBranch,
    string BaseBranch,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Url,
    IReadOnlyList<GitHubLabel> Labels,
    ReviewDecision Review,
    MergeableState Mergeable,
    int Additions,
    int Deletions,
    int ChangedFiles);

public enum CheckOutcome
{
    Pending,
    Success,
    Failure,
    Skipped,
    Cancelled,
    Neutral
}

/// <summary>One check on a pull request: a workflow job, or an external status.</summary>
public sealed record PullRequestCheck(
    string Name,
    string Workflow,
    CheckOutcome Outcome,
    string Url,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record PullRequestFile(string Path, int Additions, int Deletions, string ChangeType);

public enum ReviewState
{
    Commented,
    Approved,
    ChangesRequested,
    Dismissed,
    Pending
}

public sealed record PullRequestReview(GitHubUser? Author, ReviewState State, string Body, DateTimeOffset? SubmittedAt);

public sealed record Comment(GitHubUser? Author, string Body, DateTimeOffset CreatedAt, string Url);

/// <summary>A pull request read in full: its description, discussion, reviews, files and checks.</summary>
public sealed record PullRequestDetail(
    PullRequest Summary,
    string Body,
    IReadOnlyList<Comment> Comments,
    IReadOnlyList<PullRequestReview> Reviews,
    IReadOnlyList<PullRequestFile> Files,
    IReadOnlyList<PullRequestCheck> Checks,
    IReadOnlyList<GitHubUser> Assignees,
    IReadOnlyList<string> RequestedReviewers,
    string MergeStateStatus,
    DateTimeOffset? MergedAt,
    GitHubUser? MergedBy);

public enum MergeMethod
{
    Merge,
    Squash,
    Rebase
}

public enum ReviewAction
{
    Approve,
    RequestChanges,
    Comment
}

// ---- Issues -----------------------------------------------------------------------------------

public enum IssueState
{
    Open,
    Closed
}

public sealed record Issue(
    int Number,
    string Title,
    IssueState State,
    string StateReason,
    GitHubUser? Author,
    IReadOnlyList<GitHubLabel> Labels,
    IReadOnlyList<GitHubUser> Assignees,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string Url,
    string Milestone);

public sealed record IssueDetail(Issue Summary, string Body, IReadOnlyList<Comment> Comments);

// ---- Actions ----------------------------------------------------------------------------------

/// <summary>
///  Where a run, a job or a step stands, in one vocabulary.
/// </summary>
/// <remarks>
///  GitHub reports this as a status and a conclusion that is empty until the status is
///  <c>completed</c>, in lower case for runs and upper case for pull request checks. Folding the pair
///  into one value here is what lets every page draw the same icon for the same thing.
/// </remarks>
public enum RunOutcome
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
    Neutral,
    ActionRequired
}

public sealed record WorkflowRun(
    long Id,
    int Number,
    string Title,
    string Workflow,
    RunOutcome Outcome,
    string Event,
    string Branch,
    string Sha,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    string Url,
    int Attempt)
{
    public bool IsFinished => Outcome is not (RunOutcome.Queued or RunOutcome.Running);

    /// <summary>How long it ran, or has been running; null before it starts.</summary>
    public TimeSpan? Duration => StartedAt is null
        ? null
        : (IsFinished ? UpdatedAt : DateTimeOffset.UtcNow) - StartedAt.Value;
}

public sealed record WorkflowStep(int Number, string Name, RunOutcome Outcome, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    public TimeSpan? Duration => StartedAt is null || CompletedAt is null ? null : CompletedAt.Value - StartedAt.Value;
}

public sealed record WorkflowJob(
    long Id,
    string Name,
    RunOutcome Outcome,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Url,
    IReadOnlyList<WorkflowStep> Steps)
{
    public TimeSpan? Duration => StartedAt is null || CompletedAt is null ? null : CompletedAt.Value - StartedAt.Value;
}

public sealed record WorkflowRunDetail(WorkflowRun Run, IReadOnlyList<WorkflowJob> Jobs);

public sealed record Workflow(long Id, string Name, string Path, string State);
