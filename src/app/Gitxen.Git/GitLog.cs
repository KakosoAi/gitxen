// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Gitxen.Git;

/// <summary>One commit as <c>git log</c> reports it.</summary>
/// <param name="Id">The full object id, lower-case hex.</param>
/// <param name="ParentIds">Empty for a root commit, two or more for a merge.</param>
/// <param name="Body">The message after the subject line; empty when the message is one line.</param>
public sealed record GitCommit(
    string Id,
    IReadOnlyList<string> ParentIds,
    string Author,
    string AuthorEmail,
    DateTime AuthorDate,
    string Subject,
    string Body)
{
    /// <summary>The abbreviation shown in lists. Eight characters, which is unambiguous in practice.</summary>
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;

    public bool HasParent => ParentIds.Count > 0;

    public bool IsMerge => ParentIds.Count > 1;
}

/// <summary>What to ask <c>git log</c> for.</summary>
/// <param name="AllBranches">Every ref, rather than only what HEAD reaches.</param>
/// <param name="Skip">Commits to pass over first, for paging.</param>
/// <param name="MaxCount">At most this many; zero means all of them.</param>
/// <param name="ContainingText">
///  Commits that changed the number of occurrences of this text (<c>-S</c>) — "where was this
///  introduced", which is not the same as a message search.
/// </param>
/// <param name="Author">Matched against the author (<c>--author</c>).</param>
/// <param name="MessageContains">Matched against the message, ignoring case (<c>--grep</c>).</param>
/// <param name="Path">Only commits touching this file or directory.</param>
/// <param name="Follow">
///  Carry on across renames (<c>--follow</c>), so a moved file keeps its history. Git only allows this
///  for a single file, so it is ignored unless <paramref name="Path"/> is set.
/// </param>
/// <param name="Revision">Where to start walking instead of HEAD: a commit id, a branch, a tag.</param>
/// <param name="MatchChangedLines">
///  Search <paramref name="ContainingText"/> with <c>-G</c> instead of <c>-S</c>: every commit whose
///  added or removed lines match it, including edits that leave the number of occurrences the same.
/// </param>
public sealed record GitLogQuery(
    bool AllBranches = false,
    int Skip = 0,
    int MaxCount = 0,
    string? ContainingText = null,
    string? Author = null,
    string? MessageContains = null,
    string? Path = null,
    bool Follow = false,
    string? Revision = null,
    bool MatchChangedLines = false);

/// <summary>
///  Reads <c>git log</c>.
/// </summary>
/// <remarks>
///  <para>
///   The output is structured with a custom <c>--format</c> and <c>-z</c>, both from <c>git help
///   log</c>. <c>-z</c> ends every commit with a NUL, which cannot appear in a commit message, so a
///   message with any punctuation or blank lines in it cannot be mistaken for a boundary. Inside a
///   commit the fields are separated by the ASCII unit separator, <c>0x1f</c>, which exists for
///   exactly this and which git lets a format spell as <c>%x1f</c>.
///  </para>
///  <para>
///   <b>No <c>--topo-order</c>.</b> It would guarantee that a parent is never listed before its
///   child, but it also makes git walk the entire history before printing anything — without a
///   commit-graph file, <c>--max-count</c> stops helping — which is the page-load cost this app spent
///   effort removing. The default order is by commit date; a parent can only come first when a
///   repository has commits whose dates run backwards, which shows up as an oddly drawn lane rather
///   than a wrong answer.
///  </para>
/// </remarks>
public static class GitLog
{
    private const char FieldSeparator = '\u001f';

    // %H id, %P parents, %aN/%aE author name and email with .mailmap applied, %at author date as a
    // unix timestamp, %s subject, %b body. The body is last because it is the only free-form field.
    private const string Format = "%H%x1f%P%x1f%aN%x1f%aE%x1f%at%x1f%s%x1f%b";

    private const int FieldCount = 7;

