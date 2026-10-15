// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>What to do with one commit in an interactive rebase.</summary>
internal enum RebaseAction
{
    Pick,

    /// <summary>Keep the commit, with a new message.</summary>
    Reword,

    /// <summary>Fold into the commit before it, keeping both messages.</summary>
    Squash,

    /// <summary>Fold into the commit before it, keeping only that commit's message.</summary>
    Fixup,

    Drop,

    /// <summary>Stop after applying it, so it can be amended; the rebase is then continued as usual.</summary>
    Edit
}

/// <summary>A commit offered for editing.</summary>
internal sealed record RebaseCommit(string Id, string Subject, string Message, string Author, DateTimeOffset Date)
{
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
}

/// <summary>One line of the plan: a commit and what to do with it. <paramref name="Message"/> is used by <see cref="RebaseAction.Reword"/>.</summary>
internal sealed record RebaseStep(string CommitId, RebaseAction Action, string Message = "");

/// <summary>The commits after <paramref name="BaseId"/>, oldest first, or why they cannot be edited.</summary>
/// <param name="Pushed">How many of them are already on the upstream branch, so rewriting them means a force push.</param>
internal sealed record RebasePlan(string BaseId, IReadOnlyList<RebaseCommit> Commits, string Problem, int Pushed);

/// <summary>
///  Interactive rebase without an editor: the plan made in Gitxen is written as git's todo list and
///  handed over through <c>GIT_SEQUENCE_EDITOR</c>.
/// </summary>
/// <remarks>
///  <para>
///   From <c>git help rebase</c> and experiments: git runs <c>$GIT_SEQUENCE_EDITOR &lt;todo&gt;</c>
///   through the shell, so <c>cp '&lt;our todo&gt;'</c> puts the plan in place. A reworded commit is
///   picked and then amended by an <c>exec git commit --amend -F &lt;file&gt;</c> line, so no message
///   editor is opened; <c>GIT_EDITOR=true</c> keeps git's combined message for a squash. A conflict, or
///   an <c>edit</c> step, stops the rebase as it would in a terminal — the repository's Continue and
///   Abort bar takes it from there.
///  </para>
///  <para>
///   Refused up front: uncommitted changes (a rebase needs a clean tree), merge commits in the range
///   (replaying merges is not something to do from a list), and a rebase already in progress.
///   Checked against git on a fixture: reorder, reword with a body, squash, fixup and drop in one plan.
///  </para>
/// </remarks>
internal static class InteractiveRebase
{
    private const char Field = '\u001f';

    public static RebasePlan Plan(GitCli git, string baseId)
    {
        ArgumentNullException.ThrowIfNull(git);

        if (!git.Read(new GitArgs("merge-base").Add("--is-ancestor", baseId, "HEAD")).Succeeded)
        {
            return new RebasePlan(baseId, [], "That commit is not part of the current branch.", 0);
        }

        if (Directory.Exists(GitPath(git, "rebase-merge")) || Directory.Exists(GitPath(git, "rebase-apply")))
        {
            return new RebasePlan(baseId, [], "A rebase is already in progress. Continue or abort it first.", 0);
        }

        if (git.Read(new GitArgs("status").Add("--porcelain", "--untracked-files=no")).Output.Trim().Length > 0)
        {
            return new RebasePlan(baseId, [], "There are uncommitted changes. Commit or stash them first.", 0);
        }

        if (git.Read(new GitArgs("rev-list").Add("--merges", "--max-count=1", $"{baseId}..HEAD")).Output.Trim().Length > 0)
        {
            return new RebasePlan(baseId, [], "The commits since then include a merge, which cannot be rewritten from a list.", 0);
        }

        GitResult log = git.Read(new GitArgs("log").Add("-z", "--reverse", "--format=%H%x1f%an%x1f%at%x1f%s%x1f%B", $"{baseId}..HEAD"));
        List<RebaseCommit> commits = [];

        foreach (string record in log.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = record.TrimStart('\n').Split(Field, 5);

            if (fields.Length == 5)
            {
                DateTimeOffset date = long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : default;
                commits.Add(new RebaseCommit(fields[0], fields[3], fields[4].TrimEnd(), fields[1], date));
            }
        }

        // Already on the upstream branch: rewriting these means the branch has to be force-pushed.
        int pushed = git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", "@{upstream}")).Succeeded
            ? commits.Count(commit => git.Read(new GitArgs("merge-base").Add("--is-ancestor", commit.Id, "@{upstream}")).Succeeded)
            : 0;

        return new RebasePlan(baseId, commits, commits.Count == 0 ? "There are no commits after that one." : "", pushed);
    }

