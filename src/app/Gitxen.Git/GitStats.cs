// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Gitxen.Git;

/// <summary>When a commit was authored, and by whom (after the mailmap).</summary>
/// <param name="Time">Author time, Unix seconds.</param>
public sealed record GitCommitStamp(long Time, string Author, string Email);

/// <summary>One entry of the tree at a revision.</summary>
/// <param name="Size">Bytes; -1 for a submodule, which has no blob of its own.</param>
public sealed record GitTreeFile(string Path, long Size)
{
    public bool IsSubmodule => Size < 0;
}

/// <summary>
///  The reads behind a repository's overview: its commits' dates and authors, the files at a
///  revision with their sizes, how many lines each text file has, and how many refs there are.
/// </summary>
/// <remarks>
///  <para>
///   Written from <c>git help log</c>, <c>git help ls-tree</c>, <c>git help grep</c> and
///   <c>git help for-each-ref</c>, and checked against their output. Everything is read at one
///   revision, so an answer depends only on that commit and can be kept for as long as it is
///   <c>HEAD</c>.
///  </para>
///  <para>
///   Line counts come from <c>grep -c -I -e ""</c> on the tree: the empty pattern matches every line,
///   including a last one with no newline; <c>-I</c> leaves binary files out, which is the definition
///   of a text file wanted here; and git reads the blobs itself, in parallel, without a checkout.
///   It is not quite <c>wc -l</c>: a blank last line — a file ending in two newlines — is not
///   counted, so an empty file and one holding a single newline match nothing and are not listed.
///  </para>
/// </remarks>
public static class GitStats
{
    private const char FieldSeparator = '\u001f';

    /// <summary>Commits reachable from <paramref name="revision"/>; 0 when it does not resolve (no commits yet).</summary>
    public static int CountCommits(GitCli git, string revision = "HEAD")
    {
        ArgumentNullException.ThrowIfNull(git);
        GitResult result = git.Read(new GitArgs("rev-list").Add("--count", revision));
        return result.Succeeded && int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : 0;
    }

    /// <summary>The newest <paramref name="maximum"/> commits' author times and names, newest first.</summary>
    public static IReadOnlyList<GitCommitStamp> ReadStamps(GitCli git, int maximum, string revision = "HEAD")
    {
        ArgumentNullException.ThrowIfNull(git);
        GitResult result = git.Read(new GitArgs("log")
            .Add("-z", "--format=%at%x1f%aN%x1f%aE", $"--max-count={maximum.ToString(CultureInfo.InvariantCulture)}", revision));

        return result.Succeeded ? ParseStamps(result.Text) : [];
    }

    /// <summary>Every file in the tree at <paramref name="revision"/>, recursively.</summary>
    public static IReadOnlyList<GitTreeFile> ReadTree(GitCli git, string revision = "HEAD")
    {
        ArgumentNullException.ThrowIfNull(git);
        GitResult result = git.Read(new GitArgs("ls-tree").Add("-r", "-z", "--long", revision));
        return result.Succeeded ? ParseTree(result.Text) : [];
    }

    /// <summary>Lines in each text file of the tree at <paramref name="revision"/>, by path.</summary>
    public static IReadOnlyDictionary<string, int> ReadLineCounts(GitCli git, string revision = "HEAD")
    {
        ArgumentNullException.ThrowIfNull(git);
        GitResult result = git.Read(new GitArgs("grep").Add("-I", "-c", "-z", "-e", "", revision));

        // Exit 1 is "no line matched": a tree of binary files, or of empty ones.
        return result.ExitCode is 0 ? ParseLineCounts(result.Text, revision) : new Dictionary<string, int>();
    }

    /// <summary>Local branches and tags.</summary>
    public static (int Branches, int Tags) CountRefs(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);
        GitResult result = git.Read(new GitArgs("for-each-ref").Add("--format=%(refname)", "refs/heads", "refs/tags"));

        if (!result.Succeeded)
        {
            return (0, 0);
        }

        string[] refs = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (refs.Count(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)),
                refs.Count(r => r.StartsWith("refs/tags/", StringComparison.Ordinal)));
    }

    /// <summary>
    ///  <c>log -z --format=%at%x1f%aN%x1f%aE</c>: one NUL-terminated record per commit, three fields
    ///  separated by 0x1f.
    /// </summary>
    public static IReadOnlyList<GitCommitStamp> ParseStamps(string text)
    {
        List<GitCommitStamp> stamps = [];

        foreach (string record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = record.Trim('\n').Split(FieldSeparator);

            if (fields.Length == 3 && long.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long time))
            {
                stamps.Add(new GitCommitStamp(time, fields[1], fields[2]));
            }
        }

        return stamps;
    }

    /// <summary>
    ///  <c>ls-tree -r -z --long</c>: <c>mode SP type SP id SP+ size TAB path NUL</c>, the size padded
    ///  to a column, and <c>-</c> for a submodule (type <c>commit</c>).
    /// </summary>
    public static IReadOnlyList<GitTreeFile> ParseTree(string text)
    {
        List<GitTreeFile> files = [];

        foreach (string record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t', StringComparison.Ordinal);

            if (tab < 0)
            {
                continue;
            }

            string[] meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (meta.Length != 4)
            {
                continue;
            }

            long size = meta[1] == "commit" || !long.TryParse(meta[3], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                ? -1
                : parsed;

            files.Add(new GitTreeFile(record[(tab + 1)..], size));
        }

        return files;
    }

    /// <summary>
    ///  <c>grep -c -z</c> on a tree: <c>revision:path NUL count LF</c> per file. The revision prefix is
    ///  exactly what was asked for, so it is removed by length rather than by looking for a colon,
    ///  which a path may contain.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ParseLineCounts(string text, string revision)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        string prefix = revision + ":";
        int position = 0;

        while (position < text.Length)
        {
            int nul = text.IndexOf('\0', position);
            int newline = nul < 0 ? -1 : text.IndexOf('\n', nul);

            if (nul < 0 || newline < 0)
            {
                break;
            }

            string path = text[position..nul];

            if (path.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(text.AsSpan(nul + 1, newline - nul - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                counts[path[prefix.Length..]] = count;
            }

            position = newline + 1;
        }

        return counts;
    }
}
