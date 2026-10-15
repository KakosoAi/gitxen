// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>Why a branch looks finished with.</summary>
internal enum CleanupReason
{
    /// <summary>Everything on it is already in the default branch (<c>--merged</c>).</summary>
    Merged,

    /// <summary>A pull request from it was merged on GitHub — squash and rebase merges included, which <c>--merged</c> cannot see.</summary>
    MergedOnGitHub,

    /// <summary>It tracked a remote branch that has since been deleted (<c>[gone]</c>).</summary>
    Gone,

    /// <summary>No commit on it for a long time.</summary>
    Stale
}

/// <summary>A local branch that may be ready to delete, and why.</summary>
internal sealed record CleanupCandidate(string Name, string CommitId, DateTimeOffset LastCommit, string Upstream, IReadOnlyList<CleanupReason> Reasons)
{
    /// <summary>
    ///  Ticked in the list without asking: nothing on the branch would be lost (merged), or its remote
    ///  is gone, which is the usual end of a merged pull request. Stale and merged-on-GitHub are
    ///  offered but left for the user to tick: the first is a guess, and the second matches by name.
    /// </summary>
    public bool IsSafe => Reasons.Contains(CleanupReason.Merged) || Reasons.Contains(CleanupReason.Gone);
}

/// <summary>
///  Finds local branches that look finished with. Read-only: deleting is the caller's, through the
///  loader, so each deletion has an Undo.
/// </summary>
/// <remarks>
///  One <c>for-each-ref</c> gives each branch's last commit date, upstream and its track state —
///  <c>[gone]</c> when the remote branch was deleted — with fields separated by 0x1f (<c>%1f</c>).
///  A second gives the branches already merged into the default branch. Both checked against git on a
///  fixture with a merged, a gone, an old and a live branch.
/// </remarks>
internal static class BranchCleanup
{
    /// <summary>Never offered for deletion, whatever their state.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "master", "develop", "development", "trunk", "production", "staging", "release"
    };

    /// <summary>
    ///  The branch others are merged into: what <c>origin/HEAD</c> points at, else the first of the
    ///  usual names that exists, else the current branch.
    /// </summary>
    public static string DefaultBranch(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult remoteHead = git.Read(new GitArgs("symbolic-ref").Add("-q", "--short", "refs/remotes/origin/HEAD"));

        if (remoteHead.Succeeded && remoteHead.Output.Trim() is { Length: > 0 } head)
        {
            string name = head.StartsWith("origin/", StringComparison.Ordinal) ? head["origin/".Length..] : head;

            if (Exists(git, name))
            {
                return name;
            }
        }

        foreach (string usual in new[] { "main", "master", "trunk", "develop" })
        {
            if (Exists(git, usual))
            {
                return usual;
            }
        }

        GitResult current = git.Read(new GitArgs("branch").Add("--show-current"));
        return current.Succeeded ? current.Output.Trim() : "";
    }

    public static IReadOnlyList<CleanupCandidate> Find(
        GitCli git,
        string defaultBranch,
        IReadOnlySet<string> mergedOnGitHub,
        int staleDays,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(mergedOnGitHub);

        GitResult refs = git.Read(new GitArgs("for-each-ref").Add(
            "--format=%(refname:short)%1f%(objectname)%1f%(committerdate:unix)%1f%(upstream:short)%1f%(upstream:track)%1f%(HEAD)",
            "refs/heads"));

        if (!refs.Succeeded)
        {
            return [];
        }

        HashSet<string> merged = new(StringComparer.Ordinal);

        if (defaultBranch.Length > 0)
        {
            GitResult mergedRefs = git.Read(new GitArgs("for-each-ref").Add("--format=%(refname:short)", $"--merged=refs/heads/{defaultBranch}", "refs/heads"));
            merged.UnionWith(mergedRefs.Succeeded ? mergedRefs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []);
        }

        List<CleanupCandidate> candidates = [];

        foreach (string line in refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\u001f');

            if (fields.Length != 6)
            {
                continue;
            }

            string name = fields[0];
            bool isCurrent = fields[5].Trim() == "*";

            if (isCurrent || name == defaultBranch || Protected.Contains(name) || name.StartsWith("release/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTimeOffset last = long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : now;

            List<CleanupReason> reasons = [];

            if (merged.Contains(name))
            {
                reasons.Add(CleanupReason.Merged);
            }
            else if (mergedOnGitHub.Contains(name))
            {
                reasons.Add(CleanupReason.MergedOnGitHub);
            }

            if (fields[4].Contains("gone", StringComparison.Ordinal))
            {
                reasons.Add(CleanupReason.Gone);
            }

            if ((now - last).TotalDays >= staleDays)
            {
                reasons.Add(CleanupReason.Stale);
            }

            if (reasons.Count > 0)
            {
                candidates.Add(new CleanupCandidate(name, fields[1], last, fields[3], reasons));
            }
        }

        return [.. candidates.OrderByDescending(c => c.IsSafe).ThenBy(c => c.LastCommit)];
    }

    public static string Describe(CleanupReason reason, int staleDays) => reason switch
    {
        CleanupReason.Merged => "merged",
        CleanupReason.MergedOnGitHub => "pull request merged on GitHub",
        CleanupReason.Gone => "remote branch deleted",
        CleanupReason.Stale => $"no commits for {staleDays}+ days",
        _ => ""
    };

    private static bool Exists(GitCli git, string branch) =>
        git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", $"refs/heads/{branch}")).Succeeded;
}
