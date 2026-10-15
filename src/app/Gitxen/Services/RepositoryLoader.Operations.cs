// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Gitxen.Git;
using Gitxen.Models;

namespace Gitxen.Services;

/// <summary>
///  Everything that changes the repository: staging, committing, branching, stashing, syncing.
/// </summary>
/// <remarks>
///  <para>
///   Built from <c>git help &lt;command&gt;</c> and run through <see cref="GitCli.Write"/>. Arguments
///   reach git one at a time, so a branch name, a message or a path with spaces or quotes in it
///   arrives intact — nothing here is quoted by hand.
///  </para>
///  <para>
///   Every method returns a <see cref="GitOperationResult"/> rather than throwing: a rejected push, a
///   dirty worktree or nothing to fetch is information for the person using the app, and the repository
///   page reports it through its <c>InfoBar</c>.
///  </para>
/// </remarks>
internal sealed partial class RepositoryLoader
{
    // ---- Staging ------------------------------------------------------------------------------

    public GitOperationResult StageFile(string fileName) =>
        Run($"Stage {fileName}", new GitArgs("add").AddPaths(fileName));

    public GitOperationResult UnstageFile(string fileName) =>
        Run($"Unstage {fileName}", new GitArgs("reset").Add("--quiet").AddPaths(fileName));

    /// <summary>Stages everything, including untracked files, which is what the Changes page offers.</summary>
    public GitOperationResult StageAll()
    {
        IReadOnlyList<GitChange> unstaged = [.. GetWorkingDirectoryChanges().Where(change => !change.IsStaged)];

        if (unstaged.Count == 0)
        {
            return new GitOperationResult("Stage all", true, "Nothing to stage.");
        }

        // --all covers modifications, deletions and untracked files alike, which is what the page's
        // one button promises.
        GitResult result = _git.Write(new GitArgs("add").Add("--all"));

        return result.Succeeded
            ? new GitOperationResult("Stage all", true, $"Staged {unstaged.Count} file(s).")
            : new GitOperationResult("Stage all", false, result.AllOutput.Trim());
    }

    public GitOperationResult UnstageAll() =>
        Run("Unstage all", new GitArgs("reset").Add("--quiet"));

