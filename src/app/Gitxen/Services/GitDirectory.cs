// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>
///  The few facts about a repository that can be read straight off disk, without running git.
/// </summary>
/// <remarks>
///  A git process costs 60-90ms on Windows, almost all of it startup, and the lists that want these
///  facts want them for every repository at once — the setup wizard's scan and the Home page both
///  face dozens. Reading the file instead is microseconds. Only facts whose on-disk format is
///  stable and documented belong here; anything needing git's own interpretation stays a command.
/// </remarks>
internal static class GitDirectory
{
    /// <summary>
    ///  Whether <paramref name="directory"/> is the top of a working tree that git would open.
    /// </summary>
    /// <remarks>
    ///  A <c>.git</c> folder holding a <c>HEAD</c> — the one file every repository has, and the first
    ///  thing git itself looks for. Or a <c>.git</c> that is a file starting <c>gitdir:</c>, which is
    ///  how a linked worktree or a submodule points at the git directory it really uses.
    /// </remarks>
    public static bool IsRepository(string directory)
    {
        try
        {
            string dotGit = Path.Combine(directory, ".git");

            if (Directory.Exists(dotGit))
            {
                return File.Exists(Path.Combine(dotGit, "HEAD"));
            }

            return File.Exists(dotGit)
                && File.ReadLines(dotGit).FirstOrDefault()?.StartsWith("gitdir:", StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///  The checked-out branch, read out of <c>.git/HEAD</c>.
    /// </summary>
    /// <returns>
    ///  The branch name, "detached at &lt;short hash&gt;" when HEAD names a commit directly, or an
    ///  empty string when it cannot be read — including a worktree or submodule, where <c>.git</c> is
    ///  a file pointing elsewhere and following it is more than a label on a list is worth.
    /// </returns>
    public static string ReadBranch(string workingDirectory)
    {
        try
        {
            string head = Path.Combine(workingDirectory, ".git", "HEAD");

            if (!File.Exists(head))
            {
                return "";
            }

            string content = File.ReadAllText(head).Trim();

            const string prefix = "ref: refs/heads/";
            return content.StartsWith(prefix, StringComparison.Ordinal)
                ? content[prefix.Length..]
                : $"detached at {content[..Math.Min(7, content.Length)]}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
