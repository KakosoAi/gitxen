// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;

namespace Gitxen.Services;

/// <summary>
///  What the application knows about the GitHub CLI: whether it is there, and whether it is signed in.
/// </summary>
/// <remarks>
///  <para>
///   One answer per process rather than one per repository. Whether gh is installed and which account
///   it is using are facts about the machine, and asking again for every tab and every page would run
///   two commands a piece to learn the same thing. It is asked once, on first use, remembered, and
///   asked again only when told to — after the person has signed in, or changed which gh to use.
///  </para>
///  <para>
///   Everything after that is per repository and belongs to <c>GitHubRepositoryViewModel</c>.
///  </para>
/// </remarks>
internal static class GitHubService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static GhStatus? _status;

    /// <summary>The last answer, or null before the first check has finished.</summary>
    public static GhStatus? Current => _status;

    /// <summary>Raised when a check finishes, so pages showing the answer can update.</summary>
    public static event Action? StatusChanged;

    /// <summary>
    ///  Raised when a setting that decides whether GitHub is offered has changed — switched on or
    ///  off, or pointed at a different gh.
    /// </summary>
    /// <remarks>
    ///  Every open repository listens. The setting is the application's, but what it changes — the
    ///  three navigation entries — belongs to each repository, and there can be several open.
    /// </remarks>
    public static event Action? SettingsChanged;

    public static void NotifySettingsChanged()
    {
        Invalidate();
        SettingsChanged?.Invoke();
    }

    /// <summary>A runner for one repository's working directory, reporting to the activity console.</summary>
    public static GhCli CreateCli(string workingDirectory)
    {
        GhCli cli = new(workingDirectory, AppOptions.GhPath);
        cli.CommandCompleted += (_, command) => GitActivity.Record(command);
        return cli;
    }

    /// <summary>
    ///  Whether gh can be used, checked at most once unless <paramref name="refresh"/> is set.
    /// </summary>
    /// <remarks>
    ///  Callers that arrive while a check is running wait for it and share its answer rather than each
    ///  starting their own — three sections opened in quick succession would otherwise mean three
    ///  identical pairs of processes.
    /// </remarks>
    public static async Task<GhStatus> GetStatusAsync(bool refresh = false)
    {
        if (!refresh && _status is GhStatus known)
        {
            return known;
        }

        await _gate.WaitAsync();

        try
        {
            if (!refresh && _status is GhStatus alreadyKnown)
            {
                return alreadyKnown;
            }

            GhStatus checkedNow = await GitHubClient.CheckAsync(CreateCli(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            _status = checkedNow;
            StatusChanged?.Invoke();
            return checkedNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the answer, so the next question is asked of gh again.</summary>
    public static void Invalidate() => _status = null;
}
