// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gitxen.ViewModels;

/// <summary>One headline number on a repository's Overview.</summary>
/// <param name="Section">The navigation tag of the section clicking it opens.</param>
public sealed record StatTile(string Glyph, string Label, string Value, string Detail, string Tip, string Section);

/// <summary>One entry in Home's project column: all repositories, a project, or those in none.</summary>
public sealed class ProjectRailItem
{
    public ProjectRailItem(string title, string glyph, Brush accent, int count, RepositoryGroup? target, bool isProject)
    {
        Title = title;
        Glyph = glyph;
        Accent = accent;
        Count = count;
        Target = target;
        IsProject = isProject;
    }

    public string Title { get; }

    public string Glyph { get; }

    /// <summary>The project's colour; a neutral one for the two entries that are not projects.</summary>
    public Brush Accent { get; }

    public int Count { get; }

    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The project, the "not in a project" bucket, or null for all repositories.</summary>
    public RepositoryGroup? Target { get; }

    /// <summary>A real project, which can be renamed or deleted; the other two entries cannot.</summary>
    public bool IsProject { get; }

    public Visibility ProjectMenuVisibility => IsProject ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => $"{Title}, {CountText}";
}

/// <summary>A technology a repository uses, drawn as a small coloured badge with its details in the tooltip.</summary>
public sealed class TechBadge
{
    public TechBadge(Services.Technology technology)
    {
        Technology = technology;
        (Background, Foreground) = GitHubStyle.Label(technology.Color);
    }

    public Services.Technology Technology { get; }

    public string Label => Technology.Label;

    public string Tooltip => Technology.Description;

    public Brush Background { get; }

    public Brush Foreground { get; }

    public override string ToString() => Technology.Name;
}

/// <summary>A heading in Home's repository list and the rows under it.</summary>
public sealed class HomeSection
{
    public HomeSection(string title, IEnumerable<RecentRepository> items)
    {
        Title = title;
        Items = [.. items];
    }

    public string Title { get; }

    public List<RecentRepository> Items { get; }

    public string CountText => Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public override string ToString() => $"{Title}, {CountText}";
}
