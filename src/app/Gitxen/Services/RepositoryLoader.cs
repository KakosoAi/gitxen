// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Gitxen.Git;
using Gitxen.Models;

namespace Gitxen.Services;

/// <summary>
///  Everything the view models ask git for, for one repository.
/// </summary>
/// <remarks>
///  <para>
///   One instance per open repository, spread over several files by concern: this one holds the
///   history, diffs and blame; <c>.Objects</c> the structured listings; <c>.Operations</c> everything
///   that changes the repository; <c>.Config</c>, <c>.State</c> and <c>.Conflicts</c> the rest.
///  </para>
///  <para>
///   All of it runs through <see cref="GitCli"/>, which starts a fresh git process per command, so
///   two operations can safely run at once against the same instance — though git itself will make
///   the second of two writers fail on <c>index.lock</c>, which is the correct outcome.
///  </para>
/// </remarks>
internal sealed partial class RepositoryLoader
{
    private const int MaxRememberedPatches = 256;

    private const int MaxRememberedBlames = 32;

    private readonly GitCli _git;

    /// <summary>Patches between two commits; see <see cref="ReadPatchAsync"/>.</summary>
    private readonly ConcurrentDictionary<PatchKey, string> _patches = new();

    /// <summary>Blame of a file at a fixed commit; see <see cref="GetBlame"/>.</summary>
    private readonly ConcurrentDictionary<(string Revision, string Path), GitBlameResult> _blames = new();

    private readonly string _workingDirectory;

    public RepositoryLoader(string workingDir)
    {
        _workingDirectory = workingDir;
        _git = new GitCli(workingDir);
        _git.CommandCompleted += (_, command) => GitActivity.Record(command);
    }

    /// <summary>
    ///  Streams the commit log to <paramref name="observer"/> in batches as <c>git log</c> produces
    ///  them. Blocks the calling thread for the duration — call it from a background thread.
    /// </summary>
    /// <param name="skip">How many of the most recent commits to skip, for paging.</param>
    /// <param name="maxCount">
    ///  Handed to git rather than applied afterwards. Without it git walks the whole history and the
    ///  page is thrown away at the far end: the caller can only stop the stream once a batch has
    ///  already arrived past its cap, so on a large repository a page cost the full walk rather than
    ///  the one page.
    /// </param>
    public void StreamRevisions(IObserver<IReadOnlyList<GitCommit>> observer, int skip, int maxCount, RevisionQuery query, CancellationToken cancellationToken)
    {
        // The scope, the search terms and --skip all belong to git rather than being applied to rows
        // afterwards: the app only ever holds a page, so filtering afterwards could only search the
        // page that happened to be loaded.
        GitLogQuery log = new(
            AllBranches: query.Scope == RevisionScope.AllBranches,
            Skip: skip,
            MaxCount: maxCount,
            ContainingText: query.ContainingText,
            Author: query.Author,
            MessageContains: query.MessageContains,
            Path: query.Path,
            MatchChangedLines: query.MatchChangedLines,
            Revision: query.FromRevision);

        GitLog.Stream(
            _git,
            log,
            batch =>
            {
                observer.OnNext(batch);
                return !cancellationToken.IsCancellationRequested;
            },
            cancellationToken);

        observer.OnCompleted();
    }

    /// <summary>
    ///  The commits that touched one file, newest first, from a revision (HEAD when none is named) and
    ///  following the file across renames.
    /// </summary>
    public IReadOnlyList<GitCommit> GetFileHistory(string fileName, int maxCount, string? revision, CancellationToken cancellationToken) =>
        GitLog.Read(_git, new GitLogQuery(MaxCount: maxCount, Path: fileName, Follow: true, Revision: revision), cancellationToken);

    /// <summary>
    ///  The checked-out branch, or "detached at &lt;short hash&gt;" when HEAD names a commit directly.
    /// </summary>
    /// <remarks>
    ///  Read off <c>.git/HEAD</c> when it can be, which costs microseconds against the tens of
    ///  milliseconds a git process takes and is on the path every reload waits for. A worktree or
    ///  submodule keeps <c>.git</c> as a pointer file, and there git is asked.
    /// </remarks>
    public string GetCurrentBranch()
    {
        string fast = GitDirectory.ReadBranch(_workingDirectory);

        if (fast.Length > 0)
        {
            return fast;
        }

        GitResult branch = _git.Read(new GitArgs("symbolic-ref").Add("--short", "--quiet", "HEAD"));

        if (branch.Succeeded)
        {
            return branch.Output.Trim();
        }

        GitResult commit = _git.Read(new GitArgs("rev-parse").Add("--short", "HEAD"));
        return commit.Succeeded ? $"detached at {commit.Output.Trim()}" : "";
    }

    /// <summary>
    ///  The files a commit changed, relative to its first parent. Blocks — call from a background thread.
    /// </summary>
    public IReadOnlyList<GitChange> GetChangedFiles(GitCommit commit, CancellationToken cancellationToken)
    {
        if (!commit.HasParent)
        {
            // A root commit has nothing to diff against; listing its whole tree isn't what the pane is for.
            return [];
        }

        return ReadChanges(commit.ParentIds[0], commit.Id, cancellationToken);
    }

