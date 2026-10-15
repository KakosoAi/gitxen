// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gitxen.GitHub;

/// <summary>
///  Reads what <c>gh --json</c> prints.
/// </summary>
/// <remarks>
///  <para>
///   Written against real output rather than against gh's documentation, which lists the fields but
///   not their vocabularies. Things that differ from what one would assume:
///  </para>
///  <list type="bullet">
///   <item>
///    Pull request states and check results are UPPER case (<c>OPEN</c>, <c>SUCCESS</c>); workflow
///    run statuses and conclusions are lower case (<c>completed</c>, <c>success</c>). Every
///    comparison here ignores case for that reason.
///   </item>
///   <item>
///    A run's <c>conclusion</c> is an empty string, not null, until it has finished.
///   </item>
///   <item>
///    An issue's <c>stateReason</c> is empty while it is open, and a closed one says
///    <c>COMPLETED</c> or <c>NOT_PLANNED</c>.
///   </item>
///   <item>
///    A pull request's check list mixes two kinds of entry: <c>CheckRun</c> (a workflow job, with
///    a status and a conclusion) and <c>StatusContext</c> (an external service, with a single
///    state). Both have to be understood or a repository that uses the second kind shows no checks.
///   </item>
///   <item>
///    Asking for the check list makes a pull request listing about five times slower, so it is a
///    separate read.
///   </item>
///  </list>
///  <para>
///   Every accessor tolerates a missing or oddly-typed field and returns a neutral value: gh adds and
///   renames fields over time, and one it stops sending must not turn a whole list into an error.
///  </para>
/// </remarks>
public static class GitHubParsers
{
    // ---- Pull requests --------------------------------------------------------------------------

    public static IReadOnlyList<PullRequest> PullRequests(string json) =>
        Array(json).Select(ToPullRequest).ToList();

    public static PullRequestDetail PullRequestDetail(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new PullRequestDetail(
            ToPullRequest(root),
            Text(root, "body"),
            Items(root, "comments", ToComment),
            Items(root, "reviews", ToReview),
            Items(root, "files", ToFile),
            Checks(root),
            Items(root, "assignees", ToUser),
            Items(root, "reviewRequests", RequestedReviewer),
            Text(root, "mergeStateStatus"),
            Date(root, "mergedAt"),
            Object(root, "mergedBy") is JsonElement merger ? ToUser(merger) : null);
    }

    /// <summary>The check results of several pull requests, boiled down to one state each.</summary>
    public static IReadOnlyDictionary<int, ChecksState> ChecksByPullRequest(string json)
    {
        Dictionary<int, ChecksState> result = [];

        foreach (JsonElement pr in Array(json))
        {
            result[Int(pr, "number")] = Summarise(Checks(pr));
        }

        return result;
    }

    /// <summary>
    ///  One state for a set of checks: failing if any failed, else pending if any is still going, else
    ///  passing if at least one succeeded.
    /// </summary>
    /// <remarks>
    ///  Skipped and neutral checks count for nothing either way. A repository whose workflows mostly
    ///  skip on a given change — the sample this was written against had 690 skipped checks to 344
    ///  passing — would otherwise show a wall of "passing" for pull requests nothing actually ran on.
    /// </remarks>
    public static ChecksState Summarise(IReadOnlyList<PullRequestCheck> checks)
    {
        if (checks.Any(check => check.Outcome == CheckOutcome.Failure))
        {
            return ChecksState.Failing;
        }

        if (checks.Any(check => check.Outcome == CheckOutcome.Pending))
        {
            return ChecksState.Pending;
        }

        return checks.Any(check => check.Outcome == CheckOutcome.Success) ? ChecksState.Passing : ChecksState.None;
    }

    private static IReadOnlyList<PullRequestCheck> Checks(JsonElement pr) =>
        Items(pr, "statusCheckRollup", ToCheck);

