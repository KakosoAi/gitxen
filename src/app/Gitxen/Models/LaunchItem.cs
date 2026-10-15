// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Models;

/// <summary>One thing the quick launcher offers: a repository, or a commit found in one.</summary>
/// <remarks>A class rather than a record, for the same XAML type-info reason as <see cref="GoToItem"/>.</remarks>
public sealed class LaunchItem
{
    public LaunchItem(string name, string path, string detail, bool isOpen, string commitId = "")
    {
        Name = name;
        Path = path;
        Detail = detail;
        IsOpen = isOpen;
        CommitId = commitId;
    }

    /// <summary>The repository's name, or a commit's subject.</summary>
    public string Name { get; }

    /// <summary>The repository folder (also for a commit: the repository it is in).</summary>
    public string Path { get; }

    /// <summary>Its project and branch, or that it is already open; for a commit, its short id and age.</summary>
    public string Detail { get; }

    public bool IsOpen { get; }

    /// <summary>The full id when this is a commit found by the search index; empty for a repository.</summary>
    public string CommitId { get; }

    public bool IsCommit => CommitId.Length > 0;

    /// <summary>A folder, or a commit's icon.</summary>
    public string Glyph => IsCommit ? "" : "";

    /// <summary>What a screen reader announces for the suggestion.</summary>
    public override string ToString() => IsCommit
        ? $"Commit {Name}, {Detail}, in {System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'))}"
        : Detail.Length > 0 ? $"{Name}, {Detail}" : Name;
}
