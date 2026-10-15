// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitxen.Git;

/// <summary>One progress report from a clone, fetch, pull or push.</summary>
/// <param name="Phase">What git is doing: "Receiving objects", "Resolving deltas", "Writing objects", …</param>
/// <param name="Percent">0–100 when git gives one; null for a phase that only counts ("Enumerating objects: 123").</param>
/// <param name="Detail">What follows the count: size and speed while receiving or writing ("1.20 MiB | 2.40 MiB/s").</param>
/// <param name="IsRemote">Reported by the server (<c>remote:</c>) rather than by the local git.</param>
public sealed record GitProgress(string Phase, int? Percent, long Done, long Total, string Detail, bool IsRemote, bool IsDone);

/// <summary>
///  Reads git's progress lines, as <c>--progress</c> prints them to standard error.
/// </summary>
/// <remarks>
///  <para>
///   Taken from git's own output while cloning, fetching and pushing, not from documentation, which
///   does not describe it. Each update ends in a carriage return, so the same line is redrawn in a
///   terminal, and a phase's last update ends in <c>, done.</c> and a newline:
///  </para>
///  <code>
///   Receiving objects:  45% (1234/2742), 1.20 MiB | 2.40 MiB/s
///   Resolving deltas: 100% (65683/65683), done.
///   remote: Enumerating objects: 95425, done.          (the server's; padded with spaces)
///   Enumerating objects: 66488                         (a count, no percentage)
///  </code>
///  <para>
///   Everything else on standard error — "Cloning into …", "To …", the ref updates, "Total …" — is not
///   progress, is not matched, and stays in the command's output as before.
///  </para>
/// </remarks>
public static partial class GitProgressParser
{
    public static GitProgress? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        Match match = Pattern().Match(line.TrimEnd());

        if (!match.Success)
        {
            return null;
        }

        string rest = match.Groups["rest"].Value.Trim();
        bool done = rest.EndsWith("done.", StringComparison.Ordinal);
        string detail = rest.TrimStart(',', ' ');

        if (done)
        {
            detail = detail[..^"done.".Length].TrimEnd(',', ' ');
        }

        long Number(string group) => long.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0;

        return match.Groups["percent"].Success
            ? new GitProgress(match.Groups["phase"].Value, (int)Math.Min(100, Number("percent")), Number("done"), Number("total"), detail, match.Groups["remote"].Success, done)
            : new GitProgress(match.Groups["phase"].Value, null, Number("count"), 0, detail, match.Groups["remote"].Success, done);
    }

    [GeneratedRegex(@"^(?<remote>remote: )?(?<phase>[A-Z][A-Za-z ]*?):\s+(?:(?<percent>\d{1,3})%\s+\((?<done>\d+)/(?<total>\d+)\)|(?<count>\d+))(?<rest>(?:,.*)?)$")]
    private static partial Regex Pattern();
}