    private static PullRequest ToPullRequest(JsonElement e) => new(
        Int(e, "number"),
        Text(e, "title"),
        Text(e, "state").ToUpperInvariant() switch
        {
            "MERGED" => PullRequestState.Merged,
            "CLOSED" => PullRequestState.Closed,
            _ => PullRequestState.Open
        },
        Bool(e, "isDraft"),
        Object(e, "author") is JsonElement author ? ToUser(author) : null,
        Text(e, "headRefName"),
        Text(e, "baseRefName"),
        Date(e, "createdAt") ?? default,
        Date(e, "updatedAt") ?? default,
        Text(e, "url"),
        Items(e, "labels", ToLabel),
        Text(e, "reviewDecision").ToUpperInvariant() switch
        {
            "APPROVED" => ReviewDecision.Approved,
            "CHANGES_REQUESTED" => ReviewDecision.ChangesRequested,
            "REVIEW_REQUIRED" => ReviewDecision.ReviewRequired,
            _ => ReviewDecision.None
        },
        Text(e, "mergeable").ToUpperInvariant() switch
        {
            "MERGEABLE" => MergeableState.Mergeable,
            "CONFLICTING" => MergeableState.Conflicting,
            _ => MergeableState.Unknown
        },
        Int(e, "additions"),
        Int(e, "deletions"),
        Int(e, "changedFiles"));

    private static PullRequestCheck ToCheck(JsonElement e)
    {
        // The two kinds of entry share almost no field names, which is why they are told apart first.
        if (Text(e, "__typename").Equals("StatusContext", StringComparison.OrdinalIgnoreCase))
        {
            return new PullRequestCheck(
                Text(e, "context"),
                "",
                Text(e, "state").ToUpperInvariant() switch
                {
                    "SUCCESS" => CheckOutcome.Success,
                    "FAILURE" or "ERROR" => CheckOutcome.Failure,
                    _ => CheckOutcome.Pending
                },
                Text(e, "targetUrl"),
                Date(e, "startedAt"),
                null);
        }

        string status = Text(e, "status").ToUpperInvariant();

        CheckOutcome outcome = status != "COMPLETED"
            ? CheckOutcome.Pending
            : Text(e, "conclusion").ToUpperInvariant() switch
            {
                "SUCCESS" => CheckOutcome.Success,
                "SKIPPED" => CheckOutcome.Skipped,
                "CANCELLED" => CheckOutcome.Cancelled,
                "NEUTRAL" => CheckOutcome.Neutral,
                _ => CheckOutcome.Failure
            };

        return new PullRequestCheck(
            Text(e, "name"),
            Text(e, "workflowName"),
            outcome,
            Text(e, "detailsUrl"),
            Date(e, "startedAt"),
            Date(e, "completedAt"));
    }

    private static PullRequestReview ToReview(JsonElement e) => new(
        Object(e, "author") is JsonElement author ? ToUser(author) : null,
        Text(e, "state").ToUpperInvariant() switch
        {
            "APPROVED" => ReviewState.Approved,
            "CHANGES_REQUESTED" => ReviewState.ChangesRequested,
            "DISMISSED" => ReviewState.Dismissed,
            "PENDING" => ReviewState.Pending,
            _ => ReviewState.Commented
        },
        Text(e, "body"),
        Date(e, "submittedAt"));

    private static PullRequestFile ToFile(JsonElement e) =>
        new(Text(e, "path"), Int(e, "additions"), Int(e, "deletions"), Text(e, "changeType"));

    /// <summary>A review request names a user or a team; either has a login-like name worth showing.</summary>
    private static string RequestedReviewer(JsonElement e) =>
        Text(e, "login") is { Length: > 0 } login ? login : Text(e, "name");

    // ---- Issues ---------------------------------------------------------------------------------

    public static IReadOnlyList<Issue> Issues(string json) => Array(json).Select(ToIssue).ToList();

    public static IssueDetail IssueDetail(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new IssueDetail(ToIssue(root), Text(root, "body"), Items(root, "comments", ToComment));
    }

    private static Issue ToIssue(JsonElement e) => new(
        Int(e, "number"),
        Text(e, "title"),
        Text(e, "state").Equals("CLOSED", StringComparison.OrdinalIgnoreCase) ? IssueState.Closed : IssueState.Open,
        Text(e, "stateReason"),
        Object(e, "author") is JsonElement author ? ToUser(author) : null,
        Items(e, "labels", ToLabel),
        Items(e, "assignees", ToUser),
        Date(e, "createdAt") ?? default,
        Date(e, "updatedAt") ?? default,
        Date(e, "closedAt"),
        Text(e, "url"),
        Object(e, "milestone") is JsonElement milestone ? Text(milestone, "title") : "");

