// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Git;
using Microsoft.UI.Dispatching;

namespace Gitxen.ViewModels;

/// <summary>
///  Carries git's progress reports from the thread reading them to the UI thread, keeping only the
///  latest: a clone sends hundreds of updates a second, and only the newest one is worth drawing.
/// </summary>
internal sealed class ProgressPump(DispatcherQueue queue, Action<GitProgress> apply)
{
    private GitProgress? _latest;
    private int _queued;

    /// <summary>Called on git's reading thread.</summary>
    public void Report(GitProgress progress)
    {
        Volatile.Write(ref _latest, progress);

        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            queue.TryEnqueue(() =>
            {
                Volatile.Write(ref _queued, 0);

                if (Volatile.Read(ref _latest) is GitProgress latest)
                {
                    apply(latest);
                }
            });
        }
    }

    /// <summary>"Receiving objects", with "(server)" when the remote is the one doing it.</summary>
    public static string Phase(GitProgress progress) => progress.IsRemote ? $"{progress.Phase} (server)" : progress.Phase;

    /// <summary>"1,234 of 2,742 · 1.20 MiB | 2.40 MiB/s", or just the count when git gives no total.</summary>
    public static string Detail(GitProgress progress)
    {
        string count = progress.Total > 0
            ? $"{progress.Done.ToString("N0", CultureInfo.CurrentCulture)} of {progress.Total.ToString("N0", CultureInfo.CurrentCulture)}"
            : progress.Done.ToString("N0", CultureInfo.CurrentCulture);

        return progress.Detail.Length > 0 ? $"{count} · {progress.Detail}" : count;
    }
}
