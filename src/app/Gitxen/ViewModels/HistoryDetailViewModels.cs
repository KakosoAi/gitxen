// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gitxen.ViewModels;

/// <summary>One line of a blamed file.</summary>
public sealed class BlameRow
{
    private static readonly Brush _plain = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush _shaded = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128));
    private static readonly Brush _uncommitted = new SolidColorBrush(Color.FromArgb(52, 255, 185, 0));

    public BlameRow(GitBlameLine line, bool isBlockStart, bool shaded)
    {
        Line = line;
        IsBlockStart = isBlockStart;
        Background = line.IsUncommitted ? _uncommitted : shaded ? _shaded : _plain;
    }

    public GitBlameLine Line { get; }

    /// <summary>The first line of a run written by one commit, which is where the commit is named.</summary>
    public bool IsBlockStart { get; }

    public Brush Background { get; }

    public string LineNumber => Line.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Named once per run rather than on every line, so the eye can follow the runs.</summary>
    public string Hash => IsBlockStart && !Line.IsUncommitted ? Line.ShortId : "";

    public string Author => !IsBlockStart ? "" : Line.IsUncommitted ? "Not committed yet" : Line.Author;

    public string Age => IsBlockStart && !Line.IsUncommitted ? TimeText.Relative(Line.AuthorDate) : "";

    /// <summary>Tabs become spaces: a text block draws a tab as one narrow gap, which ruins the indentation.</summary>
    public string Text => Line.Text.Replace("\t", "    ", StringComparison.Ordinal);

    public bool CanShowCommit => !Line.IsUncommitted;

    public string Tooltip => Line.IsUncommitted
        ? "This line has not been committed yet."
        : $"{Line.Summary}{Environment.NewLine}{Line.Author} · {TimeText.Full(Line.AuthorDate)}{Environment.NewLine}{Line.ShortId}"
            + (Line.IsBoundary ? $"{Environment.NewLine}The oldest commit that could be found for this line." : "");

    /// <summary>Read out by a screen reader in place of the type name.</summary>
    public override string ToString() => $"Line {LineNumber}: {Line.Text}";
}

/// <summary>
///  Blame for the file selected in the History page: who last changed each line, as of the selected
///  commit.
/// </summary>
/// <remarks>
///  Read on demand, when the pane is showing, and again when the selected file or commit changes
///  while it is. Diff is the pane people arrive at, so a repository that is only ever browsed never
///  pays for a blame.
/// </remarks>
public sealed class BlameViewModel : ObservableObject
{
    private const string NothingSelected = "Select a file in the list to see who last changed each line.";

    private readonly RepositoryTabViewModel _tab;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<BlameRow> _rows = [];
    private BlameRow? _selected;
    private string _title = "";
    private string _subtitle = "";
    private string _message = NothingSelected;
    private bool _isLoading;

    public BlameViewModel(RepositoryTabViewModel tab)
    {
        _tab = tab;
    }

    public IReadOnlyList<BlameRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(RowsVisibility));
            }
        }
    }

    public BlameRow? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    /// <summary>Why there is nothing to show, when there is nothing.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(MessageVisibility));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public Visibility RowsVisibility => Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MessageVisibility => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Cancel() => _cts?.Cancel();

    public async Task LoadAsync(ChangedFileViewModel? file, CommitRowViewModel? commit)
    {
        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _cts, cts);

        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        if (file is null || commit is null)
        {
            Show([], "", "", NothingSelected);
            return;
        }

        bool working = commit.IsWorkingDirectory;
        string? revision = working ? null : commit.Revision?.Id;
        string subtitle = working ? "Working tree, including uncommitted changes" : $"As of {commit.ShortHash}";

        if (file.Kind == FileChangeKind.Deleted && !working)
        {
            Show([], file.Name, subtitle, "This commit deletes the file, so there is nothing left to blame at it.");
            return;
        }

        if (file.IsNew && working)
        {
            Show([], file.Name, subtitle, "This file has not been committed yet, so there is no history to blame.");
            return;
        }

        Title = file.Name;
        Subtitle = subtitle;
        IsLoading = true;

        try
        {
            GitBlameResult result = await Task.Run(() => _tab.ReadBlame(file.Name, revision, cts.Token), cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                Show([], file.Name, subtitle, result.Error);
                return;
            }

            List<BlameRow> rows = new(result.Lines.Count);
            bool shaded = false;
            string? previousId = null;

            foreach (GitBlameLine line in result.Lines)
            {
                bool start = previousId != line.CommitId;

                if (start && previousId is not null)
                {
                    shaded = !shaded;
                }

                rows.Add(new BlameRow(line, start, shaded));
                previousId = line.CommitId;
            }

            Show(rows, file.Name, subtitle, rows.Count == 0 ? "The file is empty." : "");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Every commit that changed the row's line. Empty for a line not committed yet.</summary>
    internal Task<IReadOnlyList<GitLineChange>> ReadLineHistoryAsync(BlameRow row) => _tab.ReadLineHistoryAsync(row.Line);

    /// <summary>Selects the commit in the history list, where its full details and diff are.</summary>
    public void ShowCommit(string commitId) => _tab.RevealCommit(commitId, Title);

    /// <summary>Selects the row's commit in the history list, where its full details and diff are.</summary>
    public void ShowCommit(BlameRow? row)
    {
        if (row is { CanShowCommit: true })
        {
            _tab.RevealCommit(row.Line.CommitId, Title);
        }
    }

    private void Show(IReadOnlyList<BlameRow> rows, string title, string subtitle, string message)
    {
        Title = title;
        Subtitle = subtitle;
        Message = message;
        Rows = rows;
        IsLoading = false;
    }
}

