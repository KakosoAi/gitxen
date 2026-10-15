// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Gitxen.GitHub;

/// <summary>Whether the GitHub CLI can be used at all, and if not, why not.</summary>
public enum GhAvailability
{
    /// <summary>gh could not be started: not installed, or not where it was said to be.</summary>
    NotInstalled,

    /// <summary>gh runs but has no signed-in account for github.com.</summary>
    NotSignedIn,

    Ready
}

/// <param name="Availability">Whether it can be used.</param>
/// <param name="Version">For example <c>2.96.0</c>; empty when gh is not installed.</param>
/// <param name="Login">The account gh is signed in as; empty unless <see cref="GhAvailability.Ready"/>.</param>
/// <param name="Detail">What gh said, for showing when something is wrong.</param>
public sealed record GhStatus(GhAvailability Availability, string Version, string Login, string Detail)
{
    public bool IsReady => Availability == GhAvailability.Ready;

    /// <summary>Every account signed in for github.com, the active one (<see cref="Login"/>) first.</summary>
    public IReadOnlyList<string> Accounts { get; init; } = [];
}

/// <summary>
///  Pull requests, issues and workflow runs for one repository, through <c>gh</c>.
/// </summary>
/// <remarks>
///  <para>
///   Every command names its repository with <c>-R</c> when one is known. Left to itself gh works the
///   repository out from the remotes, and when there are several — <c>origin</c> and <c>upstream</c>,
///   the normal shape of a fork — it wants to ask which one, which it cannot do here and reports as an
///   error instead. Naming it is what makes the answer the same one the rest of the app is showing.
///  </para>
///  <para>
///   Nothing here throws for a failed command: each returns the <see cref="GhResult"/> so the page can
///   show what gh said. Parsing failures are the exception, and are left to the caller to catch, since
///   they mean gh printed something this was not written for.
///  </para>
/// </remarks>
public sealed class GitHubClient
{
    private const string PullRequestListFields =
        "number,title,state,isDraft,author,headRefName,baseRefName,createdAt,updatedAt,url,labels,reviewDecision,mergeable,additions,deletions,changedFiles";

    private const string PullRequestDetailFields =
        PullRequestListFields + ",body,comments,reviews,files,statusCheckRollup,assignees,reviewRequests,mergeStateStatus,mergedAt,mergedBy";

    private const string IssueListFields =
        "number,title,state,stateReason,author,labels,assignees,createdAt,updatedAt,closedAt,url,milestone";

    private const string RunFields =
        "databaseId,number,displayTitle,name,workflowName,status,conclusion,event,headBranch,headSha,createdAt,updatedAt,startedAt,url,attempt";

    private readonly GhCli _gh;
    private readonly string _repository;

    /// <param name="gh">The runner, positioned in the repository's working directory.</param>
    /// <param name="repository"><c>owner/name</c>, or empty to let gh work it out.</param>
    public GitHubClient(GhCli gh, string repository = "")
    {
        _gh = gh ?? throw new ArgumentNullException(nameof(gh));
        _repository = repository;
    }

    // ---- The tool itself ------------------------------------------------------------------------

    /// <summary>Whether gh is installed and signed in, worked out from two commands.</summary>
    public static async Task<GhStatus> CheckAsync(GhCli gh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gh);

        GhResult version = await gh.RunAsync(["--version"], timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!version.WasLaunched)
        {
            return new GhStatus(GhAvailability.NotInstalled, "", "", version.Error);
        }