    public static GitArgs Command(GitLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Options before the revision and the pathspec, so nothing in a search term or a path can be
        // read as either.
        GitArgs args = new GitArgs("log")
            .Add("-z", $"--format={Format}", "--encoding=UTF-8", "--no-show-signature")
            .AddIf(query.Skip > 0, $"--skip={query.Skip}")
            .AddIf(query.MaxCount > 0, $"--max-count={query.MaxCount}")
            .AddIf(!string.IsNullOrWhiteSpace(query.ContainingText) && !query.MatchChangedLines, $"-S{query.ContainingText}", "--pickaxe-regex")
            .AddIf(!string.IsNullOrWhiteSpace(query.ContainingText) && query.MatchChangedLines, $"-G{query.ContainingText}")
            .AddIf(!string.IsNullOrWhiteSpace(query.Author), $"--author={query.Author}")
            .AddIf(!string.IsNullOrWhiteSpace(query.MessageContains), $"--grep={query.MessageContains}", "--regexp-ignore-case")
            .AddIf(query.Follow && !string.IsNullOrWhiteSpace(query.Path), "--follow")
            .Add(query.AllBranches ? "--all" : string.IsNullOrWhiteSpace(query.Revision) ? "HEAD" : query.Revision);

        return string.IsNullOrWhiteSpace(query.Path) ? args : args.AddPaths(query.Path);
    }

    /// <summary>
    ///  Streams commits to <paramref name="onBatch"/> as git produces them. Blocks: call it from a
    ///  background thread.
    /// </summary>
    /// <param name="onBatch">Return <c>false</c> to stop early.</param>
    /// <exception cref="OperationCanceledException">The token fired.</exception>
    /// <exception cref="InvalidOperationException">git failed; the message is what it said.</exception>
    public static void Stream(GitCli git, GitLogQuery query, Func<IReadOnlyList<GitCommit>, bool> onBatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(onBatch);

        bool stopped = false;

        GitResult result = git.ReadRecords(
            Command(query),
            records =>
            {
                List<GitCommit> commits = [];

                foreach (string record in records)
                {
                    if (Parse(record) is GitCommit commit)
                    {
                        commits.Add(commit);
                    }
                }

                if (commits.Count > 0 && !onBatch(commits))
                {
                    stopped = true;
                    return false;
                }

                return true;
            },
            cancellationToken);

        if (result.Succeeded || stopped)
        {
            return;
        }

        // A repository with no commits has no HEAD to walk. That is an empty history, not a failure.
        // Asked of git rather than read from its message, which varies by version and by locale. It
        // has to be a repository at all first: outside one, HEAD is also unresolvable, and that is
        // an error the caller needs to hear about.
        if (!query.AllBranches
            && git.Read(new GitArgs("rev-parse").Add("--git-dir")).Succeeded
            && !git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", "HEAD")).Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : $"git log exited with {result.ExitCode}.");
    }

    /// <summary>Reads up to <paramref name="query"/>'s <c>MaxCount</c> commits into a list.</summary>
    public static IReadOnlyList<GitCommit> Read(GitCli git, GitLogQuery query, CancellationToken cancellationToken = default)
    {
        List<GitCommit> all = [];

        Stream(git, query, batch =>
        {
            all.AddRange(batch);
            return true;
        }, cancellationToken);

        return all;
    }

    /// <summary>Parses one record of <see cref="Command"/>'s output; null if it is not one.</summary>
    public static GitCommit? Parse(string record)
    {
        // A limit of FieldCount keeps any 0x1f that turns up inside the message with the message.
        string[] fields = record.Split(FieldSeparator, FieldCount);

        if (fields.Length < FieldCount - 1 || fields[0].Length == 0)
        {
            return null;
        }

        DateTime date = long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
            : default;

        return new GitCommit(
            Id: fields[0],
            ParentIds: fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Author: fields[2],
            AuthorEmail: fields[3],
            AuthorDate: date,
            Subject: fields[5],
            Body: fields.Length == FieldCount ? fields[6].TrimEnd('\n', '\r') : "");
    }
}
