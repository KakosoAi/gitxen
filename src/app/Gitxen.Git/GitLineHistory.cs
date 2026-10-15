// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Gitxen.Git;

/// <summary>One commit that changed a range of lines, and what it did to them.</summary>
/// <param name="Patch">
///  The change to those lines only, from its <c>@@</c> header on. Empty for a merge that brought the lines
///  in without changing them itself, which <c>-L</c> lists but gives no diff.
/// </param>
public sealed record GitLineChange(string CommitId, string Author, DateTimeOffset Date, string Subject, string Patch)
{
    public string ShortId => CommitId.Length > 8 ? CommitId[..8] : CommitId;
}

/// <summary>
///  The history of a range of lines: every commit that changed them, newest first, with the part of each
///  diff that touched them (<c>git log -L</c>).
/// </summary>
/// <remarks>
///  <para>
///   From <c>git help log</c> and git's output: with <c>--format=%x1e%H%x1f%an%x1f%at%x1f%s</c> each
///   commit starts with a 0x1e, its fields separated by 0x1f, and the lines' patch follows after a blank
///   line, from <c>diff --git</c> to the next record. <c>-L</c> follows the lines through renames and
///   earlier edits on its own; <c>-z</c> does not apply to it, which is why the record separator is
///   chosen in the format instead.
///  </para>
///  <para>
///   The range is given in a particular commit's version of the file — the one blame named, with its
///   original line number and path — so the answer is exact even when the working copy has moved the
///   lines since.
///  </para>
/// </remarks>
public static class GitLineHistory
{
    public static IReadOnlyList<GitLineChange> Read(
        GitCli git,
        string path,
        int firstLine,
        int lastLine,
        string revision,
        int maximum = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult result = git.Read(
            new GitArgs("log").Add(
                $"-L{firstLine.ToString(CultureInfo.InvariantCulture)},{lastLine.ToString(CultureInfo.InvariantCulture)}:{path.Replace('\\', '/')}",
                "--no-color",
                "--no-ext-diff",
                $"--max-count={maximum.ToString(CultureInfo.InvariantCulture)}",
                "--format=%x1e%H%x1f%an%x1f%at%x1f%s",
                revision),
            cancellationToken);

        return result.Succeeded ? Parse(result.Text) : [];
    }

    public static IReadOnlyList<GitLineChange> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<GitLineChange> changes = [];

        foreach (string record in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            int newline = record.IndexOf('\n', StringComparison.Ordinal);
            string header = (newline < 0 ? record : record[..newline]).TrimEnd('\r');
            string[] fields = header.Split('\u001f');

            if (fields.Length != 4 || fields[0].Length is not (40 or 64))
            {
                continue;
            }

            string body = newline < 0 ? "" : record[(newline + 1)..];
            int hunk = body.IndexOf("\n@@", StringComparison.Ordinal);
            string patch = hunk < 0 ? (body.StartsWith("@@", StringComparison.Ordinal) ? body : "") : body[(hunk + 1)..];

            DateTimeOffset date = long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : default;

            changes.Add(new GitLineChange(fields[0], fields[1], date, fields[3], patch.TrimEnd('\n', '\r')));
        }

        return changes;
    }
}