        // "gh version 2.96.0 (2026-07-02)" on the first line.
        string number = version.Output.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 2 } parts
            ? parts[2]
            : "";

        // --json is recent. An older gh rejects it, and the plain form's exit code says the same thing
        // (zero when signed in), so fall back to that and ask the API who it is.
        GhResult json = await gh.RunAsync(["auth", "status", "--json", "hosts"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (json.Succeeded)
        {
            return ActiveAccount(json.Output) is { Length: > 0 } login
                ? new GhStatus(GhAvailability.Ready, number, login, "") { Accounts = SignedInAccounts(json.Output) }
                : new GhStatus(GhAvailability.NotSignedIn, number, "", "gh has no signed-in account for github.com.");
        }

        if (json.Error.Contains("unknown flag", StringComparison.OrdinalIgnoreCase))
        {
            GhResult plain = await gh.RunAsync(["auth", "status"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!plain.Succeeded)
            {
                return new GhStatus(GhAvailability.NotSignedIn, number, "", plain.AllOutput);
            }

            GhResult who = await gh.RunAsync(["api", "user", "--jq", ".login"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
            string only = who.Succeeded ? who.Output : "";
            return new GhStatus(GhAvailability.Ready, number, only, "") { Accounts = only.Length > 0 ? [only] : [] };
        }

        return new GhStatus(GhAvailability.NotSignedIn, number, "", json.AllOutput);
    }

    /// <summary>
    ///  The login of the account gh is using for github.com, from <c>gh auth status --json hosts</c>.
    /// </summary>
    /// <remarks>
    ///  Several accounts can be signed in at once, and exactly one is active — the one every command
    ///  will act as. That is the one worth naming, and the one found here.
    /// </remarks>
    public static string ActiveAccount(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("hosts", out JsonElement hosts)
                || !hosts.TryGetProperty("github.com", out JsonElement accounts)
                || accounts.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            foreach (JsonElement account in accounts.EnumerateArray())
            {
                bool active = account.TryGetProperty("active", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                bool ok = account.TryGetProperty("state", out JsonElement state) && state.GetString() == "success";

                if (active && ok && account.TryGetProperty("login", out JsonElement login))
                {
                    return login.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: treated as no account, which is what the caller will tell the person.
        }

        return "";
    }

    /// <summary>
    ///  Every account signed in for github.com, from <c>gh auth status --json hosts</c>: the active one
    ///  first, then the rest in gh's order. An account whose stored token no longer works is left out.
    /// </summary>
    public static IReadOnlyList<string> SignedInAccounts(string json)
    {
        List<string> logins = [];
        string active = "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("hosts", out JsonElement hosts)
                || !hosts.TryGetProperty("github.com", out JsonElement accounts)
                || accounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (JsonElement account in accounts.EnumerateArray())
            {
                bool ok = account.TryGetProperty("state", out JsonElement state) && state.GetString() == "success";

                if (!ok || !account.TryGetProperty("login", out JsonElement login) || login.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                if (account.TryGetProperty("active", out JsonElement flag) && flag.ValueKind == JsonValueKind.True)
                {
                    active = name;
                }
                else
                {
                    logins.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return active.Length > 0 ? [active, .. logins] : logins;
    }

    /// <summary>
    ///  The token gh holds for one of its signed-in accounts, for <see cref="GhCli.Token"/>; null when
    ///  gh has none for that login.
    /// </summary>
    /// <param name="gh">A runner with no <see cref="GhCli.Token"/> of its own, or gh answers with that instead.</param>
    public static async Task<string?> GetAccountTokenAsync(GhCli gh, string login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gh);

        GhResult result = await gh.RunAsync(
            ["auth", "token", "--hostname", "github.com", "--user", login],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Succeeded && result.Output.Trim() is { Length: > 0 } token ? token : null;
    }

    /// <summary>
    ///  Whether a failed read means this account cannot see the repository — as opposed to the network
    ///  being down — so another signed-in account is worth trying.
    /// </summary>
    /// <remarks>
    ///  For a private repository GitHub does not say "forbidden", it says the repository does not exist:
    ///  <c>gh repo view</c> reports "Could not resolve to a Repository", the REST calls a 404. An
    ///  organisation enforcing SAML single sign-on answers 403 until the account is authorised for it.
    /// </remarks>
    public static bool IsNoAccess(GhResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Succeeded || !result.WasLaunched)
        {
            return false;
        }

        string text = result.AllOutput;

        return text.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SAML", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The repository as GitHub sees it, or the reason it could not be read.</summary>
    public async Task<(GitHubRepository? Repository, GhResult Result)> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["repo", "view", .. RepositoryArguments(), "--json", "nameWithOwner,url,isPrivate,defaultBranchRef,viewerPermission,hasIssuesEnabled"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.Repository(result.Output) : null, result);
    }

    // ---- Pull requests --------------------------------------------------------------------------

    /// <param name="state"><c>open</c>, <c>closed</c>, <c>merged</c> or <c>all</c>.</param>
    /// <param name="author">A login, or <c>@me</c> for the signed-in account; empty for anyone.</param>
    public async Task<(IReadOnlyList<PullRequest> Items, GhResult Result)> ListPullRequestsAsync(string state, int limit, string author = "", CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["pr", "list", .. Repository(), "--state", state, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. Author(author), "--json", PullRequestListFields],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.PullRequests(result.Output) : [], result);
    }

    /// <summary>
    ///  The check state of the same pull requests <see cref="ListPullRequestsAsync"/> returns.
    /// </summary>
    /// <remarks>
    ///  Its own call because asking for check results makes a listing about five times slower (3.6s
    ///  for thirty against 0.66s), and a list that appears at once and grows status dots a moment later
    ///  is a better thing to look at than one that waits for them.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, ChecksState>> GetPullRequestChecksAsync(string state, int limit, string author = "", CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["pr", "list", .. Repository(), "--state", state, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. Author(author), "--json", "number,statusCheckRollup"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? GitHubParsers.ChecksByPullRequest(result.Output) : new Dictionary<int, ChecksState>();
    }

    public async Task<(PullRequestDetail? Detail, GhResult Result)> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["pr", "view", Number(number), .. Repository(), "--json", PullRequestDetailFields],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.PullRequestDetail(result.Output) : null, result);
    }

    /// <summary>The whole diff, as unified-diff text.</summary>
    public Task<GhResult> GetPullRequestDiffAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "diff", Number(number), .. Repository(), "--color", "never"], cancellationToken: cancellationToken);

    /// <summary>Makes the pull request's branch the checked-out one, fetching it first if need be.</summary>
    public Task<GhResult> CheckoutPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "checkout", Number(number), .. Repository()], cancellationToken: cancellationToken);

    public Task<GhResult> MergePullRequestAsync(int number, MergeMethod method, bool deleteBranch, CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "pr", "merge", Number(number), .. Repository(),
                method switch { MergeMethod.Squash => "--squash", MergeMethod.Rebase => "--rebase", _ => "--merge" },
                .. deleteBranch ? new[] { "--delete-branch" } : []
            ],
            cancellationToken: cancellationToken);

    public Task<GhResult> ClosePullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "close", Number(number), .. Repository()], cancellationToken: cancellationToken);

    public Task<GhResult> ReopenPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "reopen", Number(number), .. Repository()], cancellationToken: cancellationToken);

    /// <summary>Takes a draft out of draft, so it can be reviewed and merged.</summary>
    public Task<GhResult> MarkPullRequestReadyAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "ready", Number(number), .. Repository()], cancellationToken: cancellationToken);

    public Task<GhResult> CommentOnPullRequestAsync(int number, string body, CancellationToken cancellationToken = default) =>
        RunAsync(["pr", "comment", Number(number), .. Repository(), "--body-file", "-"], body, cancellationToken: cancellationToken);

    /// <param name="body">Required for <see cref="ReviewAction.RequestChanges"/> and <see cref="ReviewAction.Comment"/>.</param>
    public Task<GhResult> ReviewPullRequestAsync(int number, ReviewAction action, string body, CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "pr", "review", Number(number), .. Repository(),
                action switch { ReviewAction.Approve => "--approve", ReviewAction.RequestChanges => "--request-changes", _ => "--comment" },
                .. string.IsNullOrWhiteSpace(body) ? [] : new[] { "--body-file", "-" }
            ],
            string.IsNullOrWhiteSpace(body) ? null : body,
            cancellationToken: cancellationToken);

    /// <summary>
    ///  Opens a pull request from <paramref name="head"/> into <paramref name="baseBranch"/>.
    /// </summary>
    /// <remarks>
    ///  On success gh prints the new pull request's URL, which is what <see cref="GhResult.Output"/>
    ///  then holds. The branch has to be on GitHub already; when it is not, gh says so and that is
    ///  what comes back.
    /// </remarks>
    public Task<GhResult> CreatePullRequestAsync(string title, string body, string baseBranch, string head, bool draft, CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "pr", "create", .. Repository(), "--title", title, "--body-file", "-",
                .. string.IsNullOrWhiteSpace(baseBranch) ? [] : new[] { "--base", baseBranch },
                .. string.IsNullOrWhiteSpace(head) ? [] : new[] { "--head", head },
                .. draft ? new[] { "--draft" } : []
            ],
            body,
            cancellationToken: cancellationToken);

    // ---- Issues ---------------------------------------------------------------------------------

    public async Task<(IReadOnlyList<Issue> Items, GhResult Result)> ListIssuesAsync(string state, int limit, string author = "", CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["issue", "list", .. Repository(), "--state", state, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. Author(author), "--json", IssueListFields],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.Issues(result.Output) : [], result);
    }

    public async Task<(IssueDetail? Detail, GhResult Result)> GetIssueAsync(int number, CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["issue", "view", Number(number), .. Repository(), "--json", IssueListFields + ",body,comments"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.IssueDetail(result.Output) : null, result);
    }

    /// <summary>Opens an issue; on success <see cref="GhResult.Output"/> is its URL.</summary>
    public Task<GhResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken = default) =>
        RunAsync(["issue", "create", .. Repository(), "--title", title, "--body-file", "-"], body, cancellationToken: cancellationToken);

    /// <param name="reason"><c>completed</c> or <c>not planned</c>.</param>
    public Task<GhResult> CloseIssueAsync(int number, string reason, CancellationToken cancellationToken = default) =>
        RunAsync(
            ["issue", "close", Number(number), .. Repository(), .. string.IsNullOrWhiteSpace(reason) ? [] : new[] { "--reason", reason }],
            cancellationToken: cancellationToken);

    public Task<GhResult> ReopenIssueAsync(int number, CancellationToken cancellationToken = default) =>
        RunAsync(["issue", "reopen", Number(number), .. Repository()], cancellationToken: cancellationToken);

    public Task<GhResult> CommentOnIssueAsync(int number, string body, CancellationToken cancellationToken = default) =>
        RunAsync(["issue", "comment", Number(number), .. Repository(), "--body-file", "-"], body, cancellationToken: cancellationToken);

    // ---- Actions --------------------------------------------------------------------------------

    /// <param name="branch">Only runs on this branch, or every branch when empty.</param>
    public async Task<(IReadOnlyList<WorkflowRun> Items, GhResult Result)> ListRunsAsync(int limit, string branch = "", CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            [
                "run", "list", .. Repository(),
                "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                .. string.IsNullOrWhiteSpace(branch) ? [] : new[] { "--branch", branch },
                "--json", RunFields
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.Runs(result.Output) : [], result);
    }

    public async Task<(WorkflowRunDetail? Detail, GhResult Result)> GetRunAsync(long id, CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["run", "view", Id(id), .. Repository(), "--json", RunFields + ",jobs"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.RunDetail(result.Output) : null, result);
    }

    /// <summary>A run's log: only the steps that failed when <paramref name="failedOnly"/>, otherwise all of it.</summary>
    /// <remarks>Given longer than most calls: a big run's log is megabytes.</remarks>
    public Task<GhResult> GetRunLogAsync(long id, bool failedOnly, CancellationToken cancellationToken = default) =>
        RunAsync(
            ["run", "view", Id(id), .. Repository(), failedOnly ? "--log-failed" : "--log"],
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

    public Task<GhResult> RerunAsync(long id, bool failedOnly, CancellationToken cancellationToken = default) =>
        RunAsync(["run", "rerun", Id(id), .. Repository(), .. failedOnly ? new[] { "--failed" } : []], cancellationToken: cancellationToken);

    public Task<GhResult> CancelRunAsync(long id, CancellationToken cancellationToken = default) =>
        RunAsync(["run", "cancel", Id(id), .. Repository()], cancellationToken: cancellationToken);

    public async Task<(IReadOnlyList<Workflow> Items, GhResult Result)> ListWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(
            ["workflow", "list", .. Repository(), "--json", "id,name,path,state"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result.Succeeded ? GitHubParsers.Workflows(result.Output) : [], result);
    }

    /// <summary>Starts a workflow on a branch. It has to declare a <c>workflow_dispatch</c> trigger; gh says so when it does not.</summary>
    public Task<GhResult> RunWorkflowAsync(string workflow, string branch, CancellationToken cancellationToken = default) =>
        RunAsync(["workflow", "run", workflow, .. Repository(), .. string.IsNullOrWhiteSpace(branch) ? [] : new[] { "--ref", branch }], cancellationToken: cancellationToken);

    /// <summary>The account this client acts as, or null when gh could not say.</summary>
    public async Task<GitHubAccount?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(["api", "user"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? GitHubParsers.Account(result.Output) : null;
    }

    // ---- New repositories -----------------------------------------------------------------------

    /// <summary>The .gitignore templates GitHub keeps, by name ("VisualStudio", "Node", …).</summary>
    public async Task<(IReadOnlyList<string> Items, GhResult Result)> ListGitignoreTemplatesAsync(CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(["api", "gitignore/templates"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return (result.Succeeded ? GitHubParsers.Strings(result.Output) : [], result);
    }

    /// <summary>One .gitignore template's text, or empty when it could not be read.</summary>
    /// <remarks>No leading slash on the endpoint: gh warns that a shell may rewrite one into a path.</remarks>
    public async Task<string> GetGitignoreTemplateAsync(string name, CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(["api", $"gitignore/templates/{Uri.EscapeDataString(name)}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? GitHubParsers.Field(result.Output, "source") : "";
    }

    /// <summary>The licences GitHub offers when creating a repository.</summary>
    public async Task<(IReadOnlyList<LicenseInfo> Items, GhResult Result)> ListLicensesAsync(CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(["api", "licenses"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return (result.Succeeded ? GitHubParsers.Licenses(result.Output) : [], result);
    }

    /// <summary>
    ///  A licence's text, with its placeholders — <c>[year]</c>, <c>[fullname]</c> and the like — still
    ///  in it for the caller to fill; empty when it could not be read.
    /// </summary>
    public async Task<string> GetLicenseBodyAsync(string key, CancellationToken cancellationToken = default)
    {
        GhResult result = await RunAsync(["api", $"licenses/{Uri.EscapeDataString(key)}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? GitHubParsers.Field(result.Output, "body") : "";
    }

    /// <summary>
    ///  Creates a repository on GitHub from a local folder, adds it as <c>origin</c> and, when the folder
    ///  has commits, pushes them.
    /// </summary>
    public Task<GhResult> CreateRepositoryAsync(
        string name,
        bool isPrivate,
        string description,
        string sourceDirectory,
        bool push,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "repo", "create", name,
                isPrivate ? "--private" : "--public",
                "--source", sourceDirectory,
                "--remote", "origin",
                .. push ? new[] { "--push" } : [],
                .. string.IsNullOrWhiteSpace(description) ? [] : new[] { "--description", description }
            ],
            timeout: TimeSpan.FromMinutes(3),
            cancellationToken: cancellationToken);

    // ---- Plumbing -------------------------------------------------------------------------------

    private Task<GhResult> RunAsync(IEnumerable<string> args, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _gh.RunAsync(args, standardInput, timeout, cancellationToken);

    /// <summary><c>-R owner/name</c> when the repository is known, or nothing.</summary>
    private string[] Repository() => _repository.Length > 0 ? ["-R", _repository] : [];

    private static string[] Author(string author) => string.IsNullOrWhiteSpace(author) ? [] : ["--author", author];

    /// <summary><c>repo view</c> takes the repository as an argument rather than through <c>-R</c>.</summary>
    private string[] RepositoryArguments() => _repository.Length > 0 ? [_repository] : [];

    private static string Number(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
