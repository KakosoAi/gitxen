// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
using Gitxen.Git;
using Gitxen.Models;
using Microsoft.Data.Sqlite;

namespace Gitxen.Services;

/// <summary>
///  Keeps <see cref="RepositoryIndex"/> up to date in the background, one repository at a time.
/// </summary>
/// <remarks>
///  Nothing waits on it: the window asks for a repository to be looked at and carries on, and the
///  search simply finds more as the index fills. A repository that is already queued is not queued
///  again, one that has not changed costs two git reads, and a failure (a folder that has gone, a git
///  that is busy) is skipped — the index is a cache and the next pass tries again.
/// </remarks>
internal static class SearchIndexer
{
    private static readonly RepositoryIndex Index = new();
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly HashSet<string> Waiting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static int _started;

    /// <summary>Raised on the indexing thread after a repository was updated, so Settings can refresh its figures.</summary>
    public static event Action? Updated;

    /// <summary>Asks for repositories to be brought up to date. Safe to call often.</summary>
    public static void Enqueue(IEnumerable<string> repositoryPaths)
    {
        foreach (string path in repositoryPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            lock (Gate)
            {
                if (!Waiting.Add(path))
                {
                    continue;
                }
            }

            Queue.Writer.TryWrite(path);
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(RunAsync);
        }
    }

    public static Task<IReadOnlyList<IndexedCommit>> SearchAsync(string text, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => Index.Search(text, limit), cancellationToken);

    public static Task<IndexStatus> StatusAsync() => Task.Run(Index.Status);

    /// <summary>Deletes the index; the open and known repositories are queued to fill it again.</summary>
    public static Task RebuildAsync(IEnumerable<string> repositoryPaths)
    {
        string[] paths = [.. repositoryPaths];

        return Task.Run(() =>
        {
            Index.Clear();
            Enqueue(paths);
        });
    }

    public static Task ClearAsync() => Task.Run(Index.Clear);

    /// <summary>Every repository path the window knows: open tabs first, then the recent ones.</summary>
    public static IEnumerable<string> KnownPaths(IEnumerable<string> open, IEnumerable<RecentRepository> recent, IEnumerable<string> recentPaths) =>
        open.Concat(recent.Where(entry => !entry.IsMissing).Select(entry => entry.Path)).Concat(recentPaths)
            .Where(path => Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static async Task RunAsync()
    {
        // Let the window finish starting before the first git process is launched for this.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (Queue.Reader.TryRead(out string? path))
            {
                lock (Gate)
                {
                    Waiting.Remove(path);
                }

                try
                {
                    GitCli git = new(path);
                    Index.Update(git, path);
                    Updated?.Invoke();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Skipped; the next pass tries again.
                }

                // A breath between repositories, so a long list does not keep a core busy.
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }
}
