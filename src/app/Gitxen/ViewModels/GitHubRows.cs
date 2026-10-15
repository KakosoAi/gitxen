// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Services;
using Gitxen.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gitxen.ViewModels;

/// <summary>
///  How GitHub's states look: one colour and one word for each, shared by every page that shows them.
/// </summary>
/// <remarks>
///  The colours come from the theme's own status brushes rather than GitHub's hard-coded greens and
///  purples, so a state reads the same as an added or deleted file does elsewhere in the app and
///  follows the theme when it changes. Those brushes are updated in place, which is why holding a
///  reference to one is enough.
/// </remarks>
internal static class GitHubStyle
{
    /// <summary>Nothing to say either way: a queued run, a skipped step, a pull request with no checks.</summary>
    public static readonly SolidColorBrush Neutral = new(Color.FromArgb(255, 139, 148, 158));

    public static Brush Good => ThemeBrushes.FileAdded;

    public static Brush Bad => ThemeBrushes.FileDeleted;

    public static Brush Warn => ThemeBrushes.FileModified;

    public static Brush Merged => ThemeBrushes.FileRenamed;

    /// <summary>A label's own colour, which is the one thing about it the repository's owner chose.</summary>
    public static (Brush Background, Brush Foreground) Label(string hex)
    {
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            // Perceived brightness, so the text is dark on a light label and light on a dark one.
            double brightness = ((0.299 * r) + (0.587 * g) + (0.114 * b)) / 255;

            return (
                new SolidColorBrush(Color.FromArgb(255, r, g, b)),
                new SolidColorBrush(brightness > 0.6 ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 255, 255, 255)));
        }

        return (Neutral, new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)));
    }

    public static string OutcomeGlyph(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => "\uE930",
        RunOutcome.Failed => "\uEB90",
        RunOutcome.Cancelled => "\uE711",
        RunOutcome.Running => "\uE895",
        RunOutcome.Queued => "\uE823",
        RunOutcome.ActionRequired => "\uE7BA",
        _ => "\uE738"
    };

    public static Brush OutcomeBrush(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => Good,
        RunOutcome.Failed => Bad,
        RunOutcome.Running or RunOutcome.Queued or RunOutcome.ActionRequired => Warn,
        _ => Neutral
    };

    public static string OutcomeText(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => "Succeeded",
        RunOutcome.Failed => "Failed",
        RunOutcome.Cancelled => "Cancelled",
        RunOutcome.Running => "Running",
        RunOutcome.Queued => "Queued",
        RunOutcome.Skipped => "Skipped",
        RunOutcome.ActionRequired => "Waiting for approval",
        _ => "Neutral"
    };

    public static Visibility If(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A label as a coloured chip.</summary>
public sealed record LabelChip(string Name, string Description, Brush Background, Brush Foreground);

// ---- Pull requests ----------------------------------------------------------------------------

/// <summary>One pull request in the list.</summary>
public sealed class PullRequestRow : ObservableObject
{
    private ChecksState _checks = ChecksState.None;
    private bool _checksKnown;

    public PullRequestRow(PullRequest pullRequest)
    {
        Model = pullRequest;

        Labels =
        [
            .. pullRequest.Labels.Select(label =>
            {
                (Brush background, Brush foreground) = GitHubStyle.Label(label.Color);
                return new LabelChip(label.Name, label.Description, background, foreground);
            })
        ];
    }

    public PullRequest Model { get; }

    public int Number => Model.Number;

    public string NumberText => $"#{Model.Number}";

    public string Title => Model.Title;

    public string Branches => $"{Model.HeadBranch} → {Model.BaseBranch}";

    public string Meta => $"{Model.Author?.Display ?? "unknown"} · updated {TimeText.Relative(Model.UpdatedAt)}";

    public string UpdatedTooltip => TimeText.Full(Model.UpdatedAt);

    public string StateText => Model.State switch
    {
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => Model.IsDraft ? "Draft" : "Open"
    };

    public Brush StateBrush => Model.State switch
    {
        PullRequestState.Merged => GitHubStyle.Merged,
        PullRequestState.Closed => GitHubStyle.Bad,
        _ => Model.IsDraft ? GitHubStyle.Neutral : GitHubStyle.Good
    };

    public string ReviewText => Model.Review switch
    {
        ReviewDecision.Approved => "Approved",
        ReviewDecision.ChangesRequested => "Changes requested",
        ReviewDecision.ReviewRequired => "Review required",
        _ => ""
    };

    public Brush ReviewBrush => Model.Review switch
    {
        ReviewDecision.Approved => GitHubStyle.Good,
        ReviewDecision.ChangesRequested => GitHubStyle.Bad,
        _ => GitHubStyle.Neutral
    };

    /// <summary>Only an open pull request is waiting on a review; on a merged one it is history.</summary>
    public Visibility ReviewVisibility =>
        GitHubStyle.If(Model.State == PullRequestState.Open && Model.Review != ReviewDecision.None);

    public string ChangeSummary => $"+{Model.Additions}  −{Model.Deletions}";

    public string FilesText => $"{Model.ChangedFiles} file{(Model.ChangedFiles == 1 ? "" : "s")}";

    public Visibility ConflictVisibility =>
        GitHubStyle.If(Model.State == PullRequestState.Open && Model.Mergeable == MergeableState.Conflicting);

    public IReadOnlyList<LabelChip> Labels { get; }

    public Visibility LabelsVisibility => GitHubStyle.If(Labels.Count > 0);

    // ---- Checks arrive after the row does ----

    /// <summary>
    ///  Records what the checks came to. Raised on the row rather than replacing it, so the list does
    ///  not flicker or lose its selection when the second, slower read lands.
    /// </summary>
    public void ApplyChecks(ChecksState state)
    {
        _checks = state;
        _checksKnown = true;

        OnPropertyChanged(nameof(ChecksGlyph));
        OnPropertyChanged(nameof(ChecksBrush));
        OnPropertyChanged(nameof(ChecksTooltip));
        OnPropertyChanged(nameof(ChecksVisibility));
    }

    public string ChecksGlyph => _checks switch
    {
        ChecksState.Passing => "\uE930",
        ChecksState.Failing => "\uEB90",
        _ => "\uE823"
    };

    public Brush ChecksBrush => _checks switch
    {
        ChecksState.Passing => GitHubStyle.Good,
        ChecksState.Failing => GitHubStyle.Bad,
        _ => GitHubStyle.Warn
    };

    public string ChecksTooltip => _checks switch
    {
        ChecksState.Passing => "Checks passed",
        ChecksState.Failing => "Checks failed",
        ChecksState.Pending => "Checks running",
        _ => ""
    };

    /// <summary>Nothing is drawn until the checks are known, and nothing if none ran.</summary>
    public Visibility ChecksVisibility => GitHubStyle.If(_checksKnown && _checks != ChecksState.None);

    /// <summary>What a list container announces for the row; without it a screen reader reads the type name.</summary>
    public override string ToString() => $"{StateText} pull request {NumberText}, {Title}, {Meta}";
}

/// <summary>A check on a pull request.</summary>
public sealed record CheckRow(PullRequestCheck Model)
{
    public string Name => Model.Name;

    public string Workflow => Model.Workflow;

    public Visibility WorkflowVisibility => GitHubStyle.If(Model.Workflow.Length > 0);

    public string Glyph => Model.Outcome switch
    {
        CheckOutcome.Success => "\uE930",
        CheckOutcome.Failure => "\uEB90",
        CheckOutcome.Cancelled => "\uE711",
        CheckOutcome.Pending => "\uE823",
        _ => "\uE738"
    };

    public Brush Brush => Model.Outcome switch
    {
        CheckOutcome.Success => GitHubStyle.Good,
        CheckOutcome.Failure => GitHubStyle.Bad,
        CheckOutcome.Pending => GitHubStyle.Warn,
        _ => GitHubStyle.Neutral
    };

    public string Result => Model.Outcome switch
    {
        CheckOutcome.Success => "Passed",
        CheckOutcome.Failure => "Failed",
        CheckOutcome.Pending => "Running",
        CheckOutcome.Skipped => "Skipped",
        CheckOutcome.Cancelled => "Cancelled",
        _ => "Neutral"
    };

    /// <summary>The check's page on GitHub, as a Uri because that is what a hyperlink binds to.</summary>
    public Uri? Link => Uri.TryCreate(Model.Url, UriKind.Absolute, out Uri? link) ? link : null;
}

/// <summary>A review left on a pull request.</summary>
public sealed record ReviewRow(PullRequestReview Model)
{
    public string Author => Model.Author?.Display ?? "unknown";

    public string Verdict => Model.State switch
    {
        ReviewState.Approved => "approved",
        ReviewState.ChangesRequested => "requested changes",
        ReviewState.Dismissed => "review dismissed",
        ReviewState.Pending => "started a review",
        _ => "commented"
    };

    public Brush Brush => Model.State switch
    {
        ReviewState.Approved => GitHubStyle.Good,
        ReviewState.ChangesRequested => GitHubStyle.Bad,
        _ => GitHubStyle.Neutral
    };

    public string When => Model.SubmittedAt is DateTimeOffset at ? TimeText.Relative(at) : "";

    public string Body => Model.Body;

    public Visibility BodyVisibility => GitHubStyle.If(Model.Body.Trim().Length > 0);
}

/// <summary>A comment on a pull request or an issue.</summary>
public sealed record CommentRow(Comment Model)
{
    public string Author => Model.Author?.Display ?? "unknown";

    public string When => TimeText.Relative(Model.CreatedAt);

    public string WhenTooltip => TimeText.Full(Model.CreatedAt);

    public string Body => Model.Body;
}

/// <summary>A file a pull request changes.</summary>
public sealed record FileRow(PullRequestFile Model)
{
    public string Path => Model.Path;

    public string FileName => Model.Path[(Model.Path.LastIndexOf('/') + 1)..];

    public string Directory => Model.Path.LastIndexOf('/') is int slash and > 0 ? Model.Path[..slash] : "";

    public string Change => $"+{Model.Additions}  −{Model.Deletions}";

    public string Marker => Model.ChangeType.ToUpperInvariant() switch
    {
        "ADDED" => "A",
        "DELETED" => "D",
        "RENAMED" => "R",
        _ => "M"
    };

    public Brush MarkerBrush => Model.ChangeType.ToUpperInvariant() switch
    {
        "ADDED" => ThemeBrushes.FileAdded,
        "DELETED" => ThemeBrushes.FileDeleted,
        "RENAMED" => ThemeBrushes.FileRenamed,
        _ => ThemeBrushes.FileModified
    };

    // A record would otherwise announce its whole property dump.
    public override string ToString() => $"{Path}, {Change}";
}

// ---- Issues -----------------------------------------------------------------------------------

/// <summary>One issue in the list.</summary>
public sealed class IssueRow
{
    public IssueRow(Issue issue)
    {
        Model = issue;

        Labels =
        [
            .. issue.Labels.Select(label =>
            {
                (Brush background, Brush foreground) = GitHubStyle.Label(label.Color);
                return new LabelChip(label.Name, label.Description, background, foreground);
            })
        ];
    }

    public Issue Model { get; }

    public int Number => Model.Number;

    public string NumberText => $"#{Model.Number}";

    public string Title => Model.Title;

    public string Meta => $"{Model.Author?.Display ?? "unknown"} · updated {TimeText.Relative(Model.UpdatedAt)}";

    public string UpdatedTooltip => TimeText.Full(Model.UpdatedAt);

    /// <summary>"Open", "Completed" or "Not planned": how it ended matters as much as that it did.</summary>
    public string StateText => Model.State == IssueState.Open
        ? "Open"
        : Model.StateReason.Equals("NOT_PLANNED", StringComparison.OrdinalIgnoreCase) ? "Not planned" : "Completed";

    public Brush StateBrush => Model.State == IssueState.Open
        ? GitHubStyle.Good
        : Model.StateReason.Equals("NOT_PLANNED", StringComparison.OrdinalIgnoreCase) ? GitHubStyle.Neutral : GitHubStyle.Merged;

    public IReadOnlyList<LabelChip> Labels { get; }

    public Visibility LabelsVisibility => GitHubStyle.If(Labels.Count > 0);

    public string Assignees => string.Join(", ", Model.Assignees.Select(user => user.Display));

    public Visibility AssigneesVisibility => GitHubStyle.If(Model.Assignees.Count > 0);

    public string Milestone => Model.Milestone;

    public Visibility MilestoneVisibility => GitHubStyle.If(Model.Milestone.Length > 0);

    public override string ToString() => $"{StateText} issue {NumberText}, {Title}, {Meta}";
}

// ---- Actions ----------------------------------------------------------------------------------

/// <summary>One workflow run in the list.</summary>
public sealed class RunRow : ObservableObject
{
    public RunRow(WorkflowRun run)
    {
        Model = run;
    }

    public WorkflowRun Model { get; }

    public long Id => Model.Id;

    public string Title => Model.Title;

    public string Workflow => Model.Workflow;

    public string Meta => $"{Model.Workflow} #{Model.Number} · {Model.Event} · {Model.Branch}";

    public string Age => TimeText.Relative(Model.CreatedAt);

    public string AgeTooltip => TimeText.Full(Model.CreatedAt);

    public string Glyph => GitHubStyle.OutcomeGlyph(Model.Outcome);

    public Brush Brush => GitHubStyle.OutcomeBrush(Model.Outcome);

    public string OutcomeText => GitHubStyle.OutcomeText(Model.Outcome) + (Model.Attempt > 1 ? $" · attempt {Model.Attempt}" : "");

    public string DurationText => Model.Duration is TimeSpan duration ? TimeText.Duration(duration) : "";

    public string ShortSha => Model.Sha.Length > 7 ? Model.Sha[..7] : Model.Sha;

    public override string ToString() => $"{OutcomeText}, {Title}, {Meta}";
}

/// <summary>A job in a run, with its steps.</summary>
public sealed record JobRow(WorkflowJob Model)
{
    public string Name => Model.Name;

    public string Glyph => GitHubStyle.OutcomeGlyph(Model.Outcome);

    public Brush Brush => GitHubStyle.OutcomeBrush(Model.Outcome);

    public string DurationText => Model.Duration is TimeSpan duration ? TimeText.Duration(duration) : "";

    public IReadOnlyList<StepRow> Steps { get; } = [.. Model.Steps.Select(step => new StepRow(step))];

    public string Url => Model.Url;
}

/// <summary>A step in a job.</summary>
public sealed record StepRow(WorkflowStep Model)
{
    public string Name => Model.Name;

    public string Glyph => GitHubStyle.OutcomeGlyph(Model.Outcome);

    public Brush Brush => GitHubStyle.OutcomeBrush(Model.Outcome);

    public string DurationText => Model.Duration is TimeSpan duration ? TimeText.Duration(duration) : "";
}
