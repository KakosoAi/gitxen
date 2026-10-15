// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Graph;
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gitxen;

public sealed class CommitRowViewModel : ObservableObject
{
    private CommitChecks? _checks;
    private GraphRow _graph = GraphRow.Empty;

    private CommitRowViewModel(string subject, int pendingCount)
    {
        Revision = null;
        IsWorkingDirectory = true;
        ShortHash = "";
        FullHash = "";
        Author = "";
        AuthorEmail = "";
        Subject = subject;
        Body = $"{pendingCount} uncommitted change(s) in the working directory.";
        Date = "";
        RelativeDate = "now";
        PendingCount = pendingCount;
    }

    public CommitRowViewModel(GitCommit revision)
    {
        Revision = revision;
        ShortHash = revision.ShortId;
        FullHash = revision.Id;
        Author = revision.Author;
        AuthorEmail = revision.AuthorEmail;
        Subject = revision.Subject;
        Body = string.IsNullOrWhiteSpace(revision.Body) ? revision.Subject : revision.Body;
        Date = revision.AuthorDate.ToString("f", CultureInfo.CurrentCulture);
        RelativeDate = TimeText.Relative(revision.AuthorDate);
        IsMerge = revision.IsMerge;
    }

    /// <summary>The underlying revision; null for the synthetic working-directory row.</summary>
    public GitCommit? Revision { get; }

    /// <summary>
    ///  Lane layout for this row: plain coordinates, computed as the commit streams in.
    /// </summary>
    /// <remarks>
    ///  <see cref="GraphRow.Empty"/> for the working-directory row, which is not a commit.
    /// </remarks>
    public GraphRow Graph
    {
        get => _graph;
        set
        {
            // Raised even for the same object: the working-directory row keeps GraphRow.Empty, whose
            // width follows the settings, and a redraw has to tell its binding so.
            _graph = value;
            OnPropertyChanged(nameof(GraphSegments));
            OnPropertyChanged(nameof(GraphColumnWidth));
        }
    }

    /// <summary>
    ///  The lane drawing, built the first time the list realises this row.
    /// </summary>
    /// <remarks>
    ///  Bound OneTime from the row template, so the XAML geometry is only ever built for rows that
    ///  are actually shown — a page loads 2,000 of these and displays about thirty.
    /// </remarks>
    public IReadOnlyList<GraphSegment> GraphSegments => Graph.Segments;

    /// <summary>
    ///  Branch/tag chips pointing at this commit. Assigned as rows stream in, from the single
    ///  ref lookup the tab does per reload.
    /// </summary>
    public IReadOnlyList<RefBadge> Refs { get; init; } = [];

    public Visibility RefsVisibility => Refs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Fixed so the lane geometry lines up from row to row.</summary>
    public double RowHeight => CommitGraphBuilder.RowHeight;

    // ---- How the row is dressed. Comfortable density is two lines (subject, then who and when); Compact
    // ---- keeps one, with the author and age at its end, so nothing it showed before is lost.

    private static bool IsCompact => Services.AppOptions.Density == Services.UiDensity.Compact;

    /// <summary>The author's own colour, the same wherever they appear.</summary>
    public Brush AvatarBrush => AuthorStyle.FillFor(Author);

    public Brush AvatarInitialsBrush => AuthorStyle.InitialsBrush;

    public double AvatarSize => IsCompact ? 18 : 28;

    public double AvatarFontSize => IsCompact ? 8 : 11;

    /// <summary>The second line: who, and when.</summary>
    public string MetaLine => IsWorkingDirectory ? "not committed yet" : $"{Author}  ·  {RelativeDate}";

    /// <summary>Merges recede a little: they are bookkeeping, and the commits that carry work should lead.</summary>
    public double SubjectOpacity => IsMerge ? 0.72 : 1;

    public Visibility MetaVisibility => IsCompact ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Compact density has no second line, so the same text trails the subject instead.</summary>
    public Visibility CompactMetaVisibility => IsCompact ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HashChipVisibility => IsWorkingDirectory || IsCompact ? Visibility.Collapsed : Visibility.Visible;

    public double GraphColumnWidth => Graph.Width;

    /// <summary>
    ///  True for the pseudo-row at the top of the list representing uncommitted changes.
    /// </summary>
    public bool IsWorkingDirectory { get; }

    /// <summary>Number of uncommitted changes; only meaningful on the working-directory row.</summary>
    public int PendingCount { get; }

    /// <summary>A merge commit, drawn with a distinguishing glyph.</summary>
    public bool IsMerge { get; }

    public string ShortHash { get; }

    public string FullHash { get; }

    public string Author { get; }

    public string AuthorEmail { get; }

    public string Subject { get; }

    public string Body { get; }

    /// <summary>Full, culture-formatted timestamp — used in tooltips and the details pane.</summary>
    public string Date { get; }

    /// <summary>Short human-scale age ("3 days ago"), which is what the list column shows.</summary>
    public string RelativeDate { get; }

    /// <summary>Up to two initials for the author avatar, so the row needs no network fetch.</summary>
    public string AuthorInitials => GetInitials(Author);

    /// <summary>The commit-list rows show either an avatar or the pending-changes glyph, never both.</summary>
    public Visibility AvatarVisibility => IsWorkingDirectory ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PendingGlyphVisibility => IsWorkingDirectory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MergeGlyphVisibility => IsMerge ? Visibility.Visible : Visibility.Collapsed;

    // ---- What GitHub Actions made of this commit ----

    /// <summary>Null until the runs have been read, and for a commit no run was started for.</summary>
    public CommitChecks? Checks => _checks;

    /// <summary>
    ///  Records the runs for this commit. Raised on the row rather than replacing it, so the list keeps
    ///  its scroll position and selection when a later read lands.
    /// </summary>
    public void ApplyChecks(CommitChecks? checks)
    {
        if (ReferenceEquals(_checks, checks))
        {
            return;
        }

        _checks = checks;

        OnPropertyChanged(nameof(Checks));
        OnPropertyChanged(nameof(ChecksGlyph));
        OnPropertyChanged(nameof(ChecksBrush));
        OnPropertyChanged(nameof(ChecksVisibility));
        OnPropertyChanged(nameof(ChecksTooltip));
        OnPropertyChanged(nameof(ChecksRuns));
        OnPropertyChanged(nameof(ChecksSummary));
    }

    public string ChecksGlyph => GitHubStyle.OutcomeGlyph(_checks?.Outcome ?? RunOutcome.Neutral);

    public Brush ChecksBrush => GitHubStyle.OutcomeBrush(_checks?.Outcome ?? RunOutcome.Neutral);

    public Visibility ChecksVisibility => _checks is null ? Visibility.Collapsed : Visibility.Visible;

    public string ChecksTooltip => _checks?.Tooltip ?? "";

    public string ChecksSummary => _checks?.Summary ?? "";

    public IReadOnlyList<RunRow> ChecksRuns => _checks?.Runs ?? [];

    public static CommitRowViewModel CreateWorkingDirectory(int pendingCount) =>
        new($"{pendingCount} uncommitted change{(pendingCount == 1 ? "" : "s")}", pendingCount);

    /// <summary>Case-insensitive match against the fields shown in the list.</summary>
    public bool Matches(string filter) =>
        Subject.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Author.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || ShortHash.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string GetInitials(string author)
    {
        string[] parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return "?";
        }

        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
