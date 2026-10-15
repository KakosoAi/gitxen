// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  Undo for the operations that throw something away: discarding a file, resetting, deleting a
///  branch, amending, dropping a stash.
/// </summary>
/// <remarks>
///  <para>
///   Each one saves what it is about to lose as git objects first — a file's bytes with
///   <c>hash-object --no-filters -w</c> (no line-ending conversion, so they come back exactly), the
///   index with <c>write-tree</c>, uncommitted work with <c>stash create</c>, which records it without
///   touching anything — and returns an <see cref="GitOperationResult.Undo"/> that puts it back.
///   Nothing is written outside <c>.git</c>; the saved objects are unreachable and go the next time git
///   collects garbage. Every recipe was checked against git before it was written here.
///  </para>
///  <para>
///   An undo refuses to run when the repository has moved on since — HEAD is elsewhere, the file has
///   been edited again, the branch has been recreated — so it can never overwrite newer work.
///  </para>
/// </remarks>
internal sealed partial class RepositoryLoader
{
    private const string UndoDescription = "Undo";

    public GitOperationResult DiscardFile(string fileName, bool isUntracked)
    {
        string full = FullPath(fileName);
        string saved = File.Exists(full) ? HashFile(fileName, write: true) : "";

        GitOperationResult result = isUntracked
            ? Run($"Delete {fileName}", new GitArgs("clean").Add("--force").AddPaths(fileName))
            : Run($"Discard {fileName}", new GitArgs("checkout").AddPaths(fileName));

        if (!result.Succeeded || saved.Length == 0)
        {
            return result;
        }

        string after = File.Exists(full) ? HashFile(fileName, write: false) : "";

        return result with
        {
            Output = isUntracked ? $"Deleted {fileName}." : $"Discarded the changes to {fileName}.",
            Undo = () => RestoreFile(fileName, saved, after)
        };
    }

    public GitOperationResult ResetTo(string reference, GitResetMode mode)
    {
        string before = Head();

        // Soft and mixed keep the working tree, so the index is all that needs saving; hard throws
        // the uncommitted work away, which stash create records without touching anything.
        string index = mode == GitResetMode.Hard ? "" : ReadLine(new GitArgs("write-tree"));
        string work = mode == GitResetMode.Hard ? ReadLine(new GitArgs("stash").Add("create")) : "";

        GitOperationResult result = Run(
            $"Reset ({mode}) to {reference}",
            new GitArgs("reset").Add($"--{mode.ToString().ToLowerInvariant()}", reference));

        if (!result.Succeeded || before.Length == 0)
        {
            return result;
        }

        string after = Head();

        return result with
        {
            Output = result.Output.Length > 0 ? result.Output : $"HEAD is now at {Short(after)}.",
            Undo = () => UndoReset(before, after, mode, index, work)
        };
    }

    public GitOperationResult DeleteBranch(string branchName, bool force)
    {
        string id = ReadLine(new GitArgs("rev-parse").Add("--verify", "--quiet", $"refs/heads/{branchName}"));
        string upstream = ReadLine(new GitArgs("for-each-ref").Add("--format=%(upstream:short)", $"refs/heads/{branchName}"));

        GitOperationResult result = Run($"Delete branch {branchName}", new GitArgs("branch").Add(force ? "-D" : "-d", branchName));

        if (!result.Succeeded || id.Length == 0)
        {
            return result;
        }

        return result with { Undo = () => RestoreBranch(branchName, id, upstream) };
    }

    public GitOperationResult StashDrop(string reference)
    {
        string id = ReadLine(new GitArgs("rev-parse").Add("--verify", "--quiet", reference));
        string subject = ReadLine(new GitArgs("log").Add("-1", "--format=%s", reference));

        GitOperationResult result = Run($"Drop {reference}", new GitArgs("stash").Add("drop", reference));

        if (!result.Succeeded || id.Length == 0)
        {
            return result;
        }

        return result with
        {
            Undo = () => Run(UndoDescription, new GitArgs("stash").Add("store", "-m", subject, id)) is { Succeeded: true } stored
                ? stored with { Output = $"Restored the stash \"{subject}\"." }
                : new GitOperationResult(UndoDescription, false, "Could not store the stash again.")
        };
    }

