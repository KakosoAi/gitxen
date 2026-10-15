// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gitxen.Services;

/// <summary>
///  What the shell restores on the next launch.
/// </summary>
public sealed class SessionState
{
    public UiMode Mode { get; set; } = UiMode.Simple;

    /// <summary>Working directories of the tabs that were open, in tab order.</summary>
    public List<string> Repositories { get; set; } = [];

    public int SelectedIndex { get; set; }

    /// <summary>Most-recently-opened repositories, newest first.</summary>
    public List<string> Recent { get; set; } = [];

    /// <summary>
    ///  What the Home list last showed against each repository, so it can be drawn before git has
    ///  been asked anything.
    /// </summary>
    /// <remarks>
    ///  Display-only text, and knowingly one refresh out of date until the background read replaces
    ///  it. That is the whole point: reading it live costs a git process per repository, and Home
    ///  would otherwise sit empty for as long as that takes on every launch.
    /// </remarks>
    public List<SavedRepositoryDetail> RecentDetails { get; set; } = [];

    public int MaxCommits { get; set; } = 2000;

    public int MaxDiffLines { get; set; } = 5000;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public RepositoryLayout Layout { get; set; } = RepositoryLayout.Tabs;

    public int AutoFetchMinutes { get; set; } = 10;

    /// <summary>Repository groups, in display order.</summary>
    public List<SavedGroup> Groups { get; set; } = [];

    /// <summary>Per-repository state, so each one reopens the way it was left.</summary>
    public List<SavedRepositoryState> RepositoryStates { get; set; } = [];

    /// <summary>Width of the repository column, in effective pixels.</summary>
    public double SidebarWidth { get; set; } = 280;

    /// <summary>Whether the repository column was collapsed to its rail.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Dragged pane widths, shared by every tab. See AppOptions for why they are not per-repository.</summary>
    public double StagingPaneWidth { get; set; } = 400;

    public double HistoryDetailsWidth { get; set; } = 380;

    /// <summary>Height of the bottom terminal pane.</summary>
    public double TerminalPaneHeight { get; set; } = 280;

    /// <summary>Width of the History page's file viewer; zero means not yet chosen.</summary>
    public double HistoryViewerWidth { get; set; }

    /// <summary>The History page's Blame / File history panels: their width, and which are open.</summary>
    public double HistorySidePaneWidth { get; set; } = 560;

    public bool HistoryBlameOpen { get; set; }

    public bool HistoryFileHistoryOpen { get; set; }

    /// <summary>Whether the app opens on the quick launcher rather than on Home.</summary>
    public bool StartWithLauncher { get; set; } = true;

    /// <summary>What a repository's tab shows after its name.</summary>
    public bool TabShowBranch { get; set; } = true;

    public bool TabShowChanges { get; set; } = true;

    public bool TabShowAheadBehind { get; set; }

    public UiDensity Density { get; set; } = UiDensity.Comfortable;

    /// <summary>The commit graph's appearance.</summary>
    public Graph.GraphLineStyle GraphStyle { get; set; } = Graph.GraphLineStyle.Curved;

    public Graph.GraphLineWeight GraphWeight { get; set; } = Graph.GraphLineWeight.Regular;

    public Graph.GraphLaneSpacing GraphSpacing { get; set; } = Graph.GraphLaneSpacing.Regular;

    public bool GraphSingleColour { get; set; }

    /// <summary>How much the whole window is scaled, as a fraction: 1.0 is the system's own size.</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Whether pull requests, issues and Actions are offered for repositories on GitHub.</summary>
    public bool GitHubEnabled { get; set; } = true;

    /// <summary>Where the GitHub CLI is; empty means find it on PATH.</summary>
    public string GhPath { get; set; } = "";

    /// <summary>How many pull requests, issues or runs to read at a time.</summary>
    public int GitHubListSize { get; set; } = 50;

    /// <summary>The gh account each GitHub repository is read as, by <c>owner/name</c>.</summary>
    public Dictionary<string, string>? GitHubAccounts { get; set; }

