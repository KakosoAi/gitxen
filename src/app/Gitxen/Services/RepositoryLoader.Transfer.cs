// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>Where a transfer's progress goes, and how it is cancelled.</summary>
internal sealed record TransferContext(Action<GitProgress> Report, CancellationToken Token);

/// <summary>
///  Progress and cancelling for the commands that talk to a remote: fetch, pull, push, submodule
///  updates.
/// </summary>
internal sealed partial class RepositoryLoader
{
    /// <summary>
    ///  Set by the tab for the length of one operation it is showing progress for; null otherwise, and
    ///  then network commands run as before, without <c>--progress</c>, so their output is not full of
    ///  redrawn lines.
    /// </summary>
    public TransferContext? Transfer { get; set; }

    /// <summary>
    ///  Runs a command that talks to a remote, reporting its progress when a transfer is being shown.
    ///  A cancelled transfer throws <see cref="OperationCanceledException"/>.
    /// </summary>
    private GitOperationResult RunNetwork(string description, GitArgs arguments)
    {
        if (Transfer is not TransferContext transfer)
        {
            return Run(description, arguments);
        }

        GitResult result = _git.WriteWithProgress(arguments.Add("--progress"), transfer.Report, transfer.Token);
        return new GitOperationResult(description, result.Succeeded, result.AllOutput.Trim());
    }
}
