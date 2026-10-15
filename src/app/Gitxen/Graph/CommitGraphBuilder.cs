// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Theming;
using Microsoft.UI.Xaml.Media;

namespace Gitxen.Graph;

/// <summary>
///  Builds the commit-graph lane layout, one row at a time, in the order commits arrive from
///  <c>git log</c>.
/// </summary>
/// <remarks>
///  <para>
///   Standard lane assignment: a lane is a column waiting for a specific commit. When that commit
///   arrives it takes the lane, the lane starts waiting for the commit's first parent, and any
///   further parents (a merge) either join an existing lane or open a new one.
///  </para>
///  <para>
///   Several lanes can be waiting for the same commit — two branches that grew from one base. When
///   it arrives, every one of them bends into its dot and is freed. Taking only the first and leaving
///   the others waiting is what used to leave a branch's line running on, straight down, to the end
///   of the loaded history.
///  </para>
///  <para>
///   A lane keeps one colour for as long as it follows one line of history, and takes a new one when
///   it is freed and reused. Colouring by column instead made a branch change colour whenever it
///   moved, and gave unrelated branches the same colour for sitting in the same place.
///  </para>
///  <para>
///   What comes out is a <see cref="GraphRow"/> of plain coordinates, not XAML. The lane layout must
///   be computed for every commit that loads — a lane only means anything relative to the commits
///   already placed — but the drawing is built later, for the few rows the list actually realises.
///  </para>
/// </remarks>
public sealed class CommitGraphBuilder
{
    /// <summary>
    ///  Height of one commit row, and therefore of the lane geometry drawn behind it.
    /// </summary>
    /// <remarks>
    ///  Read as each row is laid out rather than as a constant, so a density change takes effect as
    ///  the list refreshes without touching rows that are already placed.
    /// </remarks>
    public static double RowHeight => Services.AppOptions.Density == Services.UiDensity.Compact ? 24 : 44;

    /// <summary>Beyond this the graph stops being readable and just eats horizontal space.</summary>
    private const int MaxLanes = 8;

    /// <summary>The appearance chosen in Settings, fixed for this layout.</summary>
    private readonly GraphLook _look = GraphLook.Current;

    /// <summary>
    ///  Fixed rather than grown to fit: a width that changed as new lanes appeared would shift every
    ///  column to its right and leave already-rendered rows misaligned.
    /// </summary>
    public static double ColumnWidth => Width(GraphLook.Current);

    private static double Width(GraphLook look) => (MaxLanes * look.LaneWidth) + 6;

    /// <summary>
    ///  Lane colours come from the theme, which is also where repository group accents come from.
    /// </summary>
    /// <remarks>
    ///  These brush instances outlive a theme change -- only their colour is reassigned -- so a graph
    ///  that is already on screen recolours itself without being rebuilt.
    /// </remarks>
    private static IReadOnlyList<SolidColorBrush> LaneBrushes => ThemeBrushes.Lanes;

    /// <summary>Which commit each lane is currently waiting for; null means the lane is free.</summary>
    private readonly List<string?> _lanes = [];

    /// <summary>The colour each lane is drawn in, kept alongside <see cref="_lanes"/>.</summary>
    private readonly List<int> _colors = [];

    /// <summary>
    ///  Every commit already given a row. Without <c>--topo-order</c> git lists by date, and when
    ///  committer dates run backwards a parent can come before its child; a lane waiting for a commit
    ///  that has already been drawn would never close, and would run on to the bottom of the list.
    /// </summary>
    private readonly HashSet<string> _placed = new(StringComparer.Ordinal);

    /// <summary>Reused between rows: the lines are copied out into each row's own array.</summary>
    private readonly List<GraphLine> _lines = [];

    /// <summary>The colour the next newly opened lane gets.</summary>
    private int _nextColor;

    /// <summary>Whether the lane the latest <see cref="TakeLane"/> returned was already waiting for its commit.</summary>
    private bool _arrivedFromAbove;

