// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Git;

/// <summary>
///  Reads <c>git diff</c>: which files changed between two points, and the patch for one of them.
/// </summary>
/// <remarks>
///  <para>
///   Every command here carries the same three options. <c>--no-color</c> because the app colours
///   diffs itself and <c>color.ui=always</c> in someone's config would otherwise fill the text with
///   escape codes. <c>--no-ext-diff</c> because a configured external diff tool replaces git's output
///   with whatever that tool prints, which is not a unified diff and cannot be staged from.
///   <c>--find-renames</c> so a moved file is one change rather than a delete and an add.
///  </para>
///  <para>
///   The listing format, from <c>git help diff</c> and confirmed against git's real output: with
///   <c>--name-status -z</c> every field is its own NUL-terminated record. An ordinary change is two
///   records, the status letter and the path. A rename or copy is three — the status with its
///   similarity score (<c>R100</c>), then the <b>old</b> path, then the new one.
///  </para>
/// </remarks>
public static class GitDiff
{
    /// <summary>The files that differ between two revisions.</summary>
    public static GitArgs Changes(string from, string to) =>
        Diff().Add("--name-status", "-z", from, to).AddPaths();

    /// <summary>One file's patch between two revisions.</summary>
    /// <param name="oldPath">
    ///  The path before a rename. Both paths must be in the pathspec: given only the new one, git
    ///  cannot see where the file came from and reports it as newly added.
    /// </param>
    public static GitArgs Patch(string from, string to, string path, string? oldPath) =>
        Diff().Add(from, to).AddPaths(PathsOf(path, oldPath));

    /// <summary>
    ///  One tracked file's uncommitted patch: what is staged when <paramref name="staged"/> is set,
    ///  otherwise what is in the working tree and not yet staged.
    /// </summary>
    public static GitArgs WorkingTreePatch(string path, string? oldPath, bool staged) =>
        Diff().AddIf(staged, "--cached").AddPaths(PathsOf(path, oldPath));

    /// <summary>
    ///  An untracked file shown as an addition.
    /// </summary>
    /// <remarks>
    ///  <c>git diff</c> says nothing about a file the index has never seen, so this compares the file
    ///  against nothing with <c>--no-index</c>, which git accepts <c>/dev/null</c> for on Windows too.
    ///  That mode <b>exits 1 when the two sides differ</b> — always, here — so its exit code is not a
    ///  measure of success; the output is.
    /// </remarks>
    public static GitArgs UntrackedPatch(string path) =>
        Diff().Add("--no-index").AddPaths("/dev/null", path);

    /// <summary>Parses <see cref="Changes"/>.</summary>
    public static IReadOnlyList<GitChange> ParseChanges(string output)
    {
        List<GitChange> changes = [];

        if (string.IsNullOrEmpty(output))
        {
            return changes;
        }

        string[] records = output.Split('\0');

        for (int i = 0; i < records.Length; i++)
        {
            string status = records[i];

            if (status.Length == 0)
            {
                continue;
            }

            char code = status[0];

            if (code is 'R' or 'C')
            {
                // Old path first, then new: both follow the status and both are consumed here.
                if (i + 2 >= records.Length)
                {
                    break;
                }

                string oldPath = records[++i];
                string newPath = records[++i];
                changes.Add(new GitChange(newPath, oldPath, KindOf(code), IsStaged: false));
                continue;
            }

            if (i + 1 >= records.Length)
            {
                break;
            }

            string path = records[++i];

            // 'X' is git reporting a bug in itself; there is nothing meaningful to show for it.
            if (code != 'X')
            {
                changes.Add(new GitChange(path, null, KindOf(code), IsStaged: false));
            }
        }

        return changes;
    }

    private static GitArgs Diff() =>
        new GitArgs("diff").Add("--no-color", "--no-ext-diff", "--find-renames");

    private static string[] PathsOf(string path, string? oldPath) =>
        string.IsNullOrEmpty(oldPath) || oldPath == path ? [path] : [oldPath, path];

    private static GitChangeKind KindOf(char code) => code switch
    {
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Conflicted,
        _ => GitChangeKind.Modified
    };
}
