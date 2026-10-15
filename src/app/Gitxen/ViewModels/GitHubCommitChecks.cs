// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.GitHub;
using Gitxen.Services;
using Microsoft.UI.Dispatching;

namespace Gitxen.ViewModels;

/// <summary>
///  What GitHub Actions made of one commit: the workflow runs that were started for it, and how they
///  add up.
/// </summary>
public sealed class CommitChecks
{
    public CommitChecks(IReadOnlyList<WorkflowRun> runs)
    {
        // One row per workflow and trigger, the newest attempt. A commit that was re-run has the old
        // attempts listed too, and counting a failure that has since passed would be a wrong answer.
        Runs =
        [
            .. runs
                .GroupBy(run => (run.Workflow, run.Event))
                .Select(group => group.OrderByDescending(run => run.CreatedAt).ThenByDescending(run => run.Id).First())
                .OrderBy(run => run.Workflow, StringComparer.OrdinalIgnoreCase)
                .Select(run => new RunRow(run))
        ];

        Outcome = Combine(Runs.Select(row => row.Model.Outcome));
        Summary = Summarise(Runs);
        Signature = string.Join(';', Runs.Select(row => $"{row.Id}:{row.Model.Outcome}:{row.Model.Attempt}"));

        string[] lines =
        [
            Summary,
            .. Runs.Select(row => row.DurationText.Length > 0
                ? $"{row.OutcomeText} — {row.Workflow} · {row.DurationText}"
                : $"{row.OutcomeText} — {row.Workflow}"),
            "Click to open on GitHub"
        ];

        Tooltip = string.Join(Environment.NewLine, lines);
    }

    public IReadOnlyList<RunRow> Runs { get; }

    public RunOutcome Outcome { get; }

    /// <summary>"3 passed", "1 failed, 2 passed", "2 running": what a glance needs.</summary>
    public string Summary { get; }

    public string Tooltip { get; }

    /// <summary>Identifies what these runs came to, so a re-read that changed nothing can keep the old object.</summary>
    public string Signature { get; }

    /// <summary>
    ///  The page to open on GitHub: the run itself when there is one, and the commit's checks page,
    ///  which lists every run, when there are several.
    /// </summary>
    public string Url(HostedRepository host, string commitId) =>
        Runs.Count == 1 ? Runs[0].Model.Url : $"{host.CommitUrl(commitId)}/checks";

    /// <summary>Worst first: a failure is what someone scanning the list needs to see, whatever else is going on.</summary>
    private static RunOutcome Combine(IEnumerable<RunOutcome> outcomes)
    {
        List<RunOutcome> all = [.. outcomes];

        if (all.Contains(RunOutcome.Failed))
        {
            return RunOutcome.Failed;
        }

        if (all.Any(outcome => outcome is RunOutcome.Running or RunOutcome.Queued))
        {
            return RunOutcome.Running;
        }

        if (all.Contains(RunOutcome.ActionRequired))
        {
            return RunOutcome.ActionRequired;
        }

        if (all.Contains(RunOutcome.Cancelled))
        {
            return RunOutcome.Cancelled;
        }

        return all.Contains(RunOutcome.Succeeded) ? RunOutcome.Succeeded : RunOutcome.Neutral;
    }

    private static string Summarise(IReadOnlyList<RunRow> runs)
    {
        (RunOutcome[] Outcomes, string Word)[] groups =
        [
            ([RunOutcome.Failed], "failed"),
            ([RunOutcome.Running, RunOutcome.Queued], "running"),
            ([RunOutcome.ActionRequired], "waiting for approval"),
            ([RunOutcome.Cancelled], "cancelled"),
            ([RunOutcome.Succeeded], "passed"),
            ([RunOutcome.Skipped, RunOutcome.Neutral], "skipped")
        ];

        List<string> parts = [];

        foreach ((RunOutcome[] outcomes, string word) in groups)
        {
            int count = runs.Count(row => outcomes.Contains(row.Model.Outcome));

            if (count > 0)
            {
                parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {word}");
            }
        }

        return parts.Count == 0 ? "No runs" : string.Join(", ", parts);
    }
}

