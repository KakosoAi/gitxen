// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media;

namespace Gitxen.Graph;

/// <summary>
///  One drawable piece of a commit-graph row: either the lane lines of a single colour, or the
///  commit dot. Pre-built so the row template is a plain binding with no drawing code behind it.
/// </summary>
public sealed class GraphSegment
{
    public GraphSegment(Geometry geometry, Brush? stroke, Brush? fill, double thickness)
    {
        Geometry = geometry;
        Stroke = stroke;
        Fill = fill;
        Thickness = thickness;
    }

    public Geometry Geometry { get; }

    /// <summary>Set for lane lines, null for the dot.</summary>
    public Brush? Stroke { get; }

    /// <summary>Set for the dot, null for lane lines.</summary>
    public Brush? Fill { get; }

    /// <summary>The line weight chosen in Settings; nothing for the dot, which is filled.</summary>
    public double Thickness { get; }
}
