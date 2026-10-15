// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Git;

/// <summary>How a path changed.</summary>
public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,

    /// <summary>The entry changed between a file, a symlink and a submodule.</summary>
    TypeChanged,

    /// <summary>Present in the working tree and not in the index at all.</summary>
    Untracked,

    /// <summary>Unmerged — a conflict waiting to be resolved.</summary>
    Conflicted
}

/// <summary>
///  One change to one path, on one side of the index.
/// </summary>
/// <remarks>
///  A file can produce two of these. Git reports a file that is modified, staged, and then modified
///  again as a single record carrying both states, and the Changes page lists staged and unstaged
///  separately — so the parser emits one entry per side rather than one per path.
/// </remarks>
public sealed record GitChange(string Path, string? OldPath, GitChangeKind Kind, bool IsStaged)
{
    /// <summary>
    ///  Not in HEAD, so discarding it means deleting the file rather than restoring a previous
    ///  version.
    /// </summary>
    public bool IsNew => Kind is GitChangeKind.Added or GitChangeKind.Untracked;

    public bool IsDeleted => Kind is GitChangeKind.Deleted;

    public bool IsRenamed => Kind is GitChangeKind.Renamed or GitChangeKind.Copied;
}

/// <summary>
///  Reads <c>git status --porcelain=v2 -z</c>.
/// </summary>
/// <remarks>
///  <para>
///   Version 2 of the porcelain format, which git documents as stable and machine-readable, and
///   <c>-z</c> so that each record is NUL-terminated. Together those remove every quoting question:
///   a path containing a space, a quote, a newline or a non-UTF-8 byte arrives as itself.
///  </para>
///  <para>
///   The record shapes, from <c>git help status</c>:
///  </para>
///  <code>
///  # ...                                                       a header, ignored here
///  1 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;path&gt;             an ordinary change
///  2 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;Xscore&gt; &lt;path&gt;    a rename or copy
///  u &lt;XY&gt; &lt;sub&gt; &lt;m1&gt; &lt;m2&gt; &lt;m3&gt; &lt;mW&gt; &lt;h1&gt; &lt;h2&gt; &lt;h3&gt; &lt;path&gt;   unmerged
///  ? &lt;path&gt;                                                   untracked
///  ! &lt;path&gt;                                                   ignored
///  </code>
///  <para>
///   Two details that are easy to get wrong. A rename's <b>original path is the next record</b>, not
///   a field of its own — with <c>-z</c> git cannot use the tab separator it uses otherwise. And in
///   <c>XY</c> the first character is what the index holds and the second what the working tree
///   holds, with <c>.</c> meaning unchanged; a path with both set has been changed twice and belongs
///   in both lists.
///  </para>
/// </remarks>
public static class GitStatus
{
    /// <summary>The arguments this parser expects.</summary>
    /// <remarks>
    ///  <c>--untracked-files=all</c> lists the files inside a new directory rather than the directory
    ///  alone, which is what a staging list has to show. Ignored files are left out: they are not
    ///  changes, and asking for them makes the command markedly slower in a repository with a large
    ///  build output.
    /// </remarks>
    public static GitArgs Command() =>
        new GitArgs("status").Add("--porcelain=v2", "-z", "--untracked-files=all");

    public static IReadOnlyList<GitChange> Parse(string output)
    {
        List<GitChange> changes = [];

        if (string.IsNullOrEmpty(output))
        {
            return changes;
        }

        string[] records = output.Split('\0');

        for (int i = 0; i < records.Length; i++)
        {
            string record = records[i];

            if (record.Length < 2)
            {
                continue;
            }

            switch (record[0])
            {
                case '1':
                    AddSides(changes, Field(record, 1), Path(record, 8), oldPath: null);
                    break;

                case '2':
                    // The original path is the record that follows, so it is consumed here and the
                    // loop must not see it again.
                    string newPath = Path(record, 9);
                    string? oldPath = i + 1 < records.Length ? records[++i] : null;
                    AddSides(changes, Field(record, 1), newPath, oldPath);
                    break;

                case 'u':
                    // One entry, unstaged: a conflict is something to resolve, not something staged.
                    changes.Add(new GitChange(Path(record, 10), null, GitChangeKind.Conflicted, IsStaged: false));
                    break;

                case '?':
                    changes.Add(new GitChange(record[2..], null, GitChangeKind.Untracked, IsStaged: false));
                    break;

                default:
                    // A header ('#') or an ignored file ('!'), neither of which is a change.
                    break;
            }
        }

        return changes;
    }

    /// <summary>
    ///  Emits an entry for each side of the index that reports a change.
    /// </summary>
    /// <remarks>
    ///  The old path belongs only to the staged side: the rename is what was staged, and anything
    ///  done to the file afterwards is an ordinary edit of its new path.
    /// </remarks>
    private static void AddSides(List<GitChange> changes, string states, string path, string? oldPath)
    {
        if (states.Length < 2)
        {
            return;
        }

        if (states[0] != '.')
        {
            changes.Add(new GitChange(path, oldPath, KindOf(states[0]), IsStaged: true));
        }

        if (states[1] != '.')
        {
            changes.Add(new GitChange(path, null, KindOf(states[1]), IsStaged: false));
        }
    }

    private static GitChangeKind KindOf(char state) => state switch
    {
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Conflicted,
        _ => GitChangeKind.Modified
    };

    /// <summary>The <paramref name="index"/>th space-separated field of a record.</summary>
    private static string Field(string record, int index)
    {
        string[] fields = record.Split(' ', index + 2);
        return fields.Length > index ? fields[index] : "";
    }

    /// <summary>
    ///  The path, which is the last field and the only one that may contain spaces.
    /// </summary>
    /// <remarks>
    ///  Split with a limit so everything after the fixed fields stays together, however many spaces
    ///  it holds.
    /// </remarks>
    private static string Path(string record, int index)
    {
        string[] fields = record.Split(' ', index + 1);
        return fields.Length > index ? fields[index] : "";
    }
}
