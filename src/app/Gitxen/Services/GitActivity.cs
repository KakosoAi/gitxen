// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  Everything Gitxen's own git layer has run, for the activity console.
/// </summary>
/// <remarks>
///  Every <see cref="GitCli"/> the app creates reports here as each command finishes. The bridge lives
///  in the application rather than in <c>Gitxen.Git</c> so that project stays free of anything
///  UI-shaped, and stays a plain library that only knows how to run git.
/// </remarks>
internal static class GitActivity
{
    /// <summary>A tail, not an archive: the console shows what just happened.</summary>
    private const int MaxEntries = 500;

    private static readonly ConcurrentQueue<GitActivityEntry> _entries = new();

    /// <summary>Raised, off the UI thread, whenever a command finishes.</summary>
    public static event Action? Changed;

    public static IEnumerable<GitActivityEntry> Entries => _entries;

    public static void Record(GitCommandCompleted command) =>
        Record(command.CommandLine, command.WorkingDirectory, command.ExitCode, command.Duration, "Gitxen.Git");

    /// <summary>A <c>gh</c> command, shown in the same list as git's so one console tells the whole story.</summary>
    public static void Record(Gitxen.GitHub.GhCommandCompleted command) =>
        Record(command.CommandLine, command.WorkingDirectory, command.ExitCode, command.Duration, "Gitxen.GitHub");

    private static void Record(string commandLine, string workingDirectory, int exitCode, TimeSpan duration, string source)
    {
        _entries.Enqueue(new GitActivityEntry(
            DateTime.Now,
            commandLine,
            workingDirectory,
            exitCode,
            duration,
            source));

        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }

        Changed?.Invoke();
    }

    public static void Clear()
    {
        _entries.Clear();
        Changed?.Invoke();
    }
}

/// <summary>
///  One row in the activity console.
/// </summary>
internal sealed record GitActivityRow(string ColumnLine, string CommandLine, string Detail);

/// <summary>One command Gitxen's own layer ran.</summary>
internal sealed record GitActivityEntry(
    DateTime StartedAt,
    string CommandLine,
    string WorkingDirectory,
    int ExitCode,
    TimeSpan Duration,
    string Source)
{
    /// <summary>The console's row: start time, duration, exit code, then the command.</summary>
    public string ColumnLine =>
        $"{StartedAt:HH:mm:ss.fff} {Duration.TotalMilliseconds,6:0,0} ms {ExitCode,8} {CommandLine}";

    public string Detail =>
        $"""
         Command:     {CommandLine}
         Working dir: {WorkingDirectory}
         Exit code:   {ExitCode}
         Duration:    {Duration.TotalMilliseconds:0.###} ms
         Started at:  {StartedAt:O}
         Source:      {Source}
         """;
}
