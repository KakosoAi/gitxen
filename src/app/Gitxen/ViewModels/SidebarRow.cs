// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Gitxen.ViewModels;

/// <summary>
///  One repository row in the repository column or the tab strip, open or not.
/// </summary>
/// <remarks>
///  The column and the strip list the same repositories Home does — every project member, not only
///  the open ones — so the three views always agree. A row for a repository that is not open opens it
///  when clicked; a row for an open one switches to its tab, carries its close button, and shows its
///  title bold. The strip also carries a Home row, since Home is a tab there rather than a button.
/// </remarks>
public sealed class SidebarRow
{
    public SidebarRow(RecentRepository entry, RepositoryTabViewModel? openTab, RepositoryGroup? group)
    {
        Entry = entry;
        OpenTab = openTab;
        Group = group;
    }

    private SidebarRow()
    {
    }

    private SidebarRow(RepositoryGroup group, int openCount, bool collapsed)
    {
        Group = group;
        IsGroupLabel = true;
        OpenCount = openCount;
        IsCollapsed = collapsed;
    }

    /// <summary>The strip's Home entry; the column has a Home button instead.</summary>
    public static SidebarRow ForHome() => new();

    /// <summary>
    ///  A project's label in the tab strip, in front of its tabs: click it to fold them into it, and
    ///  again to bring them back, the way a browser's tab groups work.
    /// </summary>
    public static SidebarRow ForGroupLabel(RepositoryGroup group, int openCount, bool collapsed) => new(group, openCount, collapsed);

    /// <summary>A project's label in the strip rather than a tab.</summary>
    public bool IsGroupLabel { get; }

    /// <summary>How many of the project's repositories are open; shown on the label while it is folded.</summary>
    public int OpenCount { get; }

    public bool IsCollapsed { get; }

    public Visibility LabelVisibility => IsGroupLabel ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TabPartsVisibility => IsGroupLabel ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CollapsedCountVisibility => IsGroupLabel && IsCollapsed ? Visibility.Visible : Visibility.Collapsed;

    public string OpenCountText => OpenCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The label's chevron: pointing down while its tabs show, right while they are folded away.</summary>
    public double LabelChevronRotation => IsCollapsed ? -90 : 0;

    public RecentRepository? Entry { get; }

    /// <summary>The open tab this row stands for, null while the repository is not open.</summary>
    public RepositoryTabViewModel? OpenTab { get; }

    /// <summary>The project the row is sectioned under, for the colour stripe.</summary>
    public RepositoryGroup? Group { get; }

    public bool IsHome => Entry is null && OpenTab is null && Group is null && !IsGroupLabel;

    public string Title => IsGroupLabel ? Group?.Name ?? "" : Entry?.Name ?? "Home";

    public string Path => Entry?.Path ?? "";

    public string GroupName => Group?.Name ?? "";

    public string Glyph => IsGroupLabel ? Group?.Glyph ?? "\uE8B7" : IsHome ? "\uE80F" : "\uE8B7";

    public string ToolTipText => IsGroupLabel
        ? $"{Group?.Name}: {OpenCount} open. Click to {(IsCollapsed ? "show" : "fold away")} its tabs."
        : IsHome ? "Open a repository, or pick up where you left off" : Path;

    public bool IsOpen => OpenTab is not null;

    /// <summary>Bold marks the repositories that are actually open among their project's members.</summary>
    public FontWeight TitleWeight => IsOpen ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>
    ///  Every repository row can be removed from the lists — closing its tab first when it is open.
    ///  Home is the one row that cannot leave.
    /// </summary>
    public Visibility RemoveVisibility => IsHome ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The close cross belongs only to rows that stand for an open tab.</summary>
    public Visibility CloseVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    public Brush Accent => Group?.Accent ?? GroupPalette.Accent("Slate");

    /// <summary>The colour cue is only meaningful when the repository is actually in a project.</summary>
    public Visibility GroupVisibility => Group is null || IsGroupLabel ? Visibility.Collapsed : Visibility.Visible;
}
