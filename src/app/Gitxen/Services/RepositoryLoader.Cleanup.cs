// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>Branch cleanup: finding finished branches (<see cref="BranchCleanup"/>) and deleting several at once.</summary>
internal sealed partial class RepositoryLoader
{
    /// <summary>Days without a commit before a branch is offered as stale.</summary>
    public const int StaleDays = 90;

    public IReadOnlyList<CleanupCandidate> FindCleanupCandidates(IReadOnlySet<string> mergedOnGitHub) =>
        BranchCleanup.Find(_git, BranchCleanup.DefaultBranch(_git), mergedOnGitHub, StaleDays, DateTimeOffset.Now);

    /// <summary>
    ///  Deletes each branch, and offers one Undo that restores every one deleted, each at its commit
    ///  and with its upstream.
    /// </summary>
    public GitOperationResult DeleteBranches(IReadOnlyList<string> branchNames)
    {
        List<string> deleted = [];
        List<Func<GitOperationResult>> undos = [];
        List<string> failed = [];

        foreach (string name in branchNames)
        {
            GitOperationResult result = DeleteBranch(name, force: true);

            if (result.Succeeded)
            {
                deleted.Add(name);

                if (result.Undo is Func<GitOperationResult> undo)
                {
                    undos.Add(undo);
                }
            }
            else
            {
                failed.Add($"{name}: {result.Output}");
            }
        }

        string message = deleted.Count == 1 ? $"Deleted {deleted[0]}." : $"Deleted {deleted.Count} branches: {string.Join(", ", deleted)}.";

        if (failed.Count > 0)
        {
            message += $"\nNot deleted:\n{string.Join("\n", failed)}";
        }

        return new GitOperationResult(deleted.Count == 1 ? "Delete branch" : "Delete branches", deleted.Count > 0, message)
        {
            Undo = undos.Count == 0 ? null : () =>
            {
                List<string> problems = [.. undos.Select(undo => undo()).Where(r => !r.Succeeded).Select(r => r.Output)];
                return problems.Count == 0
                    ? new GitOperationResult(UndoDescription, true, $"Restored {undos.Count} {(undos.Count == 1 ? "branch" : "branches")}.")
                    : new GitOperationResult(UndoDescription, false, string.Join("\n", problems));
            }
        };
    }
}
