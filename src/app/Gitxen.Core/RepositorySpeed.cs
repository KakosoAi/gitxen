// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>Which of git's own speed-ups a repository has, and how big it is.</summary>
/// <param name="CommitGraph">A commit-graph file exists, so history walks read it instead of every commit object.</param>
/// <param name="FetchWritesGraph"><c>fetch.writeCommitGraph</c>: every fetch keeps the graph up to date.</param>
/// <param name="Commits">Commits reachable from HEAD, counted only up to <see cref="LargeCommits"/>.</param>
/// <param name="FsMonitorPossible">git's file watcher can run here: a local drive, not a network share.</param>
internal sealed record SpeedSettings(
    bool CommitGraph,
    bool FetchWritesGraph,
    bool UntrackedCache,
    bool FsMonitor,
    int Commits,
    int TrackedFiles,
    bool FsMonitorPossible)
{
    public const int LargeCommits = 5_000;
    public const int LargeFiles = 20_000;

    /// <summary>Large enough that the speed-ups are worth offering unasked.</summary>
    public bool IsLarge => Commits >= LargeCommits || TrackedFiles >= LargeFiles;

    /// <summary>The file watcher only earns its background process on a working tree this size.</summary>
    public bool WantsFsMonitor => TrackedFiles >= LargeFiles && FsMonitorPossible;

    public bool IsComplete => CommitGraph && FetchWritesGraph && UntrackedCache && (FsMonitor || !WantsFsMonitor);
}

/// <summary>
///  git's own speed-ups for large repositories: a commit-graph, the untracked cache and the file
///  watcher.
/// </summary>
/// <remarks>
///  <para>
///   Measured before adding: on cli/cli (12k commits) the commit-graph with changed-path filters took
///   <c>rev-list --count</c> from 136 to 72 ms, <c>log --graph -n 500</c> from 145 to 82 ms and a
///   file's history from 205 to 80 ms. On a 60,000-file working tree the untracked cache and the
///   watcher took <c>status</c> from about 105 to 80 ms on a fast SSD — more on slower disks.
///  </para>
///  <para>
///   Everything is written to the repository's own config, nowhere else. The file watcher is a
///   background process git starts per repository, so it is offered only for big working trees and
///   never on a network drive, where git refuses to run it.
///  </para>
/// </remarks>
internal static class RepositorySpeed
{
    public static SpeedSettings Read(GitCli git, string workingDirectory)
    {
        GitResult paths = git.Read(new GitArgs("rev-parse").Add(
            "--git-path", "objects/info/commit-graph",
            "--git-path", "objects/info/commit-graphs/commit-graph-chain"));

        bool graph = paths.Succeeded && paths.Output.Split('\n')
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Any(path => File.Exists(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path)));

        GitResult count = git.Read(new GitArgs("rev-list").Add("--count", $"--max-count={SpeedSettings.LargeCommits}", "HEAD"));
        int commits = count.Succeeded && int.TryParse(count.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

        GitResult files = git.Read(new GitArgs("ls-files").Add("-z"));
        int tracked = files.Succeeded ? files.OutputBytes.Count(value => value == 0) : 0;

        return new SpeedSettings(
            graph,
            ConfigFlag(git, "fetch.writeCommitGraph"),
            ConfigFlag(git, "core.untrackedCache"),
            ConfigFlag(git, "core.fsmonitor"),
            commits,
            tracked,
            IsLocalDrive(workingDirectory));
    }

    /// <summary>
    ///  Turns the speed-ups on in this repository's config, and writes the commit-graph now rather than
    ///  waiting for the next fetch.
    /// </summary>
    public static GitOperationResult Apply(GitCli git, bool fileWatcher)
    {
        const string Description = "Speed up this repository";
        List<(string Key, string Value)> settings =
        [
            ("core.commitGraph", "true"),
            ("fetch.writeCommitGraph", "true"),
            ("core.untrackedCache", "true"),
        ];

        if (fileWatcher)
        {
            settings.Add(("core.fsmonitor", "true"));
        }

        foreach ((string key, string value) in settings)
        {
            GitResult set = git.Write(new GitArgs("config").Add("--local", key, value));

            if (!set.Succeeded)
            {
                return new GitOperationResult(Description, false, set.AllOutput.Trim());
            }
        }

        GitResult written = git.Write(new GitArgs("commit-graph").Add("write", "--reachable", "--changed-paths"));

        return written.Succeeded
            ? new GitOperationResult(Description, true,
                "History reads now use a commit-graph, kept up to date by every fetch, and the untracked cache is on." +
                (fileWatcher ? " git's file watcher starts with the next status." : ""))
            : new GitOperationResult(Description, false, written.AllOutput.Trim());
    }

    /// <summary>Stops git's file watcher here and turns it off. The other speed-ups stay.</summary>
    public static GitOperationResult StopFileWatcher(GitCli git)
    {
        git.Write(new GitArgs("config").Add("--local", "--unset", "core.fsmonitor"));
        GitResult stopped = git.Write(new GitArgs("fsmonitor--daemon").Add("stop"));

        // "not running" is exit 1 and is the state wanted anyway.
        return new GitOperationResult("File watcher", true, stopped.Succeeded ? "Stopped git's file watcher for this repository." : "");
    }

    private static bool IsLocalDrive(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A boolean setting as git reads it from every scope, normalised by <c>--type=bool</c>.</summary>
    private static bool ConfigFlag(GitCli git, string key)
    {
        GitResult result = git.Read(new GitArgs("config").Add("--type=bool", "--get", key));
        return result.Succeeded && result.Output.Trim() == "true";
    }
}
