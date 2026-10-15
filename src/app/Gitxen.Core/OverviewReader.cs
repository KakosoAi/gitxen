// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  The reads behind a repository's Overview, shared by the window and <c>gx stats</c>.
/// </summary>
internal static class OverviewReader
{
    /// <summary>Dates and authors are read for at most this many commits; beyond that the charts say how many they cover.</summary>
    public const int StampLimit = 500_000;

    /// <summary>
    ///  The repository's overview at <c>HEAD</c>, or <paramref name="previous"/> again — with the ref
    ///  counts re-read — when <c>HEAD</c> has not moved since it was made.
    /// </summary>
    /// <remarks>
    ///  Everything except the ref counts is a function of the commit, so keeping it while the commit
    ///  is the same is not a stale answer. Branches and tags come and go without HEAD moving, and cost
    ///  one cheap read, so they are always read again.
    /// </remarks>
    public static RepositoryOverview Read(GitCli git, RepositoryOverview? previous = null)
    {
        GitResult head = git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", "HEAD"));
        string headId = head.Succeeded ? head.Output.Trim() : "";
        (int Branches, int Tags) refs = GitStats.CountRefs(git);

        if (headId.Length == 0)
        {
            return RepositoryOverview.Empty("") with { Branches = refs.Branches, Tags = refs.Tags };
        }

        if (previous is not null && previous.HeadId == headId)
        {
            return previous with { Branches = refs.Branches, Tags = refs.Tags };
        }

        return OverviewBuilder.Build(
            headId,
            GitStats.CountCommits(git, headId),
            GitStats.ReadStamps(git, StampLimit, headId),
            GitStats.ReadTree(git, headId),
            GitStats.ReadLineCounts(git, headId),
            refs,
            DateTime.Now);
    }
}
