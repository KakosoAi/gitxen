// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gitxen;

/// <summary>How a file changed, which drives its marker letter and colour.</summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>
///  One row in a changed-files list, whether that list came from the working tree or from a commit.
/// </summary>
/// <remarks>
///  Takes Gitxen's own <see cref="GitChange"/>. The working-tree listing produces those directly;
///  the commit listings still come from the inherited engine and are converted at the loader, so
///  nothing above this line knows which layer read them.
/// </remarks>
public sealed class ChangedFileViewModel
{
    public ChangedFileViewModel(GitChange change, bool isWorkingDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(change);

        Name = change.Path;
        OldName = change.OldPath;
        Kind = GetKind(change);
        IsNew = change.IsNew;
        IsStaged = change.IsStaged;
        IsWorkingDirectory = isWorkingDirectory;

        // Split for the two-line row: the leaf is what identifies the file, the folder is context.
        int separator = Name.LastIndexOfAny(['/', '\\']);
        FileName = separator >= 0 ? Name[(separator + 1)..] : Name;
        Directory = separator >= 0 ? Name[..separator] : "";

        Detail = change.IsRenamed && !string.IsNullOrEmpty(change.OldPath)
            ? $"renamed from {change.OldPath}"
            : Directory;
    }

    public string Name { get; }

    /// <summary>Leaf file name, shown as the row's primary text.</summary>
    public string FileName { get; }

    /// <summary>Containing folder, shown beneath the file name.</summary>
    public string Directory { get; }

    /// <summary>Previous path for a rename; needed so git can diff the right pair.</summary>
    public string? OldName { get; }

    public FileChangeKind Kind { get; }

    /// <summary>Untracked, which changes how the file is discarded — removed rather than reverted.</summary>
    public bool IsNew { get; }

    /// <summary>Single-character status marker, matching git's own porcelain vocabulary.</summary>
    public string Change => Kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        _ => "M"
    };

    public Brush ChangeBrush => Kind switch
    {
        FileChangeKind.Added => ThemeBrushes.FileAdded,
        FileChangeKind.Deleted => ThemeBrushes.FileDeleted,
        FileChangeKind.Renamed => ThemeBrushes.FileRenamed,
        _ => ThemeBrushes.FileModified
    };

    public string Detail { get; }

    /// <summary>Collapses the second row line when there is nothing to say in it.</summary>
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsStaged { get; }

    public bool IsWorkingDirectory { get; }

    /// <summary>Stage/unstage buttons only make sense for uncommitted changes.</summary>
    public Visibility StagingVisibility => IsWorkingDirectory ? Visibility.Visible : Visibility.Collapsed;

    private static FileChangeKind GetKind(GitChange change)
    {
        if (change.IsNew)
        {
            return FileChangeKind.Added;
        }

        if (change.IsDeleted)
        {
            return FileChangeKind.Deleted;
        }

        return change.IsRenamed ? FileChangeKind.Renamed : FileChangeKind.Modified;
    }
}
