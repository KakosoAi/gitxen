// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using AwesomeAssertions;
using Gitxen.GitHub;
using NUnit.Framework;

namespace Gitxen.Tests;

/// <summary>
///  The fixtures are real <c>gh --json</c> output captured from KakosoAi/gitxen. Each list test reads
///  the same file independently and compares field by field, so a parser that drops or invents an entry
///  fails either way.
/// </summary>
[TestFixture]
public sealed class GitHubParsersTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

    private static List<JsonElement> Raw(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }

    [Test]
    public void PullRequests_should_match_gh_output_entry_for_entry()
    {
        string json = Fixture("pr-list.json");

        IReadOnlyList<PullRequest> parsed = GitHubParsers.PullRequests(json);
        List<JsonElement> raw = Raw(json);

        parsed.Should().HaveCount(raw.Count);

        for (int i = 0; i < raw.Count; i++)
        {
            parsed[i].Number.Should().Be(raw[i].GetProperty("number").GetInt32());
            parsed[i].Title.Should().Be(raw[i].GetProperty("title").GetString());
            parsed[i].HeadBranch.Should().Be(raw[i].GetProperty("headRefName").GetString());
            parsed[i].Additions.Should().Be(raw[i].GetProperty("additions").GetInt32());
            parsed[i].Labels.Select(label => label.Name).Should().Equal(
                raw[i].GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString()));
            parsed[i].State.Should().Be(PullRequestState.Merged);
            parsed[i].Author!.IsBot.Should().BeTrue();
        }
    }

    [Test]
    public void ChecksByPullRequest_should_fail_a_pull_request_with_one_failed_check()
    {
        IReadOnlyDictionary<int, ChecksState> checks = GitHubParsers.ChecksByPullRequest(Fixture("pr-checks.json"));

        checks.Should().HaveCount(3);
        checks[20].Should().Be(ChecksState.Failing);
        checks[19].Should().Be(ChecksState.Passing);
    }

    [Test]
    public void ChecksByPullRequest_should_read_status_contexts_and_running_checks()
    {
        const string json = """
            [
              { "number": 1, "statusCheckRollup": [
                { "__typename": "StatusContext", "context": "ci/external", "state": "SUCCESS" },
                { "__typename": "CheckRun", "name": "build", "status": "IN_PROGRESS", "conclusion": "" } ] },
              { "number": 2, "statusCheckRollup": [
                { "__typename": "StatusContext", "context": "ci/external", "state": "ERROR" } ] },
              { "number": 3, "statusCheckRollup": [
                { "__typename": "CheckRun", "name": "lint", "status": "COMPLETED", "conclusion": "SKIPPED" } ] }
            ]
            """;

        IReadOnlyDictionary<int, ChecksState> checks = GitHubParsers.ChecksByPullRequest(json);

        checks[1].Should().Be(ChecksState.Pending);
        checks[2].Should().Be(ChecksState.Failing);
        checks[3].Should().Be(ChecksState.None);
    }

    [Test]
    public void Issues_should_match_gh_output_entry_for_entry()
    {
        string json = Fixture("issue-list.json");

        IReadOnlyList<Issue> parsed = GitHubParsers.Issues(json);
        List<JsonElement> raw = Raw(json);

        parsed.Should().HaveCount(raw.Count);

        for (int i = 0; i < raw.Count; i++)
        {
            parsed[i].Number.Should().Be(raw[i].GetProperty("number").GetInt32());
            parsed[i].Title.Should().Be(raw[i].GetProperty("title").GetString());
            parsed[i].State.Should().Be(IssueState.Open);
            parsed[i].StateReason.Should().BeEmpty();
            parsed[i].ClosedAt.Should().BeNull();
            parsed[i].Milestone.Should().BeEmpty();
            parsed[i].Labels.Should().HaveCount(raw[i].GetProperty("labels").GetArrayLength());
        }
    }

    [Test]
    public void Runs_should_map_each_conclusion()
    {
        IReadOnlyList<WorkflowRun> runs = GitHubParsers.Runs(Fixture("run-list.json"));

        runs.Select(run => run.Outcome).Should().Equal(RunOutcome.Succeeded, RunOutcome.Failed, RunOutcome.Cancelled);
        runs.Should().OnlyContain(run => run.Id > 0 && run.Workflow.Length > 0 && run.Url.Length > 0);
    }

    [Test]
    public void RunDetail_should_read_jobs_and_steps()
    {
        WorkflowRunDetail detail = GitHubParsers.RunDetail(Fixture("run-detail.json"));

        WorkflowJob job = detail.Jobs.Should().ContainSingle().Subject;
        job.Outcome.Should().Be(RunOutcome.Failed);
        job.Steps.Select(step => step.Outcome).Should().Equal(RunOutcome.Succeeded, RunOutcome.Failed, RunOutcome.Succeeded);
    }

    [Test]
    public void Outcome_should_treat_an_unfinished_run_by_its_status()
    {
        GitHubParsers.Outcome("in_progress", "").Should().Be(RunOutcome.Running);
        GitHubParsers.Outcome("queued", "").Should().Be(RunOutcome.Queued);
        GitHubParsers.Outcome("COMPLETED", "SUCCESS").Should().Be(RunOutcome.Succeeded);
        GitHubParsers.Outcome("completed", "startup_failure").Should().Be(RunOutcome.Failed);
    }

    [Test]
    public void Repository_should_read_gh_repo_view()
    {
        GitHubRepository repository = GitHubParsers.Repository(Fixture("repo-view.json"));

        repository.NameWithOwner.Should().Be("KakosoAi/gitxen");
        repository.DefaultBranch.Should().Be("main");
        repository.IsPrivate.Should().BeFalse();
        repository.HasIssues.Should().BeTrue();
        repository.CanWrite.Should().BeTrue();
    }

    [Test]
    public void FormatRunLog_should_print_each_step_once_without_timestamps_or_bom()
    {
        string formatted = GitHubParsers.FormatRunLog(Fixture("run-log.txt"));

        formatted.Should().StartWith("=== CLA signed / Set up job ===");
        formatted.Split("===").Length.Should().Be(3);
        formatted.Should().Contain("Current runner version: '2.337.0'");
        formatted.Should().NotContain("2026-10-15T");
        formatted.Should().NotContain("﻿");
    }

    [Test]
    public void FormatRunLog_should_keep_the_end_of_a_long_log()
    {
        string raw = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"job\tstep\t2026-10-15T17:47:22.5677639Z line {i}"));

        string formatted = GitHubParsers.FormatRunLog(raw, maxCharacters: 1000);

        formatted.Should().StartWith("[log shortened");
        formatted.Should().Contain("line 1999");
        formatted.Should().NotContain("line 0\n");
    }

    [Test]
    public void Parsers_should_tolerate_missing_fields_and_empty_input()
    {
        GitHubParsers.PullRequests("").Should().BeEmpty();
        GitHubParsers.Issues("{}").Should().BeEmpty();
        GitHubParsers.Account("").Should().BeNull();

        PullRequest pr = GitHubParsers.PullRequests("""[ { "number": 7, "author": null } ]""").Single();
        pr.Number.Should().Be(7);
        pr.Title.Should().BeEmpty();
        pr.Author.Should().BeNull();
        pr.State.Should().Be(PullRequestState.Open);
    }
}
