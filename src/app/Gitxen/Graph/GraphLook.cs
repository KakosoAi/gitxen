// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;

namespace Gitxen.Graph;

/// <summary>How a change of lane is drawn.</summary>
public enum GraphLineStyle
{
    /// <summary>An S-curve that leaves and arrives vertically.</summary>
    Curved,

    /// <summary>A straight diagonal, the way <c>git log --graph</c> draws it.</summary>
    Angular,

    /// <summary>Straight runs joined by a rounded right angle, like a metro map.</summary>
    Rails
}

public enum GraphLineWeight
{
    Thin,
    Regular,
    Bold
}

public enum GraphLaneSpacing
{
    Compact,
    Regular,
    Wide
}

/// <summary>
///  The graph's appearance, as chosen in Settings, fixed for one layout so every row of it agrees.
/// </summary>
/// <remarks>
///  Taken once per layout rather than read as each row is drawn: rows are drawn lazily as the list
///  realises them, and a setting read at draw time would leave half a list in one style and the rest in
///  another until the next reload.
/// </remarks>
public sealed record GraphLook(GraphLineStyle Style, GraphLineWeight Weight, GraphLaneSpacing Spacing, bool SingleColour)
{
    /// <summary>What Settings says now.</summary>
    public static GraphLook Current => new(
        AppOptions.GraphStyle,
        AppOptions.GraphWeight,
        AppOptions.GraphSpacing,
        AppOptions.GraphSingleColour);

    public double Thickness => Weight switch
    {
        GraphLineWeight.Thin => 1.5,
        GraphLineWeight.Bold => 3,
        _ => 2
    };

    public double LaneWidth => Spacing switch
    {
        GraphLaneSpacing.Compact => 10,
        GraphLaneSpacing.Wide => 18,
        _ => 14
    };

    /// <summary>A merge commit's dot is smaller: it joins work rather than adding any.</summary>
    public double DotRadius(bool isMerge) => Weight switch
    {
        GraphLineWeight.Thin => isMerge ? 2.8 : 3.6,
        GraphLineWeight.Bold => isMerge ? 4.5 : 5.5,
        _ => isMerge ? 3.5 : 4.5
    };
}