    public GraphRow AddCommit(GitCommit revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        _lines.Clear();

        double rowHeight = RowHeight;
        double middle = rowHeight / 2;
        int myLane = TakeLane(revision.Id);
        bool continuesFromAbove = _arrivedFromAbove;
        double myX = X(myLane);

        // Everything waiting at the top of this row arrives from above: the commit's own lane stops at
        // its dot, any other lane waiting for this same commit bends into the dot, and the rest pass
        // straight through. A commit nothing was waiting for is the tip of a branch, and its dot
        // starts the lane rather than continuing it, so nothing is drawn above it.
        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            if (_lanes[lane] is not string waiting)
            {
                continue;
            }

            if (lane == myLane)
            {
                if (continuesFromAbove)
                {
                    _lines.Add(new GraphLine(_colors[lane], myX, 0, myX, middle));
                }
            }
            else if (string.Equals(waiting, revision.Id, StringComparison.Ordinal))
            {
                _lines.Add(new GraphLine(_colors[lane], X(lane), 0, myX, middle));
                _lanes[lane] = null;
            }
            else if (IsDrawn(lane))
            {
                _lines.Add(new GraphLine(_colors[lane], X(lane), 0, X(lane), rowHeight));
            }
        }

        _placed.Add(revision.Id);

        // A parent already drawn above cannot be reached by a line going down; the edge is dropped
        // rather than left waiting for ever. See _placed.
        List<string> parents = [.. revision.ParentIds.Where(parent => !_placed.Contains(parent))];
        bool keepsFirstParent = revision.ParentIds.Count > 0 && parents.Count > 0 && parents[0] == revision.ParentIds[0];
        int myColor = _colors[myLane];

        // The commit's lane carries on downwards waiting for its first parent.
        _lanes[myLane] = keepsFirstParent ? parents[0] : null;

        if (keepsFirstParent)
        {
            _lines.Add(new GraphLine(myColor, myX, middle, myX, rowHeight));
        }

        // A merge sends an extra edge out to each additional parent, in that parent's lane's colour.
        for (int i = keepsFirstParent ? 1 : 0; i < parents.Count; i++)
        {
            // Straight down when the parent reuses this commit's own lane — freed above because its
            // first parent had already been drawn — and a curve out to any other lane.
            int parentLane = TakeLane(parents[i]);
            _lines.Add(new GraphLine(_colors[parentLane], myX, middle, X(parentLane), rowHeight));
        }

        TrimTrailingFreeLanes();

        return new GraphRow([.. _lines], myColor, myX, rowHeight, revision.IsMerge, _look, Width(_look));
    }

    /// <summary>
    ///  The leftmost lane already waiting for <paramref name="commitId"/>, or a newly opened one.
    /// </summary>
    private int TakeLane(string commitId)
    {
        int existing = _lanes.FindIndex(waiting => string.Equals(waiting, commitId, StringComparison.Ordinal));

        if (existing >= 0)
        {
            _arrivedFromAbove = true;
            return existing;
        }

        _arrivedFromAbove = false;

        int free = _lanes.IndexOf(null);

        if (free >= 0)
        {
            _lanes[free] = commitId;
            _colors[free] = _nextColor++;
            return free;
        }

        _lanes.Add(commitId);
        _colors.Add(_nextColor++);
        return _lanes.Count - 1;
    }

    private void TrimTrailingFreeLanes()
    {
        while (_lanes.Count > 0 && _lanes[^1] is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
            _colors.RemoveAt(_colors.Count - 1);
        }
    }

    /// <summary>
    ///  Lanes past the overflow column are all drawn at its x, so drawing them would only stack
    ///  identical lines in different colours.
    /// </summary>
    private static bool IsDrawn(int lane) => lane <= MaxLanes;

    private double X(int lane) => (Math.Min(lane, MaxLanes) * _look.LaneWidth) + (_look.LaneWidth / 2);

    internal static Brush BrushFor(int color, GraphLook look) =>
        LaneBrushes[look.SingleColour ? 0 : color % LaneBrushes.Count];
}
