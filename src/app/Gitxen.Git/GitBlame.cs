// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Gitxen.Git;

/// <summary>One line of a file, and the commit that last changed it.</summary>
/// <param name="LineNumber">Where the line is in the file as it was blamed, counting from 1.</param>
/// <param name="CommitId">Full object id. All zeros for a line that has not been committed yet.</param>
/// <param name="Summary">The subject of that commit.</param>
/// <param name="Text">The line itself, without its line ending.</param>
/// <param name="IsBoundary">
///  The blame walk stopped here because history ran out (or a shallow clone ends), so the commit is
///  the oldest one that could be found rather than the one that really wrote the line.
/// </param>
public sealed record GitBlameLine(
    int LineNumber,
    string CommitId,
    string Author,
    DateTimeOffset AuthorDate,
    string Summary,
    string Text,
    bool IsBoundary)
{
    /// <summary>Git blames uncommitted lines on the all-zero id, and calls the author "Not Committed Yet".</summary>
    public bool IsUncommitted => CommitId.All(character => character == '0');

    public string ShortId => CommitId.Length > 8 ? CommitId[..8] : CommitId;

    /// <summary>Where the line was in <see cref="CommitId"/>'s version of the file: what <c>log -L</c> needs to follow it back.</summary>
    public int OriginalLine { get; init; }

    /// <summary>The file's path in <see cref="CommitId"/>, which differs from today's when it has been renamed since.</summary>
    public string OriginalPath { get; init; } = "";
}

/// <summary>What <c>git blame</c> came to: the lines, or why there are none.</summary>
public sealed record GitBlameResult(IReadOnlyList<GitBlameLine> Lines, string Error)
{
    public bool Succeeded => Error.Length == 0;
}

/// <summary>
///  Reads <c>git blame</c>.
/// </summary>
/// <remarks>
///  <para>
///   Read as <c>--line-porcelain</c>, from <c>git help blame</c>: every line carries the whole of its
///   commit's header, so a line can be read without remembering anything from the ones before it.
///   The plain <c>--porcelain</c> form gives the header only the first time a commit appears, which
///   is smaller but makes every line depend on an earlier one.
///  </para>
///  <para>
///   A line is a header — <c>&lt;id&gt; &lt;original line&gt; &lt;final line&gt; [&lt;group size&gt;]</c> —
///   then <c>key value</c> lines (<c>author</c>, <c>author-time</c>, <c>summary</c>, and a bare
///   <c>boundary</c> when it applies), then the file's own line prefixed with a tab. The tab is what
///   ends the record: it cannot begin a header or a key, and the line's content is never scanned for
///   anything, so a source line that happens to look like a header is still only text.
///  </para>
/// </remarks>
public static class GitBlame
{
    /// <summary>Blames <paramref name="path"/> as it is in the working tree, or as of <paramref name="revision"/>.</summary>
    public static GitBlameResult Read(GitCli git, string path, string? revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);

        // The revision goes before `--`, the path after, so neither can be read as the other.
        GitResult result = git.Read(
            new GitArgs("blame")
                .Add("--line-porcelain")
                .AddIfNotEmpty(revision)
                .AddPaths(path),
            cancellationToken);

        if (!result.Succeeded)
        {
            return new GitBlameResult([], result.Message.Length > 0 ? result.Message : $"git blame exited with {result.ExitCode}.");
        }

        return new GitBlameResult(Parse(result.Text), "");
    }

    /// <summary>Parses <c>git blame --line-porcelain</c> output.</summary>
    public static IReadOnlyList<GitBlameLine> Parse(string porcelain)
    {
        ArgumentNullException.ThrowIfNull(porcelain);

        List<GitBlameLine> lines = [];

        string commitId = "";
        int finalLine = 0;
        int originalLine = 0;
        string originalPath = "";
        string author = "";
        DateTimeOffset date = default;
        string summary = "";
        bool boundary = false;
        bool inHeader = false;

        foreach (string raw in porcelain.Split('\n'))
        {
            if (raw.Length > 0 && raw[0] == '\t')
            {
                if (inHeader)
                {
                    // A file with Windows line endings leaves the carriage return on the line.
                    string text = raw.Length > 1 && raw[^1] == '\r' ? raw[1..^1] : raw[1..];
                    lines.Add(new GitBlameLine(finalLine, commitId, author, date, summary, text, boundary)
                    {
                        OriginalLine = originalLine,
                        OriginalPath = originalPath
                    });
                }

                inHeader = false;
                continue;
            }

            if (!inHeader)
            {
                if (TryParseHeader(raw, out commitId, out originalLine, out finalLine))
                {
                    inHeader = true;
                    originalPath = "";
                    author = "";
                    date = default;
                    summary = "";
                    boundary = false;
                }

                continue;
            }

            if (raw.StartsWith("author ", StringComparison.Ordinal))
            {
                author = raw[7..];
            }
            else if (raw.StartsWith("author-time ", StringComparison.Ordinal)
                && long.TryParse(raw.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            {
                date = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            else if (raw.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = raw[8..];
            }
            else if (raw.StartsWith("filename ", StringComparison.Ordinal))
            {
                originalPath = raw[9..];
            }
            else if (raw == "boundary")
            {
                boundary = true;
            }
        }

        return lines;
    }

    /// <summary><c>&lt;40 or 64 hex digits&gt; &lt;number&gt; &lt;number&gt; [&lt;number&gt;]</c>; false for anything else.</summary>
    private static bool TryParseHeader(string line, out string commitId, out int originalLine, out int finalLine)
    {
        commitId = "";
        originalLine = 0;
        finalLine = 0;

        string[] parts = line.Split(' ');

        if (parts.Length is < 3 or > 4
            || parts[0].Length is not (40 or 64)
            || !parts[0].All(Uri.IsHexDigit)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out originalLine)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out finalLine))
        {
            return false;
        }

        commitId = parts[0];
        return true;
    }
}
