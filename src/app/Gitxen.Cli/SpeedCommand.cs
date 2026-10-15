// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary><c>gx speedup</c>: the same speed-ups as Maintenance, written to this repository's config only.</summary>
internal static class SpeedCommand
{
    public static int Run(Arguments arguments)
    {
        string root = Program.RepositoryRoot(arguments[0]);
        GitCli git = new(root);
        SpeedSettings before = RepositorySpeed.Read(git, root);

        if (!arguments.Has("status"))
        {
            if (arguments.Has("no-watcher") && before.FsMonitor)
            {
                RepositorySpeed.StopFileWatcher(git);
            }

            bool watcher = arguments.Has("watcher") || (before.FsMonitor && !arguments.Has("no-watcher"));

            if (watcher && !before.FsMonitorPossible)
            {
                throw new UsageException("git's file watcher cannot run on a network drive.");
            }

            GitOperationResult result = RepositorySpeed.Apply(git, watcher);

            if (!result.Succeeded)
            {
                Output.Error(result.Output);
                return 1;
            }
        }

        SpeedSettings now = RepositorySpeed.Read(git, root);
        Show("commit-graph", now.CommitGraph);
        Show("kept up by fetch", now.FetchWritesGraph);
        Show("untracked cache", now.UntrackedCache);
        Show("file watcher", now.FsMonitor, now.FsMonitorPossible ? (now.WantsFsMonitor && !now.FsMonitor ? "worth it here: gx speedup --watcher" : "") : "not on a network drive");
        string commits = now.Commits >= SpeedSettings.LargeCommits ? $"{now.Commits:N0}+ commits" : now.Commits == 1 ? "1 commit" : $"{now.Commits:N0} commits";
        Output.Line(Output.Dim($"  {commits}, {now.TrackedFiles:N0} files"));
        return 0;
    }

    private static void Show(string name, bool on, string note = "") =>
        Output.Line($"  {name,-18} {(on ? Output.Green("on ") : Output.Yellow("off"))}  {Output.Dim(note)}");
}