    // ---- Actions --------------------------------------------------------------------------------

    public static IReadOnlyList<WorkflowRun> Runs(string json) => Array(json).Select(ToRun).ToList();

    public static WorkflowRunDetail RunDetail(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new WorkflowRunDetail(ToRun(root), Items(root, "jobs", ToJob));
    }

    public static IReadOnlyList<Workflow> Workflows(string json) =>
        Array(json).Select(e => new Workflow(Long(e, "id"), Text(e, "name"), Text(e, "path"), Text(e, "state"))).ToList();

    /// <summary><c>api user</c>. Null when the answer has no login.</summary>
    public static GitHubAccount? Account(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement e = document.RootElement;

        return e.ValueKind == JsonValueKind.Object && Text(e, "login") is { Length: > 0 } login
            ? new GitHubAccount(Long(e, "id"), login, Text(e, "name"))
            : null;
    }

    /// <summary>A plain JSON array of strings, such as <c>api gitignore/templates</c> returns.</summary>
    public static IReadOnlyList<string> Strings(string json) =>
        [.. Array(json).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(s => s.Length > 0)];

    /// <summary><c>api licenses</c>: key and display name of each.</summary>
    public static IReadOnlyList<LicenseInfo> Licenses(string json) =>
        [.. Array(json).Select(e => new LicenseInfo(Text(e, "key"), Text(e, "name"))).Where(l => l.Key.Length > 0)];