    /// <summary>
    ///  Applies a patch to the index, forwards to stage it or reversed to unstage it.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This is how partial staging works: git has no command that stages part of a file, so a patch
    ///   containing just the wanted hunk is applied to the index with <c>--cached</c>. The working
    ///   tree is untouched either way — only what is staged changes.
    ///  </para>
    ///  <para>
    ///   The patch goes through a temp file rather than stdin so that its exact bytes, including the
    ///   trailing newline git insists on, reach the command unmodified.
    ///  </para>
    /// </remarks>
    public GitOperationResult ApplyToIndex(string patch, bool reverse, string description)
    {
        string patchFile = Path.Combine(Path.GetTempPath(), $"gitxen-hunk-{Guid.NewGuid():N}.patch");

        try
        {
            // Written as UTF-8 without a BOM: git treats a BOM as file content and the patch stops
            // matching.
            File.WriteAllText(patchFile, patch, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Hunks taken out of a larger diff carry the surrounding context but not always the
            // whitespace git would prefer; without --whitespace=nowarn a stage can fail on an
            // unrelated line.
            return Run(description, new GitArgs("apply")
                .Add("--cached")
                .AddIf(reverse, "--reverse")
                .Add("--whitespace=nowarn", "--", patchFile));
        }
        catch (IOException ex)
        {
            return new GitOperationResult(description, false, ex.Message);
        }
        finally
        {
            DeleteQuietly(patchFile);
        }
    }

    /// <summary>
    ///  Throws away a file's uncommitted changes. An untracked file has nothing to restore it from, so
    ///  discarding it means deleting it — a different command, and the caller must have confirmed.
    /// </summary>
    /// <summary>Files with unresolved merge conflicts.</summary>
    public IReadOnlyList<string> GetConflictedFiles() =>
        [.. _git.Read(new GitArgs("diff").Add("--name-only", "--diff-filter=U")).Lines()];

    /// <summary>Marks a conflict resolved by staging the file, which is what git means by resolved.</summary>
    public GitOperationResult MarkResolved(string fileName) =>
        Run($"Resolve {fileName}", new GitArgs("add").AddPaths(fileName));

    // ---- Branches -----------------------------------------------------------------------------

    /// <summary>Creates a branch at HEAD, for when no specific commit is selected.</summary>
    public GitOperationResult CreateBranchFromHead(string branchName, bool checkout) =>
        Run($"Create branch {branchName}", BranchCommand(branchName, checkout).Add(branchName));

    /// <summary>Creates a branch at any resolvable reference, including a reflog selector.</summary>
    public GitOperationResult CreateBranchAt(string branchName, string reference, bool checkout) =>
        Run($"Create branch {branchName}", BranchCommand(branchName, checkout).Add(branchName, reference));

    /// <summary>
    ///  <c>checkout -b</c> creates and switches in one step; <c>branch</c> alone creates and stays put.
    /// </summary>
    private static GitArgs BranchCommand(string branchName, bool checkout) =>
        checkout ? new GitArgs("checkout").Add("-b") : new GitArgs("branch");

    public GitOperationResult RenameBranch(string oldName, string newName) =>
        Run($"Rename {oldName}", new GitArgs("branch").Add("-m", oldName, newName));

    /// <summary>Points a local branch at an upstream, for a branch created before its remote existed.</summary>
    public GitOperationResult SetUpstream(string branch, string upstream) =>
        Run($"Track {upstream}", new GitArgs("branch").Add($"--set-upstream-to={upstream}", branch));

    public GitOperationResult Checkout(string branch) =>
        Run($"Checkout {branch}", new GitArgs("checkout").Add(branch));

    /// <summary>Checks out a tag or commit, which necessarily leaves HEAD detached.</summary>
    public GitOperationResult CheckoutDetached(string reference) =>
        Run($"Checkout {reference}", new GitArgs("checkout").Add("--detach", reference));

    public GitOperationResult MergeBranch(string branchName) =>
        Run($"Merge {branchName}", new GitArgs("merge").Add(branchName));

    /// <summary>Merge with the strategy choices: keep the merge commit, or fold the work in flat.</summary>
    public GitOperationResult MergeBranch(string branchName, MergeOptions options) =>
        Run($"Merge {branchName}", new GitArgs("merge")
            .AddIf(options.NoFastForward && !options.Squash, "--no-ff")
            .AddIf(options.Squash, "--squash")
            .AddIf(options.NoCommit && !options.Squash, "--no-commit")
            .AddIf(!string.IsNullOrWhiteSpace(options.Message), "-m", options.Message)
            .Add(branchName));

    // ---- Stashes ------------------------------------------------------------------------------

    public GitOperationResult StashSave(string message) =>
        StashSave(message, includeUntracked: false, keepIndex: false);

    /// <summary>Stash with the options that decide what actually gets set aside.</summary>
    public GitOperationResult StashSave(string message, bool includeUntracked, bool keepIndex) =>
        Run("Stash", new GitArgs("stash")
            .Add("push")
            .AddIf(includeUntracked, "--include-untracked")
            .AddIf(keepIndex, "--keep-index")
            .AddIf(!string.IsNullOrWhiteSpace(message), "-m", message));

    public GitOperationResult StashList() =>
        Run("Stash list", new GitArgs("stash").Add("list"));

    public GitOperationResult StashPop() =>
        Run("Stash pop", new GitArgs("stash").Add("pop"));

    /// <summary>
    ///  Stash entries are addressed by reflog selector (<c>stash@{1}</c>), which is why the pages pass
    ///  a reference through rather than an index — the selector is what git accepts.
    /// </summary>
    public GitOperationResult StashApply(string reference) =>
        Run($"Apply {reference}", new GitArgs("stash").Add("apply", reference));

    public GitOperationResult StashPop(string reference) =>
        Run($"Pop {reference}", new GitArgs("stash").Add("pop", reference));

    /// <summary>The diff a stash entry would apply, for reading before deciding to apply it.</summary>
    public string GetStashDiff(string reference)
    {
        // stash show takes diff options, and wants the same two as every other patch read here.
        GitResult result = _git.Read(new GitArgs("stash").Add("show", "--patch", "--no-color", "--no-ext-diff", reference));

        return result.Succeeded ? result.Text : result.AllOutput;
    }

    // ---- History operations -------------------------------------------------------------------

    public GitOperationResult CherryPick(string commit) =>
        Run($"Cherry-pick {Short(commit)}", new GitArgs("cherry-pick").Add(commit));

    public GitOperationResult Revert(string commit) =>
        Run($"Revert {Short(commit)}", new GitArgs("revert").Add("--no-edit", commit));

    public GitOperationResult Rebase(string onto) =>
        Run($"Rebase onto {onto}", new GitArgs("rebase").Add(onto));

    /// <summary>
    ///  Reset to anything git can resolve: a commit id, or a reflog selector such as <c>HEAD@{2}</c>.
    /// </summary>
    /// <summary>
    ///  Undoes the last commit, keeping its changes staged.
    /// </summary>
    /// <remarks>
    ///  A soft reset, so nothing is lost: the files stay exactly as they were and the commit's own
    ///  message can be recovered from the reflog. This is the "I committed too early" button, not a
    ///  way to discard work.
    /// </remarks>
    public GitOperationResult UndoLastCommit() =>
        ResetTo("HEAD~1", GitResetMode.Soft) with { Description = "Undo last commit" };

    /// <summary>
    ///  Continue/abort/skip for whichever operation is in progress. The command differs per
    ///  operation, so the caller says which one it means.
    /// </summary>
    public GitOperationResult ContinueOperation(string operation, string action) =>
        Run($"{operation} --{action}", new GitArgs(operation).Add($"--{action}"));

    public GitOperationResult Bisect(string action, string? commit = null) =>
        Run($"Bisect {action}", new GitArgs("bisect").Add(action).AddIfNotEmpty(commit));

    // ---- Tags, remotes, submodules, worktrees --------------------------------------------------

    public GitOperationResult ListTags() =>
        Run("Tags", new GitArgs("tag").Add("--list", "--sort=-creatordate"));

    public GitOperationResult CreateTag(string name, string? commit, string message) =>
        Run($"Create tag {name}", new GitArgs("tag")
            .AddIf(!string.IsNullOrWhiteSpace(message), "-a", "-m", message)
            .Add(name)
            .AddIfNotEmpty(commit));

    public GitOperationResult DeleteTag(string name) =>
        Run($"Delete tag {name}", new GitArgs("tag").Add("-d", name));

    public GitOperationResult PushTag(string name) =>
        RunNetwork($"Push tag {name}", Remote(new GitArgs("push").Add(GetRemote(), $"refs/tags/{name}")));

    public GitOperationResult ListRemotes() =>
        Run("Remotes", new GitArgs("remote").Add("-v"));

    public GitOperationResult AddRemote(string name, string url) =>
        Run($"Add remote {name}", new GitArgs("remote").Add("add", name, url));

    public GitOperationResult RemoveRemote(string name) =>
        Run($"Remove remote {name}", new GitArgs("remote").Add("remove", name));

    public GitOperationResult ListSubmodules() =>
        Run("Submodules", new GitArgs("submodule").Add("status", "--recursive"));

    public GitOperationResult UpdateSubmodules() =>
        RunNetwork("Update submodules", Remote(new GitArgs("submodule").Add("update", "--init", "--recursive")));

    /// <summary>Updates one submodule, for when re-initialising every one of them is overkill.</summary>
    public GitOperationResult UpdateSubmodule(string path) =>
        RunNetwork($"Update {path}", Remote(new GitArgs("submodule").Add("update", "--init", "--recursive").AddPaths(path)));

    public GitOperationResult SyncSubmodules() =>
        Run("Sync submodules", new GitArgs("submodule").Add("sync", "--recursive"));

    public GitOperationResult ListWorktrees() =>
        Run("Worktrees", new GitArgs("worktree").Add("list"));

    public GitOperationResult AddWorktree(string path, string branch) =>
        Run($"Add worktree {path}", new GitArgs("worktree").Add("add", path, branch));

    public GitOperationResult RemoveWorktree(string path) =>
        Run($"Remove worktree {path}", new GitArgs("worktree").Add("remove", path));

    /// <summary>Drops worktree administrative entries whose directories are gone.</summary>
    public GitOperationResult PruneWorktrees() =>
        Run("Prune worktrees", new GitArgs("worktree").Add("prune"));

    // ---- Talking to remotes -------------------------------------------------------------------

    public GitOperationResult Fetch() =>
        RunNetwork("Fetch", Remote(new GitArgs("fetch").Add(GetRemote())));

    /// <summary>Fetches one named remote, rather than whichever one the current branch tracks.</summary>
    public GitOperationResult FetchRemote(string remote, bool prune) =>
        RunNetwork($"Fetch {remote}", Remote(new GitArgs("fetch").AddIf(prune, "--prune").Add("--tags", remote)));

    /// <summary>
    ///  Fetches quietly, for the periodic refresh. No progress and no output worth reporting: this
    ///  runs on a timer, and a failure is not something the user asked about.
    /// </summary>
    public GitOperationResult FetchQuietly()
    {
        GitResult result = _git.Write(Remote(new GitArgs("fetch").Add("--quiet", "--all", "--prune")));

        return new GitOperationResult("Background fetch", result.Succeeded, result.AllOutput.Trim());
    }

    /// <remarks>
    ///  Deliberately without a rebase or merge flag: how a diverged branch is reconciled is the
    ///  user's <c>pull.rebase</c> setting, and <see cref="Pull(PullOptions)"/> is where they choose
    ///  otherwise.
    /// </remarks>
    public GitOperationResult Pull() =>
        RunNetwork("Pull", Remote(new GitArgs("pull").Add(GetRemote())));

    /// <summary>
    ///  Pull with the choices the plain one does not offer: rebase instead of merge, and pruning
    ///  remote-tracking branches that no longer exist.
    /// </summary>
    public GitOperationResult Pull(PullOptions options)
    {
        string remote = string.IsNullOrWhiteSpace(options.Remote) ? GetRemote() : options.Remote;

        return RunNetwork($"Pull {remote}", Remote(new GitArgs("pull")
            .AddIf(options.Rebase, "--rebase")
            .AddIf(options.Prune, "--prune")
            .AddIf(options.FastForwardOnly && !options.Rebase, "--ff-only")
            .Add(remote)
            .AddIfNotEmpty(options.RemoteBranch)));
    }

    public GitOperationResult Push(string branch) =>
        RunNetwork("Push", Remote(new GitArgs("push").Add(GetRemote(), branch)));

    /// <summary>
    ///  Push with the options that make it usable on a real branch: force-with-lease, tags, and
    ///  setting the upstream for a branch that has never been pushed.
    /// </summary>
    /// <remarks>
    ///  Force is always <c>--force-with-lease</c>, never a bare <c>--force</c>: the lease refuses the
    ///  push if the remote moved since it was last fetched, which is the difference between rewriting
    ///  your own history and discarding someone else's.
    /// </remarks>
    public GitOperationResult Push(PushOptions options)
    {
        string remote = string.IsNullOrWhiteSpace(options.Remote) ? GetRemote() : options.Remote;
        string target = string.IsNullOrWhiteSpace(options.RemoteBranch)
            ? options.Branch
            : $"{options.Branch}:{options.RemoteBranch}";

        return RunNetwork($"Push to {remote}", Remote(new GitArgs("push")
            .AddIf(options.ForceWithLease, "--force-with-lease")
            .AddIf(options.SetUpstream, "--set-upstream")
            .AddIf(options.PushAllTags, "--tags")
            .Add(remote)
            .AddIf(!options.PushAllTags || !string.IsNullOrWhiteSpace(options.Branch), target)));
    }

    /// <summary>
    ///  Deletes a branch on the remote by pushing an empty ref to it. Nothing local changes.
    /// </summary>
    public GitOperationResult DeleteRemoteBranch(string remote, string branch) =>
        RunNetwork($"Delete {remote}/{branch}", Remote(new GitArgs("push").Add(remote, "--delete", branch)));

    // ---- Housekeeping -------------------------------------------------------------------------

    /// <summary>
    ///  Removes untracked files. Directories and ignored files are opt-in because <c>git clean</c>
    ///  deletes without a reflog to recover from.
    /// </summary>
    public GitOperationResult Clean(bool includeDirectories, bool includeIgnored, bool dryRun) =>
        Run(dryRun ? "Clean (preview)" : "Clean", new GitArgs("clean")
            .Add(dryRun ? "--dry-run" : "--force")
            .AddIf(includeDirectories, "-d")
            .AddIf(includeIgnored, "-x"));

    /// <summary>Repacks and prunes stale objects.</summary>
    public GitOperationResult CollectGarbage() =>
        Run("Garbage collect", new GitArgs("gc").Add("--auto"));

    /// <summary>Writes the working tree of a revision to an archive.</summary>
    public GitOperationResult Archive(string reference, string outputPath) =>
        Run($"Archive {reference}", new GitArgs("archive").Add($"--output={outputPath}", reference));

    // ---- Committing ---------------------------------------------------------------------------

    /// <summary>
    ///  Stages every change in the working directory and commits it with <paramref name="message"/>.
    ///  Deliberately all-or-nothing: partial staging is what <see cref="Commit"/> after staging is for,
    ///  and silently committing a subset would be worse than not offering it.
    /// </summary>
    public GitOperationResult CommitAll(string message)
    {
        if (GetWorkingDirectoryChanges().Count == 0)
        {
            return new GitOperationResult("Commit", false, "Nothing to commit — the working directory is clean.");
        }

        GitOperationResult staged = StageAll();

        return staged.Succeeded ? Commit(message, amend: false, signOff: false) : staged;
    }

    /// <summary>The message of HEAD, for pre-filling an amend.</summary>
    public string GetLastCommitMessage()
    {
        GitResult result = _git.Read(new GitArgs("log").Add("-1", "--pretty=%B"));

        return result.Succeeded ? result.Output.TrimEnd() : "";
    }

    /// <summary>
    ///  Commits whatever is currently staged. Staging is the caller's business, which is what makes
    ///  partial commits possible.
    /// </summary>
    public GitOperationResult Commit(string message, bool amend, bool signOff)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitOperationResult("Commit", false, "A commit message is required.");
        }

