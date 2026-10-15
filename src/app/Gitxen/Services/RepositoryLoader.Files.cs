// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  What the Files section needs from git: which of the entries it is showing are ignored.
/// </summary>
internal sealed partial class RepositoryLoader
{
    /// <summary>Keeps each command line well inside Windows' 32K limit.</summary>
    private const int CheckIgnoreBatch = 200;

    /// <summary>
    ///  Those of <paramref name="relativePaths"/> that git ignores, with forward slashes, as given.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   <c>check-ignore</c> only answers for untracked paths: a tracked file matching a pattern is not
    ///   ignored, and git leaves it out. It exits 1 when nothing matched, which is an answer, not a
    ///   failure.
    ///  </para>
    ///  <para>
    ///   <c>-z</c> needs <c>--stdin</c>, which the runner does not feed, so output is one path per line
    ///   with <c>core.quotePath</c> off — otherwise a non-ASCII name comes back as octal escapes in
    ///   quotes. A name git still has to quote (a quote or a control character in it) does not match
    ///   anything asked for, and is shown as not ignored.
    ///  </para>
    /// </remarks>
    public HashSet<string> IgnoredPaths(IReadOnlyList<string> relativePaths)
    {
        HashSet<string> ignored = new(StringComparer.Ordinal);

        for (int start = 0; start < relativePaths.Count; start += CheckIgnoreBatch)
        {
            string[] batch = [.. relativePaths.Skip(start).Take(CheckIgnoreBatch).Select(path => path.Replace('\\', '/'))];
            GitResult result = _git.Read(new GitArgs("-c").Add("core.quotePath=false", "check-ignore").AddPaths(batch));

            if (result.ExitCode is not (0 or 1))
            {
                break;
            }

            HashSet<string> asked = new(batch, StringComparer.Ordinal);

            foreach (string line in result.Output.Split('\n'))
            {
                string path = line.TrimEnd('\r');

                if (asked.Contains(path))
                {
                    ignored.Add(path);
                }
            }
        }

        return ignored;
    }
}
