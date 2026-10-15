// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>What the AI writing help is given to read: diffs and messages, read from git as usual.</summary>
internal sealed partial class RepositoryLoader
{
    /// <summary>The staged changes as a patch, for writing a commit message.</summary>
    public string StagedPatch()
    {
        GitResult result = _git.Read(new GitArgs("diff").Add("--cached", "--no-color", "--no-ext-diff"));
        return result.Succeeded ? result.Text : "";
    }

    /// <summary>The subjects of the latest commits, so a suggested message follows the repository's style.</summary>
    public IReadOnlyList<string> RecentSubjects(int count = 20)
    {
        GitResult result = _git.Read(new GitArgs("log").Add($"--max-count={count}", "--format=%s"));
        return result.Succeeded ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    /// <summary>
    ///  The commits and the diff a pull request from <paramref name="head"/> into <paramref name="baseBranch"/>
    ///  would carry. The remote's copy of the base is preferred, since that is what GitHub compares with.
    /// </summary>
    public (string Commits, string Diff) PullRequestMaterial(string baseBranch, string head)
    {
        string compareWith = _git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", $"refs/remotes/origin/{baseBranch}")).Succeeded
            ? $"origin/{baseBranch}"
            : baseBranch;

        GitResult commits = _git.Read(new GitArgs("log").Add("--format=- %s%n%w(0,2,2)%b", $"{compareWith}..{head}"));
        GitResult diff = _git.Read(new GitArgs("diff").Add("--no-color", "--no-ext-diff", $"{compareWith}...{head}"));
        return (commits.Succeeded ? commits.Output : "", diff.Succeeded ? diff.Text : "");
    }

    /// <summary>
    ///  The history of one blamed line, followed from the commit that last changed it, at that commit's
    ///  own line number and path — exact even when the working copy has moved the line since.
    /// </summary>
    public IReadOnlyList<GitLineChange> ReadLineHistory(GitBlameLine line) =>
        line.IsUncommitted || line.OriginalLine == 0
            ? []
            : GitLineHistory.Read(_git, line.OriginalPath, line.OriginalLine, line.OriginalLine, line.CommitId);

    /// <summary>A commit's full message and patch, for explaining it.</summary>
    public string CommitWithPatch(string commitId)
    {
        GitResult result = _git.Read(new GitArgs("show").Add("--no-color", "--no-ext-diff", "--format=fuller", "--stat", "--patch", commitId));
        return result.Succeeded ? result.Text : "";
    }
}