    /// <summary>Why <paramref name="steps"/> (oldest first) cannot run, or empty when they can.</summary>
    public static string Validate(IReadOnlyList<RebaseStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        RebaseStep? first = steps.FirstOrDefault(step => step.Action != RebaseAction.Drop);

        if (first is { Action: RebaseAction.Squash or RebaseAction.Fixup })
        {
            return "The oldest kept commit cannot be squashed or fixed up: there is nothing before it to fold into.";
        }

        return steps.Any(step => step.Action == RebaseAction.Reword && string.IsNullOrWhiteSpace(step.Message))
            ? "A reworded commit needs a message."
            : "";
    }

    /// <summary>
    ///  Runs the plan. On success the result offers Undo, which puts the branch back where it was —
    ///  refused once anything has changed since.
    /// </summary>
    public static GitOperationResult Run(GitCli git, string baseId, IReadOnlyList<RebaseStep> steps)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(steps);

        const string Description = "Edit commits";

        if (Validate(steps) is { Length: > 0 } invalid)
        {
            return new GitOperationResult(Description, false, invalid);
        }

        string before = git.Read(new GitArgs("rev-parse").Add("HEAD")).Output.Trim();
        string folder = GitPath(git, "gitxen-rebase");

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);

            StringBuilder todo = new();
            int messages = 0;

            foreach (RebaseStep step in steps)
            {
                switch (step.Action)
                {
                    case RebaseAction.Reword:
                        string file = Path.Combine(folder, $"message-{++messages}.txt");
                        File.WriteAllText(file, step.Message.TrimEnd() + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        todo.Append("pick ").Append(step.CommitId).Append('\n');
                        todo.Append("exec git commit --amend --quiet --allow-empty -F ").Append(ShellQuote(file)).Append('\n');
                        break;

                    default:
                        todo.Append(step.Action.ToString().ToLowerInvariant()).Append(' ').Append(step.CommitId).Append('\n');
                        break;
                }
            }

            string todoFile = Path.Combine(folder, "todo");
            File.WriteAllText(todoFile, todo.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            GitResult result = git.Write(new GitArgs("rebase")
                .Add("--interactive", baseId)
                .WithEnvironment("GIT_SEQUENCE_EDITOR", $"cp {ShellQuote(todoFile)}")
                .WithEnvironment("GIT_EDITOR", "true"));

            string after = git.Read(new GitArgs("rev-parse").Add("HEAD")).Output.Trim();
            bool stopped = Directory.Exists(GitPath(git, "rebase-merge"));

            if (!result.Succeeded || stopped)
            {
                return new GitOperationResult(
                    Description,
                    result.Succeeded,
                    stopped
                        ? $"The rebase stopped part-way. {result.AllOutput.Trim()}\nResolve or amend, then Continue — or Abort to put everything back."
                        : result.AllOutput.Trim());
            }

            return new GitOperationResult(Description, true, $"Rewrote the commits after {baseId[..Math.Min(8, baseId.Length)]}.")
            {
                Undo = () => UndoRebase(git, before, after)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitOperationResult(Description, false, ex.Message);
        }
    }

    private static GitOperationResult UndoRebase(GitCli git, string before, string after)
    {
        const string Description = "Undo";

        if (git.Read(new GitArgs("rev-parse").Add("HEAD")).Output.Trim() != after)
        {
            return new GitOperationResult(Description, false, "The branch has moved on since, so undoing now could lose newer work.");
        }

        if (git.Read(new GitArgs("status").Add("--porcelain", "--untracked-files=no")).Output.Trim().Length > 0)
        {
            return new GitOperationResult(Description, false, "There are changes since the rebase. Commit or stash them, then undo.");
        }

        GitResult reset = git.Write(new GitArgs("reset").Add("--hard", "--quiet", before));
        return new GitOperationResult(Description, reset.Succeeded, reset.Succeeded ? $"The branch is back at {before[..Math.Min(8, before.Length)]}, as before the edit." : reset.AllOutput.Trim());
    }

    /// <summary>A path inside the repository's git directory, absolute.</summary>
    private static string GitPath(GitCli git, string name)
    {
        string path = git.Read(new GitArgs("rev-parse").Add("--path-format=absolute", "--git-path", name)).Output.Trim();
        return Path.GetFullPath(path);
    }

    /// <summary>Single-quoted for the shell git runs editors and exec lines with, forward slashes for its benefit.</summary>
    private static string ShellQuote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