/// <summary>One commit in a file's history.</summary>
public sealed class FileHistoryRow
{
    public FileHistoryRow(GitCommit commit)
    {
        Commit = commit;
    }

    public GitCommit Commit { get; }

    public string ShortHash => Commit.ShortId;

    public string Author => Commit.Author;

    public string Subject => Commit.Subject;

    public string Age => TimeText.Relative(Commit.AuthorDate);

    public string Date => TimeText.Full(new DateTimeOffset(Commit.AuthorDate));

    public override string ToString() => $"{ShortHash}, {Subject}, {Author}, {Age}";
}

/// <summary>
///  The commits that changed the file selected in the History page, newest first from HEAD, following
///  the file across renames.
/// </summary>
/// <remarks>
///  Read from HEAD rather than from the selected commit, and kept while the same file stays selected:
///  it sits beside the diff while commits are clicked through, and a list that cut itself short at
///  each click would lose the very rows being browsed. The selected commit is highlighted in it
///  instead, when it touched the file.
/// </remarks>
public sealed class FileHistoryViewModel : ObservableObject
{
    private const int MaxCommits = 200;
    private const string NothingSelected = "Select a file in the list to see the commits that changed it.";

    private readonly RepositoryTabViewModel _tab;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<FileHistoryRow> _rows = [];
    private FileHistoryRow? _selected;
    private string _title = "";
    private string _subtitle = "";
    private string _message = NothingSelected;
    private bool _isLoading;

    /// <summary>The file the rows are for, so selecting another commit that touched it does not read it again.</summary>
    private string? _loadedPath;

    public FileHistoryViewModel(RepositoryTabViewModel tab)
    {
        _tab = tab;
    }

    public IReadOnlyList<FileHistoryRow> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(RowsVisibility));
            }
        }
    }

    public FileHistoryRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(MessageVisibility));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public Visibility RowsVisibility => Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MessageVisibility => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Cancel() => _cts?.Cancel();

    /// <summary>The history moved — a commit, a checkout, a pull — so the next showing reads again.</summary>
    public void Invalidate() => _loadedPath = null;

    public async Task LoadAsync(ChangedFileViewModel? file, CommitRowViewModel? commit)
    {
        if (file is not null && file.Name == _loadedPath)
        {
            Highlight(commit);
            return;
        }

        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _cts, cts);

        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        _loadedPath = null;

        if (file is null)
        {
            Show([], "", "", NothingSelected);
            return;
        }

        if (file.IsNew && commit?.IsWorkingDirectory == true)
        {
            Show([], file.Name, "", "This file has not been committed yet, so it has no history.");
            return;
        }

        Title = file.Name;
        IsLoading = true;

        try
        {
            IReadOnlyList<GitCommit> commits = await Task.Run(() => _tab.ReadFileHistory(file.Name, null, MaxCommits, cts.Token), cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            string count = commits.Count >= MaxCommits
                ? $"The latest {MaxCommits} commits"
                : commits.Count == 1 ? "1 commit" : $"{commits.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)} commits";

            Show(
                [.. commits.Select(item => new FileHistoryRow(item))],
                file.Name,
                commits.Count == 0 ? "" : $"{count}, following renames",
                commits.Count == 0 ? "No commits changed this file." : "");

            _loadedPath = file.Name;
            Highlight(commit);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (InvalidOperationException ex)
        {
            Show([], file.Name, "", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Marks the selected commit in the list, when it is one that changed the file.</summary>
    private void Highlight(CommitRowViewModel? commit) =>
        Selected = commit?.Revision is GitCommit revision
            ? Rows.FirstOrDefault(row => row.Commit.Id == revision.Id)
            : null;

    /// <summary>Selects the commit in the history list, where its full details and diff are.</summary>
    public void ShowSelected()
    {
        if (Selected is FileHistoryRow row)
        {
            _tab.RevealCommit(row.Commit.Id, Title);
        }
    }

    private void Show(IReadOnlyList<FileHistoryRow> rows, string title, string subtitle, string message)
    {
        Title = title;
        Subtitle = subtitle;
        Message = message;
        Rows = rows;
        IsLoading = false;
    }
}
