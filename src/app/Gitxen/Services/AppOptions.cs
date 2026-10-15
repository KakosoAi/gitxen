// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>
///  Which theme the shell uses.
/// </summary>
/// <remarks>
///  Serialized by name into the session file, so members can be added freely but not renamed.
///  <c>Light</c> and <c>Dark</c> are Gitxen's own two themes and keep those names because sessions
///  written before there were eight themes still hold them.
/// </remarks>
public enum AppTheme
{
    /// <summary>Follow the Windows light/dark setting, using Gitxen Light and Gitxen Dark.</summary>
    System,

    /// <summary>Gitxen Light.</summary>
    Light,

    /// <summary>Gitxen Dark.</summary>
    Dark,

    GitHubDark,
    GitHubLight,
    VisualStudioDark,
    VisualStudioLight,
    CyberpunkDark,
    CyberpunkLight
}

/// <summary>How tightly the object lists pack their rows.</summary>
public enum UiDensity
{
    /// <summary>The shipped spacing.</summary>
    Comfortable,

    /// <summary>Tighter rows: roughly a fifth more commits on the same screen.</summary>
    Compact
}

/// <summary>How the open repositories are listed.</summary>
public enum RepositoryLayout
{
    /// <summary>A tab strip across the top, like a browser.</summary>
    Tabs,

    /// <summary>A column down the left, which suits long repository names and many repositories.</summary>
    Sidebar
}

/// <summary>
///  User-adjustable settings, loaded from the session file at startup.
/// </summary>
/// <remarks>
///  Static because the values are process-wide and read from view models that are created per tab;
///  threading them through every constructor would buy nothing for a single-window personal app.
/// </remarks>
public static class AppOptions
{
    /// <summary>Commits read per page. See RepositoryTabViewModel for why this is capped at all.</summary>
    public static int MaxCommits { get; set; } = 2000;

    public static int MaxDiffLines { get; set; } = 5000;

    public static AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    ///  Minutes between background fetches; 0 turns them off.
    /// </summary>
    /// <remarks>
    ///  Ahead/behind counts come from the remote-tracking refs, which only move when something
    ///  fetches. Without this the toolbar reports "up to date" indefinitely while the remote moves on.
    /// </remarks>
    public static int AutoFetchMinutes { get; set; } = 10;

    /// <summary>Tabs across the top, or a column down the left.</summary>
    public static RepositoryLayout Layout { get; set; } = RepositoryLayout.Tabs;

    /// <summary>
    ///  Width of the staging pane on the Changes page.
    /// </summary>
    /// <remarks>
    ///  Pane widths are options rather than per-repository state: a splitter is dragged to suit the
    ///  monitor and the reader, not the repository, so every tab shares one value. The clamps match
    ///  the PaneSplitter Minimum/Maximum in the XAML that drags them.
    /// </remarks>
    public static double StagingPaneWidth { get; set; } = 400;

    /// <summary>Width of the commit-details pane on the History page.</summary>
    public static double HistoryDetailsWidth { get; set; } = 380;

    /// <summary>Height of the bottom terminal pane.</summary>
    public static double TerminalPaneHeight { get; set; } = 280;

    /// <summary>Width of the History page's file viewer; zero until it is first dragged.</summary>
    public static double HistoryViewerWidth { get; set; }

    /// <summary>Width of the Blame / File history panels beside the History page's diff.</summary>
    public static double HistorySidePaneWidth { get; set; } = 560;

    /// <summary>Whether Blame is open beside the History page's diff.</summary>
    public static bool HistoryBlameOpen { get; set; }

    /// <summary>Whether File history is open beside the History page's diff.</summary>
    public static bool HistoryFileHistoryOpen { get; set; }

    /// <summary>Whether the app opens on the quick launcher rather than on Home.</summary>
    public static bool StartWithLauncher { get; set; } = true;

    /// <summary>Whether a repository's tab shows its current branch after its name.</summary>
    public static bool TabShowBranch { get; set; } = true;

    /// <summary>Whether a repository's tab shows how many uncommitted changes it has.</summary>
    public static bool TabShowChanges { get; set; } = true;

    /// <summary>Whether a repository's tab shows how far its branch is ahead of and behind its upstream.</summary>
    public static bool TabShowAheadBehind { get; set; }

    /// <summary>Raised when any of the tab-detail options change, so open tabs redraw their headers.</summary>
    public static event EventHandler? TabDetailsChanged;

    public static void NotifyTabDetailsChanged() => TabDetailsChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Row spacing for the object lists and the commit graph.</summary>
    public static UiDensity Density { get; set; } = UiDensity.Comfortable;

    /// <summary>How the commit graph draws a change of lane.</summary>
    public static Graph.GraphLineStyle GraphStyle { get; set; } = Graph.GraphLineStyle.Curved;

    public static Graph.GraphLineWeight GraphWeight { get; set; } = Graph.GraphLineWeight.Regular;

    public static Graph.GraphLaneSpacing GraphSpacing { get; set; } = Graph.GraphLaneSpacing.Regular;

    /// <summary>One colour for every lane, rather than one per line of history.</summary>
    public static bool GraphSingleColour { get; set; }

    /// <summary>Raised when any of the graph's appearance settings change, so open histories redraw.</summary>
    public static event EventHandler? GraphChanged;

    public static void NotifyGraphChanged() => GraphChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>
    ///  How much the window's contents are scaled, as a fraction of the system's own size.
    /// </summary>
    /// <remarks>
    ///  Separate from <see cref="Density"/>, which changes how much padding a row has: this scales
    ///  everything, text and spacing and icons alike, the way a browser's zoom does.
    /// </remarks>
    public static double Zoom { get; set; } = 1.0;