    /// <summary>Repositories whose owner said "not now" to the large-repository speed-ups.</summary>
    public List<string>? SpeedUpDeclined { get; set; }

    public WindowBounds? Window { get; set; }

    /// <summary>
    ///  Whether the first-run wizard has been through, either finished or skipped.
    /// </summary>
    /// <remarks>
    ///  Skipping counts. Being asked again on every launch after saying no once is worse than never
    ///  having asked.
    /// </remarks>
    public bool HasCompletedSetup { get; set; }

    /// <summary>The installation the wizard was last shown for; see <c>InstallInfo.FirstRunToken</c>.</summary>
    public string SetupToken { get; set; } = "";
}

/// <summary>
///  The Home list's last-known text for one repository.
/// </summary>
public sealed class SavedRepositoryDetail
{
    public string Path { get; set; } = "";

    public string Branch { get; set; } = "";

    public string LastCommit { get; set; } = "";
}

/// <summary>
///  A repository group as it is stored.
/// </summary>
/// <remarks>
///  Repositories are recorded as paths rather than as objects: the group is a statement about which
///  working directories belong together, and it should survive one of them being closed, reopened or
///  temporarily unavailable.
/// </remarks>
public sealed class SavedGroup
{
    public string Name { get; set; } = "";

    public string Glyph { get; set; } = "";

    public string ColorKey { get; set; } = "";

    public bool IsExpanded { get; set; } = true;

    public List<string> Repositories { get; set; } = [];
}

/// <summary>
///  How one repository was left: which section, what was being typed, how the diff was being read.
/// </summary>
/// <remarks>
///  Keyed by working directory rather than by tab position, so state follows the repository across
///  being closed and reopened, and across tabs being reordered by their project.
/// </remarks>
public sealed class SavedRepositoryState
{
    public string Path { get; set; } = "";

    /// <summary>Navigation tag, e.g. "history"; empty falls back to the default section.</summary>
    public string Section { get; set; } = "";

    /// <summary>
    ///  An uncommitted message. Losing a half-written commit message to a restart is the single most
    ///  annoying thing a git client can do, so it is kept with the rest of the state.
    /// </summary>
    public string CommitDraft { get; set; } = "";

    public bool DiffSideBySide { get; set; }

    /// <summary>Whether History was showing every branch or only the current one.</summary>
    public bool AllBranches { get; set; }
}