    private IReadOnlyList<GitChange> ReadChanges(string from, string to, CancellationToken cancellationToken) =>
        GitDiff.ParseChanges(_git.Read(GitDiff.Changes(from, to), cancellationToken).Output);

    /// <summary>
    ///  The unified diff for one file in a commit, against its first parent.
    /// </summary>
    public async Task<string> GetDiffTextAsync(GitCommit commit, string fileName, string? oldFileName, CancellationToken cancellationToken)
    {
        if (!commit.HasParent)
        {
            return "";
        }

        return await ReadPatchAsync(commit.ParentIds[0], commit.Id, fileName, oldFileName, cancellationToken);
    }

    /// <summary>
    ///  <c>git blame</c> for a file, as of a revision, one entry per line.
    /// </summary>
    /// <remarks>
    ///  Remembered when a revision is named, because blaming a file at a commit is a question about
    ///  two immutable things and can only ever have one answer. Without a revision it is the working
    ///  tree, which changes under us, so that goes to git every time.
    /// </remarks>
    public GitBlameResult GetBlame(string fileName, string? revision, CancellationToken cancellationToken = default)
    {
        bool isFixedRevision = !string.IsNullOrEmpty(revision);
        (string, string) key = (revision ?? "", fileName);

        if (isFixedRevision && _blames.TryGetValue(key, out GitBlameResult? remembered))
        {
            return remembered;
        }

        GitBlameResult result = GitBlame.Read(_git, fileName, revision, cancellationToken);

        // Only an answer is remembered; a failure may be the file not being there yet.
        if (isFixedRevision && result.Succeeded)
        {
            if (_blames.Count >= MaxRememberedBlames)
            {
                _blames.Clear();
            }

            _blames[key] = result;
        }

        return result;
    }

    public IReadOnlyList<string> GetLocalBranches() =>
    [
        .. _git.Read(new GitArgs("for-each-ref").Add("--format=%(refname:short)", "refs/heads"))
            .Lines()
            .Order(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    ///  Everything uncommitted, one entry per side of the index.
    /// </summary>
    /// <remarks>
    ///  Read and parsed by Gitxen's own layer. A file that was changed, staged and then changed
    ///  again yields two entries, which is what lets the Changes page list it under both headings.
    /// </remarks>
    public IReadOnlyList<GitChange> GetWorkingDirectoryChanges() =>
        GitStatus.Parse(_git.Read(GitStatus.Command()).Output);

    /// <summary>
    ///  The diff for an uncommitted file. Unlike a commit diff there is no revision pair — it is the
    ///  index against HEAD, or the working tree against the index — and nothing is remembered,
    ///  because both sides change under us.
    /// </summary>
    /// <param name="untracked">
    ///  A file git has never been told about, which plain <c>git diff</c> is silent on; it is shown as
    ///  an addition of its whole content instead.
    /// </param>
    public string GetWorkingDirectoryDiff(string fileName, string? oldFileName, bool staged, bool untracked = false)
    {
        if (untracked && !staged)
        {
            // --no-index exits 1 whenever the sides differ, which here is always: judge by output.
            GitResult added = _git.Read(GitDiff.UntrackedPatch(fileName));
            return added.Text.Length > 0 ? added.Text : added.Error;
        }

        GitResult result = _git.Read(GitDiff.WorkingTreePatch(fileName, oldFileName, staged));
        return result.Succeeded ? result.Text : result.Error;
    }

    /// <summary>The files that differ between two commits, for comparing them directly.</summary>
    public IReadOnlyList<GitChange> GetChangedFilesBetween(string first, string second, CancellationToken cancellationToken) =>
        ReadChanges(first, second, cancellationToken);

    /// <summary>One file's diff between two arbitrary commits.</summary>
    public Task<string> GetDiffTextBetweenAsync(string first, string second, string fileName, string? oldFileName, CancellationToken cancellationToken) =>
        ReadPatchAsync(first, second, fileName, oldFileName, cancellationToken);

    /// <summary>
    ///  One file's patch between two commits, remembered.
    /// </summary>
    /// <remarks>
    ///  Two commit ids and a path can only ever have one diff, so nothing that happens to the
    ///  repository can make a remembered answer wrong — unlike the listings, this needs no watcher to
    ///  be safe. Bounded, because a long browse through history would otherwise keep every patch it
    ///  touched, and only successful reads are kept so a transient failure is not replayed.
    /// </remarks>
    private async Task<string> ReadPatchAsync(string from, string to, string fileName, string? oldFileName, CancellationToken cancellationToken)
    {
        PatchKey key = new(from, to, fileName, oldFileName);

        if (_patches.TryGetValue(key, out string? remembered))
        {
            return remembered;
        }

        GitResult result = await _git.ReadAsync(
            GitDiff.Patch(from.ToString(), to.ToString(), fileName, oldFileName),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result.Error;
        }

        if (_patches.Count >= MaxRememberedPatches)
        {
            _patches.Clear();
        }

        _patches[key] = result.Text;
        return result.Text;
    }

    private readonly record struct PatchKey(string From, string To, string Path, string? OldPath);
}