    /// <summary>
    ///  Whether the GitHub sections are offered for repositories hosted on github.com.
    /// </summary>
    /// <remarks>
    ///  On by default: it costs nothing until a GitHub section is opened, and does nothing at all when
    ///  the GitHub CLI is not installed beyond saying so on those pages.
    /// </remarks>
    public static bool GitHubEnabled { get; set; } = true;

    /// <summary>Where the GitHub CLI is. Empty means find <c>gh</c> on PATH, which is where its installer puts it.</summary>
    public static string GhPath { get; set; } = "";

    /// <summary>How many pull requests, issues or runs to read at a time.</summary>
    public static int GitHubListSize { get; set; } = 50;

    /// <summary>
    ///  Which signed-in gh account each GitHub repository is read as, keyed by <c>owner/name</c>.
    /// </summary>
    /// <remarks>
    ///  Keyed by the repository on GitHub rather than by the folder, so every clone of it agrees. A
    ///  repository with no entry uses gh's active account, or failing that the first signed-in account
    ///  that can read it — and that one is then written here, so it is not searched for again.
    /// </remarks>
    private static Dictionary<string, string> _gitHubAccounts = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> _speedUpDeclined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the speed-up suggestion was turned down for this working directory.</summary>
    public static bool IsSpeedUpDeclined(string workingDirectory) => _speedUpDeclined.Contains(workingDirectory);

    public static void DeclineSpeedUp(string workingDirectory) => _speedUpDeclined.Add(workingDirectory);

    public static string GitHubAccountFor(string repository) =>
        _gitHubAccounts.TryGetValue(repository, out string? login) ? login : "";

    /// <summary>An empty login forgets the choice, so the repository is matched automatically again.</summary>
    public static void SetGitHubAccount(string repository, string login)
    {
        if (login.Length == 0)
        {
            _gitHubAccounts.Remove(repository);
        }
        else
        {
            _gitHubAccounts[repository] = login;
        }
    }

    /// <summary>The range the status bar's slider offers, and what a stored value is held to.</summary>
    public const double MinimumZoom = 0.8;

    public const double MaximumZoom = 1.5;

    public static void Apply(SessionState state)
    {
        // Guard against a hand-edited session file putting in something unusable.
        MaxCommits = Math.Clamp(state.MaxCommits, 100, 50_000);
        MaxDiffLines = Math.Clamp(state.MaxDiffLines, 100, 200_000);
        Theme = state.Theme;
        AutoFetchMinutes = Math.Clamp(state.AutoFetchMinutes, 0, 240);
        Layout = state.Layout;
        StagingPaneWidth = Math.Clamp(state.StagingPaneWidth, 300, 800);
        HistoryDetailsWidth = Math.Clamp(state.HistoryDetailsWidth, 260, 900);
        TerminalPaneHeight = Math.Clamp(state.TerminalPaneHeight, 140, 700);
        HistorySidePaneWidth = Math.Clamp(state.HistorySidePaneWidth, 280, 1600);
        HistoryViewerWidth = state.HistoryViewerWidth <= 0 ? 0 : Math.Clamp(state.HistoryViewerWidth, 480, 3000);
        HistoryBlameOpen = state.HistoryBlameOpen;
        HistoryFileHistoryOpen = state.HistoryFileHistoryOpen;
        StartWithLauncher = state.StartWithLauncher;
        TabShowBranch = state.TabShowBranch;
        TabShowChanges = state.TabShowChanges;
        TabShowAheadBehind = state.TabShowAheadBehind;
        Density = state.Density;
        GraphStyle = state.GraphStyle;
        GraphWeight = state.GraphWeight;
        GraphSpacing = state.GraphSpacing;
        GraphSingleColour = state.GraphSingleColour;
        Zoom = Math.Clamp(state.Zoom, MinimumZoom, MaximumZoom);
        GitHubEnabled = state.GitHubEnabled;
        GhPath = state.GhPath ?? "";
        GitHubListSize = Math.Clamp(state.GitHubListSize, 10, 200);
        _gitHubAccounts = new Dictionary<string, string>(state.GitHubAccounts ?? [], StringComparer.OrdinalIgnoreCase);
        _speedUpDeclined = new HashSet<string>(state.SpeedUpDeclined ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static void CopyTo(SessionState state)
    {
        state.MaxCommits = MaxCommits;
        state.MaxDiffLines = MaxDiffLines;
        state.Theme = Theme;
        state.AutoFetchMinutes = AutoFetchMinutes;
        state.Layout = Layout;
        state.StagingPaneWidth = StagingPaneWidth;
        state.HistoryDetailsWidth = HistoryDetailsWidth;
        state.TerminalPaneHeight = TerminalPaneHeight;
        state.HistorySidePaneWidth = HistorySidePaneWidth;
        state.HistoryViewerWidth = HistoryViewerWidth;
        state.HistoryBlameOpen = HistoryBlameOpen;
        state.HistoryFileHistoryOpen = HistoryFileHistoryOpen;
        state.StartWithLauncher = StartWithLauncher;
        state.TabShowBranch = TabShowBranch;
        state.TabShowChanges = TabShowChanges;
        state.TabShowAheadBehind = TabShowAheadBehind;
        state.Density = Density;
        state.GraphStyle = GraphStyle;
        state.GraphWeight = GraphWeight;
        state.GraphSpacing = GraphSpacing;
        state.GraphSingleColour = GraphSingleColour;
        state.Zoom = Zoom;
        state.GitHubEnabled = GitHubEnabled;
        state.GhPath = GhPath;
        state.GitHubListSize = GitHubListSize;
        state.GitHubAccounts = new Dictionary<string, string>(_gitHubAccounts, StringComparer.OrdinalIgnoreCase);
        state.SpeedUpDeclined = [.. _speedUpDeclined];
    }
}