public sealed class WindowBounds
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>
///  Persists <see cref="SessionState"/> to the user's local app data.
/// </summary>
/// <remarks>
///  Deliberately a private file of its own: this is an
///  experimental front-end and shouldn't write into the settings the real app reads.
/// </remarks>
internal static class SessionStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///  Where the session lives: under the current Windows user's local app data.
    /// </summary>
    /// <remarks>
    ///  Per user rather than per machine, so two accounts keep entirely separate state and nothing
    ///  needs a login. Local rather than roaming: it records window bounds and machine-specific paths,
    ///  which have no business following the user to another machine.
    /// </remarks>
    private static string SessionDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gitxen");

    /// <summary>
    ///  The folder used before the product was named, kept only so state can be carried over.
    /// </summary>
    /// <remarks>
    ///  The literal old name, not the current one. It briefly became "Gitxen" when the project was
    ///  renamed wholesale, which made this the same folder as <see cref="SessionDirectory"/> and the
    ///  migration a no-op that could never fire.
    /// </remarks>
    private static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitExtensions.WinUI");

    private static string SessionFilePath => Path.Combine(SessionDirectory, "session.json");

    /// <summary>
    ///  Carries a session written under the old folder name across to the new one.
    /// </summary>
    /// <remarks>
    ///  Copied rather than moved, and only when the new location has nothing in it: renaming the
    ///  product should not be able to lose someone's groups and layout, and leaving the old file in
    ///  place means a build from before the rename still finds its own state.
    /// </remarks>
    private static void MigrateLegacySession()
    {
        try
        {
            string current = SessionFilePath;
            string legacy = Path.Combine(LegacyDirectory, "session.json");

            if (File.Exists(current) || !File.Exists(legacy))
            {
                return;
            }

            Directory.CreateDirectory(SessionDirectory);
            File.Copy(legacy, current);
        }
        catch (Exception)
        {
            // Starting fresh is a far better outcome than refusing to start.
        }
    }

    public static SessionState Load()
    {
        MigrateLegacySession();

        try
        {
            string path = SessionFilePath;
            if (!File.Exists(path))
            {
                return new SessionState();
            }

            SessionState state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), _options)
                ?? new SessionState();

            return MarkExistingUserAsSetUp(state);
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable session should never stop the app from starting, but it
            // shouldn't look like a first run with no explanation either.
            LogRestoreFailure(ex);
            return new SessionState();
        }
    }

    /// <summary>
    ///  Treats a session written before the wizard existed as one that has already been through it.
    /// </summary>
    /// <remarks>
    ///  The flag is absent from those files and deserialises to false, which would show the first-run
    ///  wizard to someone who has been using the app for months. There is nothing in the file that
    ///  says when it was written, so this reads the next best thing: a session that already names
    ///  repositories, groups or recent paths belongs to someone who is demonstrably past their first
    ///  run.
    /// </remarks>
    private static SessionState MarkExistingUserAsSetUp(SessionState state)
    {
        if (!state.HasCompletedSetup
            && (state.Repositories.Count > 0 || state.Recent.Count > 0 || state.Groups.Count > 0))
        {
            state.HasCompletedSetup = true;
        }

        return state;
    }

    /// <summary>
    ///  Records an unhandled exception next to the session file, then leaves the crash to proceed.
    /// </summary>
    /// <remarks>
    ///  Without this a failure while a page's XAML loads ends the process with a bare 0xC000027B and
    ///  no message at all — and that is exactly when the message is the whole diagnosis, since it
    ///  names the resource or property the markup got wrong. Appended rather than overwritten so an
    ///  earlier crash is not lost to a later one.
    /// </remarks>
    public static void LogCrash(Exception exception)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(SessionFilePath)!, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Failing to write the log must not replace the failure it was going to describe.
        }
    }

    /// <summary>
    ///  Records why a session failed to restore, next to the session file itself.
    /// </summary>
    public static void LogRestoreFailure(Exception exception)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(SessionFilePath)!, "restore-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, exception.ToString());
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    ///  Set once <see cref="Reset"/> has run. From then on <see cref="Save"/> is a no-op, so the
    ///  window closing on the way to the restart cannot write back the state that was just deleted.
    /// </summary>
    private static bool _isReset;

    /// <summary>
    ///  Deletes every piece of stored state, so the next launch is a first run.
    /// </summary>
    /// <remarks>
    ///  The legacy file goes too: leaving it would let <see cref="MigrateLegacySession"/> quietly
    ///  resurrect the pre-rename state on the very launch that was supposed to be fresh. Repositories
    ///  on disk and git configuration are not touched — this file is the only thing the app owns.
    /// </remarks>
    public static void Reset()
    {
        _isReset = true;

        DeleteQuietly(SessionFilePath);
        DeleteQuietly(SessionFilePath + ".tmp");
        DeleteQuietly(Path.Combine(SessionDirectory, "restore-error.log"));
        DeleteQuietly(Path.Combine(LegacyDirectory, "session.json"));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A file that would not delete still loses to the suppressed save: the next launch may
            // restore old state, but nothing worse. Each file is tried on its own so one refusal
            // does not spare the rest.
        }
    }

    /// <summary>
    ///  Writes the session, replacing the previous file only once the new one is complete.
    /// </summary>
    /// <remarks>
    ///  Written to a temporary file and then moved into place. A crash or a power cut part-way through
    ///  a direct write leaves a truncated file, which fails to parse on the next launch and silently
    ///  looks like a first run — everything the user had arranged, gone. The move is atomic, so the
    ///  worst case becomes losing the most recent save rather than all of them.
    /// </remarks>
    public static void Save(SessionState state)
    {
        if (_isReset)
        {
            return;
        }

        try
        {
            string path = SessionFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, _options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing one save is not worth surfacing an error over; the next one will succeed.
        }
    }
}