    /// <summary>One string property of a JSON object — a template's <c>source</c>, a licence's <c>body</c>.</summary>
    public static string Field(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, name) : "";
    }

    /// <summary>Folds a status and a conclusion into one <see cref="RunOutcome"/>.</summary>
    public static RunOutcome Outcome(string status, string conclusion)
    {
        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? RunOutcome.Running : RunOutcome.Queued;
        }

        return conclusion.ToLowerInvariant() switch
        {
            "success" => RunOutcome.Succeeded,
            "cancelled" => RunOutcome.Cancelled,
            "skipped" => RunOutcome.Skipped,
            "neutral" or "stale" => RunOutcome.Neutral,
            "action_required" => RunOutcome.ActionRequired,

            // failure, timed_out, startup_failure — and anything gh adds later, which is more likely to
            // be a new way of failing than a new way of succeeding.
            _ => RunOutcome.Failed
        };
    }

    private static WorkflowRun ToRun(JsonElement e) => new(
        Long(e, "databaseId"),
        Int(e, "number"),
        Text(e, "displayTitle") is { Length: > 0 } title ? title : Text(e, "name"),
        Text(e, "workflowName") is { Length: > 0 } workflow ? workflow : Text(e, "name"),
        Outcome(Text(e, "status"), Text(e, "conclusion")),
        Text(e, "event"),
        Text(e, "headBranch"),
        Text(e, "headSha"),
        Date(e, "createdAt") ?? default,
        Date(e, "updatedAt") ?? default,
        Date(e, "startedAt"),
        Text(e, "url"),
        Math.Max(1, Int(e, "attempt")));

    private static WorkflowJob ToJob(JsonElement e) => new(
        Long(e, "databaseId"),
        Text(e, "name"),
        Outcome(Text(e, "status"), Text(e, "conclusion")),
        Date(e, "startedAt"),
        Date(e, "completedAt"),
        Text(e, "url"),
        Items(e, "steps", ToStep));

    private static WorkflowStep ToStep(JsonElement e) => new(
        Int(e, "number"),
        Text(e, "name"),
        Outcome(Text(e, "status"), Text(e, "conclusion")),
        Date(e, "startedAt"),
        Date(e, "completedAt"));

    // ---- Logs -----------------------------------------------------------------------------------

    /// <summary>
    ///  Makes the log <c>gh run view --log</c> prints readable.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Every line arrives as <c>job&lt;tab&gt;step&lt;tab&gt;timestamp text</c>, with the job and the
    ///   step repeated on every one of thousands of lines, an ISO timestamp in front of each, and a
    ///   byte-order mark buried in the first. Read as it is, the text is mostly furniture. This prints
    ///   the job and step once, as a heading each time they change, and the text under it.
    ///  </para>
    ///  <para>
    ///   A line without two tabs is left alone: gh also prints plain messages into the same stream.
    ///  </para>
    /// </remarks>
    /// <param name="maxCharacters">
    ///  Keeps the end of the log when it is longer, since a failure is at the bottom. A run's log can
    ///  be tens of megabytes and nothing that shows text can hold that.
    /// </param>
    public static string FormatRunLog(string raw, int maxCharacters = 400_000)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        StringBuilder text = new(Math.Min(raw.Length, maxCharacters * 2));
        string lastJob = "";
        string lastStep = "";
        char byteOrderMark = (char)0xFEFF;

        foreach (string line in raw.Split('\n'))
        {
            string[] parts = line.TrimEnd('\r').Split('\t', 3);

            if (parts.Length < 3)
            {
                text.AppendLine(line.TrimEnd('\r'));
                continue;
            }

            string job = parts[0];
            string step = parts[1];
            string content = parts[2].TrimStart(byteOrderMark);

            // "2026-09-21T11:20:09.6976109Z Current runner version" -> "Current runner version".
            int space = content.IndexOf(' ');

            if (space > 19 && content[space - 1] == 'Z' && content[4] == '-' && content[10] == 'T')
            {
                content = content[(space + 1)..];
            }

            if (job != lastJob || step != lastStep)
            {
                if (text.Length > 0)
                {
                    text.AppendLine();
                }

                text.Append("=== ").Append(job).Append(" / ").Append(step.Replace("UNKNOWN STEP", "setup", StringComparison.Ordinal)).AppendLine(" ===");
                lastJob = job;
                lastStep = step;
            }

            text.AppendLine(content);
        }

        string result = text.ToString();

        return result.Length <= maxCharacters
            ? result
            : "[log shortened: showing the last " + (maxCharacters / 1000) + " thousand characters]\n\n" + result[^maxCharacters..];
    }

    // ---- Repository -----------------------------------------------------------------------------

    public static GitHubRepository Repository(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new GitHubRepository(
            Text(root, "nameWithOwner"),
            Text(root, "url"),
            Bool(root, "isPrivate"),
            Object(root, "defaultBranchRef") is JsonElement branch ? Text(branch, "name") : "",
            Text(root, "viewerPermission").ToUpperInvariant(),
            !root.TryGetProperty("hasIssuesEnabled", out JsonElement issues) || issues.ValueKind != JsonValueKind.False);
    }

    // ---- Shared ---------------------------------------------------------------------------------

    private static GitHubUser ToUser(JsonElement e) => new(Text(e, "login"), Text(e, "name"), Bool(e, "is_bot"));

    private static GitHubLabel ToLabel(JsonElement e) => new(Text(e, "name"), Text(e, "color"), Text(e, "description"));

    private static Comment ToComment(JsonElement e) => new(
        Object(e, "author") is JsonElement author ? ToUser(author) : null,
        Text(e, "body"),
        Date(e, "createdAt") ?? default,
        Text(e, "url"));

    /// <summary>The elements of a JSON array, or none for anything that is not one.</summary>
    /// <remarks>
    ///  The document is cloned out because a JsonElement is only valid while its document is, and
    ///  these outlive the parse.
    /// </remarks>
    private static List<JsonElement> Array(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray().Select(element => element.Clone())]
            : [];
    }

    private static IReadOnlyList<T> Items<T>(JsonElement parent, string name, Func<JsonElement, T> read) =>
        parent.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(read)]
            : [];

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : 0;

    private static long Long(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : 0;

    private static bool Bool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement parent, string name) =>
        Text(parent, name) is { Length: > 0 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
            && date.Year > 1
                ? date
                : null;

    /// <summary>A nested object, or null when it is missing or null — gh sends null for a deleted account.</summary>
    private static JsonElement? Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
