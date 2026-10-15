// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>
///  Watches a repository's <c>.git</c> directory and reports that something in it changed.
/// </summary>
/// <remarks>
///  <para>
///   This exists so that a listing can be read once and reused until it is actually out of date.
///   Without a signal like this there is no safe way to reuse anything about refs: a repository can
///   be changed from a terminal, another client, or a script while the window is open, and a stale
///   branch list is a wrong answer rather than a slow one.
///  </para>
///  <para>
///   Over-reporting is fine and under-reporting is not, so this errs towards noise: everything under
///   <c>.git</c> is watched, a lost-events overflow is reported as a change like any other, and only
///   the object database — which no listing reads — is filtered out. Each report costs a re-read of
///   whatever the user next looks at, which is exactly what happened before this existed.
///  </para>
///  <para>
///   The WinForms app has equivalents in <c>GitStatusMonitor</c> and <c>IndexWatcher</c>, but both
///   live in GitUI, which this app deliberately does not reference.
///  </para>
/// </remarks>
internal sealed class RepositoryWatcher : IDisposable
{
    /// <summary>
    ///  Raised, off the UI thread, when something under <c>.git</c> changed. Marshal before using it.
    /// </summary>
    public event EventHandler? Changed;

    private readonly FileSystemWatcher? _watcher;

    public RepositoryWatcher(string workingDirectory)
    {
        try
        {
            string gitDirectory = Path.Combine(workingDirectory, ".git");

            // A worktree or submodule keeps .git as a file pointing elsewhere. Following it is
            // possible but it is one more thing to get wrong, and the fallback — reading every time,
            // as the app did before — is correct, merely slower.
            if (!Directory.Exists(gitDirectory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(gitDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,

                // The default 8KB buffer overflows easily during a fetch or a checkout. An overflow
                // is handled safely below, but it is worth making it rare.
                InternalBufferSize = 64 * 1024
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            // Events were dropped, so what is on disk can no longer be inferred from what arrived.
            // The only safe reading of that is "assume everything changed".
            _watcher.Error += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path the platform will not watch — a network share, a permission problem. Callers
            // check IsWatching and keep reading from git every time.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    ///  Whether changes are actually being reported. When false, nothing may be reused.
    /// </summary>
    public bool IsWatching => _watcher is not null;

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // The object database is most of the write traffic — a fetch writes thousands of files — and
        // no listing reads it. Refs, config, logs and the index all live outside it.
        if (IsInObjectDatabase(e.Name))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsInObjectDatabase(string? relativePath) =>
        relativePath is not null
        && (relativePath.StartsWith("objects", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("lfs", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        // Stopped before disposing: a queued event delivered during teardown would otherwise reach a
        // view model that has already been closed.
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
