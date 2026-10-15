// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Git;
using Gitxen.Models;

namespace Gitxen.Services;

/// <summary>
///  Detecting the state a repository has been left in, and the health checks that go with it.
/// </summary>
internal sealed partial class RepositoryLoader
{
    /// <summary>
    ///  Which multi-step operation, if any, is paused part-way.
    /// </summary>
    /// <remarks>
    ///  Read from the marker files in the git directory rather than by parsing <c>git status</c>:
    ///  these are the same files git's own prompt scripts look at, they are stable across versions,
    ///  and they carry the rebase step counters that status does not.
    /// </remarks>
    /// <param name="conflicts">
    ///  How many files are unmerged. Passed in rather than read here: the only caller has just read
    ///  the conflict listing, and counting them again meant a second `git diff` over the index —
    ///  about 200ms on this repository, on the path a reload takes before it can show anything.
    /// </param>
    public RepositoryOperation GetCurrentOperation(int conflicts)
    {
        string gitDirectory = GetGitDirectory();

        if (gitDirectory.Length == 0)
        {
            return RepositoryOperation.None;
        }

        // An interactive rebase uses rebase-merge; a patch-based one uses rebase-apply. Both count.
        string rebaseMerge = Path.Combine(gitDirectory, "rebase-merge");
        string rebaseApply = Path.Combine(gitDirectory, "rebase-apply");

        if (Directory.Exists(rebaseMerge) || Directory.Exists(rebaseApply))
        {
            string active = Directory.Exists(rebaseMerge) ? rebaseMerge : rebaseApply;

            return new RepositoryOperation(
                RepositoryOperationKind.Rebase,
                ReadCount(Path.Combine(active, "msgnum")),
                ReadCount(Path.Combine(active, "end")),
                conflicts);
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return new RepositoryOperation(RepositoryOperationKind.Merge, 0, 0, conflicts);
        }

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
        {
            return new RepositoryOperation(RepositoryOperationKind.CherryPick, 0, 0, conflicts);
        }

        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
        {
            return new RepositoryOperation(RepositoryOperationKind.Revert, 0, 0, conflicts);
        }

        if (File.Exists(Path.Combine(gitDirectory, "BISECT_LOG")))
        {
            return new RepositoryOperation(RepositoryOperationKind.Bisect, 0, 0, conflicts);
        }

        return RepositoryOperation.None;
    }

    /// <summary>
    ///  The identity commits will be recorded under, resolved the way git resolves it — repository
    ///  configuration first, then global.
    /// </summary>
    public (string Name, string Email) GetEffectiveIdentity()
    {
        // Effective, so no scope argument: git resolves local over global over system itself.
        GitResult name = _git.Read(new GitArgs("config").Add("--get", "user.name"));
        GitResult email = _git.Read(new GitArgs("config").Add("--get", "user.email"));

        return (
            name.Succeeded ? name.Output.Trim() : "",
            email.Succeeded ? email.Output.Trim() : "");
    }

    /// <summary>The git version behind this front-end, for the About card and for support questions.</summary>
    public string GetGitVersion()
    {
        GitResult result = _git.Read(new GitArgs("version"));

        return result.Succeeded ? result.Output.Trim() : "";
    }

    /// <summary>Absolute path of the git directory, empty when this is not a repository.</summary>
    private string GetGitDirectory()
    {
        GitResult result = _git.Read(new GitArgs("rev-parse").Add("--absolute-git-dir"));

        return result.Succeeded ? result.Output.Trim() : "";
    }

    private static int ReadCount(string path)
    {
        try
        {
            return File.Exists(path)
                && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                    ? value
                    : 0;
        }
        catch (IOException)
        {
            // The counters are a nicety; a rebase that is mid-write should not break the banner.
            return 0;
        }
    }
}
