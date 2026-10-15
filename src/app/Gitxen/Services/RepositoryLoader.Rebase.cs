// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>Interactive rebase; the work is in <see cref="InteractiveRebase"/>.</summary>
internal sealed partial class RepositoryLoader
{
    public RebasePlan PlanRebase(string baseId) => InteractiveRebase.Plan(_git, baseId);

    public GitOperationResult RewriteCommits(string baseId, IReadOnlyList<RebaseStep> steps) => InteractiveRebase.Run(_git, baseId, steps);
}