/// <summary>
///  The CI result for each commit of one repository, read from the workflow runs GitHub keeps and
///  matched to commits by id.
/// </summary>
/// <remarks>
///  <para>
///   Best-effort and silent. A repository that is not on GitHub, a machine without gh, an account
///   that cannot see the repository: each simply means no marks appear. The History page must never
///   show an error, or slow down, because a build server was out of reach.
///  </para>
///  <para>
///   One read for the whole page (the newest runs in the repository) rather than one per commit,
///   because a run carries the id of the commit it ran for, so matching is free once the runs are
///   in. Runs older than that window leave their commits unmarked, which is the right way to be
///   wrong: no mark means "unknown", never "passed".
///  </para>
/// </remarks>
public sealed class GitHubCommitChecksViewModel : IDisposable
{
    private const int RunsToRead = 200;

    private static readonly TimeSpan _tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _idleRefresh = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _minimumGap = TimeSpan.FromSeconds(10);

    private readonly GitHubRepositoryViewModel _github;
    private readonly DispatcherQueueTimer? _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);

    private Dictionary<string, CommitChecks> _byCommit = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;
    private bool _anythingRunning;

    /// <summary>Set once gh could not be used, so History does not ask again until something changes.</summary>
    private bool _unavailable;

    public GitHubCommitChecksViewModel(GitHubRepositoryViewModel github)
    {
        _github = github;

        // Created on the UI thread the tab is built on. It only ever acts while the History page is
        // on screen, and only reads while something is running or the answer has gone stale.
        _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();

        if (_timer is not null)
        {
            _timer.Interval = _tick;
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    /// <summary>Set by the page: whether it is the one on screen.</summary>
    public Func<bool>? IsPageVisible { get; set; }

    public void Dispose()
    {
        // The semaphore is left to the garbage collector: a read still in flight releases it when it
        // finishes, and disposing it out from under that read would throw.
        _timer?.Stop();
    }

    /// <summary>What is known about a commit, or null when nothing is.</summary>
    public CommitChecks? For(string commitId) =>
        _byCommit.TryGetValue(commitId, out CommitChecks? checks) ? checks : null;

    /// <summary>Forgets everything: the settings or the remote changed.</summary>
    public void Invalidate()
    {
        _unavailable = false;
        _lastRead = DateTimeOffset.MinValue;
        _byCommit = new Dictionary<string, CommitChecks>(StringComparer.OrdinalIgnoreCase);
        _github.Tab.ApplyCommitChecks();

        if (IsPageVisible?.Invoke() == true)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    ///  Reads the runs and marks the commits. Cheap to call: it does nothing when a read has just
    ///  happened, when one is already going, or when GitHub is not available for this repository.
    /// </summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (_unavailable
            || !AppOptions.GitHubEnabled
            || _github.Tab.Host is not { IsGitHub: true }
            || (!force && DateTimeOffset.UtcNow - _lastRead < _minimumGap))
        {
            return;
        }

        if (!await _busy.WaitAsync(0))
        {
            return;
        }

        try
        {
            _lastRead = DateTimeOffset.UtcNow;

            if (!await _github.EnsureReadyAsync())
            {
                _unavailable = true;
                return;
            }

            (IReadOnlyList<WorkflowRun> runs, GhResult result) = await _github.Client.ListRunsAsync(RunsToRead);

            if (!result.Succeeded)
            {
                return;
            }

            Dictionary<string, CommitChecks> next = new(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, WorkflowRun> group in runs.Where(run => run.Sha.Length > 0).GroupBy(run => run.Sha, StringComparer.OrdinalIgnoreCase))
            {
                CommitChecks fresh = new([.. group]);

                // Keep the object already on the rows when nothing changed, so a poll that found the
                // same answer does not make every row redraw.
                next[group.Key] = _byCommit.TryGetValue(group.Key, out CommitChecks? old) && old.Signature == fresh.Signature ? old : fresh;
            }

            _byCommit = next;
            _anythingRunning = runs.Any(run => !run.IsFinished);
            _github.Tab.ApplyCommitChecks();
        }
        catch (OperationCanceledException)
        {
            // The tab closed while the read was going.
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>A timer tick has nowhere to return a task to, so it is the one place an async void belongs.</summary>
    private async void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (IsPageVisible?.Invoke() != true)
        {
            return;
        }

        if (_anythingRunning || DateTimeOffset.UtcNow - _lastRead >= _idleRefresh)
        {
            await RefreshAsync(force: true);
        }
    }
}