    /// <summary>An amend can be taken back: the commit it replaced is still there, one reset away.</summary>
    private GitOperationResult WithAmendUndo(string before, GitOperationResult result)
    {
        if (!result.Succeeded || before.Length == 0)
        {
            return result;
        }

        string after = Head();

        return result with
        {
            Undo = () =>
            {
                if (Head() != after)
                {
                    return MovedOn();
                }

                GitOperationResult reset = Run(UndoDescription, new GitArgs("reset").Add("--soft", before));
                return reset.Succeeded
                    ? reset with { Output = $"Back to the commit before the amend ({Short(before)}); the amended changes are staged." }
                    : reset;
            }
        };
    }

    private GitOperationResult RestoreFile(string fileName, string blob, string after)
    {
        string full = FullPath(fileName);
        string now = File.Exists(full) ? HashFile(fileName, write: false) : "";

        if (now != after)
        {
            return new GitOperationResult(UndoDescription, false, $"{fileName} has changed since, so it was left alone.");
        }

        GitResult bytes = _git.Read(new GitArgs("cat-file").Add("blob", blob));

        if (!bytes.Succeeded)
        {
            return new GitOperationResult(UndoDescription, false, bytes.AllOutput.Trim());
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? _workingDirectory);
            File.WriteAllBytes(full, bytes.OutputBytes);
            return new GitOperationResult(UndoDescription, true, $"Restored {fileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitOperationResult(UndoDescription, false, ex.Message);
        }
    }

    private GitOperationResult UndoReset(string before, string after, GitResetMode mode, string index, string work)
    {
        if (Head() != after)
        {
            return MovedOn();
        }

        if (mode == GitResetMode.Hard)
        {
            // Resetting hard again would throw away anything done since the reset.
            if (_git.Read(new GitArgs("status").Add("--porcelain", "--untracked-files=no")).Output.Trim().Length > 0)
            {
                return new GitOperationResult(UndoDescription, false, "There are changes since the reset. Commit or stash them, then undo.");
            }

            GitOperationResult reset = Run(UndoDescription, new GitArgs("reset").Add("--hard", before));

            if (reset.Succeeded && work.Length > 0)
            {
                GitOperationResult applied = Run(UndoDescription, new GitArgs("stash").Add("apply", "--index", work));

                if (!applied.Succeeded)
                {
                    return applied;
                }
            }

            return reset.Succeeded ? reset with { Output = $"Back at {Short(before)}, with the uncommitted changes restored." } : reset;
        }

        GitOperationResult soft = Run(UndoDescription, new GitArgs("reset").Add("--soft", before));

        if (soft.Succeeded && index.Length > 0)
        {
            soft = Run(UndoDescription, new GitArgs("read-tree").Add(index));
        }

        return soft.Succeeded ? soft with { Output = $"Back at {Short(before)}, with the staged changes as they were." } : soft;
    }

    private GitOperationResult RestoreBranch(string branchName, string id, string upstream)
    {
        if (ReadLine(new GitArgs("rev-parse").Add("--verify", "--quiet", $"refs/heads/{branchName}")).Length > 0)
        {
            return new GitOperationResult(UndoDescription, false, $"A branch called {branchName} exists again, so it was left alone.");
        }

        GitOperationResult created = Run(UndoDescription, new GitArgs("branch").Add(branchName, id));

        if (created.Succeeded && upstream.Length > 0)
        {
            Run(UndoDescription, new GitArgs("branch").Add($"--set-upstream-to={upstream}", branchName));
        }

        return created.Succeeded
            ? created with { Output = $"Restored {branchName} at {Short(id)}{(upstream.Length > 0 ? $", tracking {upstream}" : "")}." }
            : created;
    }

    private static GitOperationResult MovedOn() =>
        new(UndoDescription, false, "The repository has moved on since, so undoing now could lose newer work.");

    private string Head() => ReadLine(new GitArgs("rev-parse").Add("--verify", "--quiet", "HEAD"));

    private string ReadLine(GitArgs arguments)
    {
        GitResult result = _git.Read(arguments);
        return result.Succeeded ? result.Output.Trim() : "";
    }

    /// <summary>The file's exact bytes as a blob id — stored in the object database when <paramref name="write"/>.</summary>
    private string HashFile(string fileName, bool write) =>
        ReadLine(new GitArgs("hash-object").Add("--no-filters").AddIf(write, "-w").AddPaths(fileName));

    private string FullPath(string fileName) => Path.Combine(_workingDirectory, fileName.Replace('/', Path.DirectorySeparatorChar));
}
