// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Git;

/// <summary>
///  What <c>git reset</c> does to the working tree and the index while it moves the branch.
/// </summary>
/// <remarks>
///  The names are git's own flags: <c>--soft</c>, <c>--mixed</c> and <c>--hard</c>.
/// </remarks>
public enum GitResetMode
{
    /// <summary>Move the branch only. The index and the working tree are left as they were.</summary>
    Soft,

    /// <summary>Move the branch and reset the index, keeping the working tree.</summary>
    Mixed,

    /// <summary>Move the branch and reset the index and working tree. Uncommitted work is lost.</summary>
    Hard
}