        // The message is read from a file so an arbitrarily long, multi-line message never has to
        // fit a command line. UTF-8 without a BOM: git would take a BOM as the message's first
        // character.
        string messageFile = Path.Combine(Path.GetTempPath(), $"gitxen-commit-{Guid.NewGuid():N}.txt");
        string before = amend ? Head() : "";

        try
        {
            File.WriteAllText(messageFile, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            GitOperationResult result = Run(amend ? "Amend" : "Commit", new GitArgs("commit")
                .AddIf(amend, "--amend")
                .AddIf(signOff, "--signoff")
                .Add("--file", messageFile));

            return amend ? WithAmendUndo(before, result) : result;
        }
        catch (IOException ex)
        {
            return new GitOperationResult("Commit", false, ex.Message);
        }
        finally
        {
            DeleteQuietly(messageFile);
        }
    }

    // ---- Plumbing for the above ---------------------------------------------------------------

    /// <summary>
    ///  The remote to fetch/pull/push against — the one configured for the current branch where there
    ///  is one, otherwise the repository's first remote.
    /// </summary>
    private string GetRemote()
    {
        string branch = GetCurrentBranch();

        if (branch.Length > 0 && !branch.StartsWith("detached", StringComparison.Ordinal))
        {
            GitResult configured = _git.Read(new GitArgs("config").Add("--get", $"branch.{branch}.remote"));

            if (configured.Succeeded && configured.Output.Trim() is { Length: > 0 } remote)
            {
                return remote;
            }
        }

        return _git.Read(new GitArgs("remote")).Lines().FirstOrDefault() ?? "origin";
    }

    /// <summary>Runs a changing command and reports what it said.</summary>
    /// <summary>
    ///  The GitHub account network commands authenticate as, or null to leave it to git's own
    ///  credential helper. Set by the tab from the repository's account choice.
    /// </summary>
    public GitHubCredential? GitHubCredential { get; set; }

    /// <summary>A command that talks to a remote, with the repository's GitHub account when it has one.</summary>
    private GitArgs Remote(GitArgs arguments) => GitHubCredential is { } credential ? credential.Apply(arguments) : arguments;

    private GitOperationResult Run(string description, GitArgs arguments)
    {
        GitResult result = _git.Write(arguments);
        return new GitOperationResult(description, result.Succeeded, result.AllOutput.Trim());
    }

    /// <summary>The abbreviation shown in messages, matching <see cref="GitCommit.ShortId"/>.</summary>
    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing an operation over.
        }
    }
}
