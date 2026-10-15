// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <param name="Description">The operation, for display.</param>
/// <param name="Succeeded">Whether git exited zero.</param>
/// <param name="Output">Combined stdout/stderr — git reports progress on stderr even when it succeeds.</param>
public sealed record GitOperationResult(string Description, bool Succeeded, string Output)
{
    /// <summary>
    ///  Puts back what the operation threw away, when it can be put back: offered as the Undo button on
    ///  the result bar. Runs git, so call it off the UI thread.
    /// </summary>
    public Func<GitOperationResult>? Undo { get; init; }
}
