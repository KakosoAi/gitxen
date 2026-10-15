// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gitxen.Graph;

/// <summary>
///  One lane line of a commit-graph row, already positioned in the row's coordinate space.
/// </summary>
/// <remarks>
///  A plain struct rather than a <see cref="PathFigure"/>: the lane layout is computed for every
///  commit that loads, but only the handful of rows the list actually realises ever need drawing.
///  See <see cref="GraphRow"/>.
/// </remarks>
/// <param name="Color">Which lane colour it is drawn in: the branch's, not the column's.</param>
public readonly record struct GraphLine(int Color, double X1, double Y1, double X2, double Y2);

/// <summary>
///  The lane layout of one commit-graph row, and the XAML drawing built from it on demand.
/// </summary>
/// <remarks>
///  <para>
///   Splitting the row in two is what keeps loading a page cheap. The layout — which lanes pass
///   through, where the dot sits — has to be computed as each commit arrives, because a lane is only
///   meaningful relative to the commits already placed. The drawing does not: the commit list is
///   virtualised, so of the 2,000 rows a page loads only about thirty are ever realised.
///  </para>
///  <para>
///   Building the drawing eagerly meant roughly 37,000 <see cref="PathFigure"/>,
///   <see cref="LineSegment"/> and <see cref="Geometry"/> objects per page on this repository — all
///   of them WinRT objects, constructed on the UI thread, and about 98% of them never shown.
///  </para>
/// </remarks>
public sealed class GraphRow
{
    /// <summary>A row with nothing to draw: the working-directory row, which is not a commit.</summary>
    public static GraphRow Empty { get; } = new([], dotColor: -1, dotX: 0, rowHeight: 0, isMerge: false, GraphLook.Current, width: -1);

    private readonly GraphLine[] _lines;
    private readonly int _dotColor;
    private readonly double _dotX;
    private readonly double _rowHeight;
    private readonly bool _isMerge;
    private readonly GraphLook _look;
    private readonly double _width;

    /// <summary>Built on first request and kept, so scrolling back over a row does not rebuild it.</summary>
    private IReadOnlyList<GraphSegment>? _segments;

    internal GraphRow(GraphLine[] lines, int dotColor, double dotX, double rowHeight, bool isMerge, GraphLook look, double width)
    {
        _lines = lines;
        _dotColor = dotColor;
        _dotX = dotX;
        _rowHeight = rowHeight;
        _isMerge = isMerge;
        _look = look;
        _width = width;
    }

    /// <summary>
    ///  The graph column's width for this layout. The working-directory row has no layout of its own
    ///  and takes the current setting, so it lines up with the commits laid out alongside it.
    /// </summary>
    public double Width => _width < 0 ? CommitGraphBuilder.ColumnWidth : _width;

    /// <summary>
    ///  The drawable pieces of this row, built the first time the row is realised.
    /// </summary>
    public IReadOnlyList<GraphSegment> Segments => _segments ??= Build();

    private IReadOnlyList<GraphSegment> Build()
    {
        if (_dotColor < 0)
        {
            return [];
        }

        // Lines by colour, so a row emits one Path per colour rather than one per segment.
        Dictionary<int, PathFigureCollection> figuresByLane = [];

        foreach (GraphLine line in _lines)
        {
            if (!figuresByLane.TryGetValue(line.Color, out PathFigureCollection? figures))
            {
                figures = [];
                figuresByLane[line.Color] = figures;
            }

            figures.Add(Figure(line));
        }

        List<GraphSegment> segments = new(figuresByLane.Count + 1);

        foreach ((int lane, PathFigureCollection figures) in figuresByLane)
        {
            segments.Add(new GraphSegment(
                new PathGeometry { Figures = figures },
                CommitGraphBuilder.BrushFor(lane, _look),
                fill: null,
                _look.Thickness));
        }

        // The dot goes last so it paints over the lines meeting underneath it.
        double radius = _look.DotRadius(_isMerge);

        segments.Add(new GraphSegment(
            new EllipseGeometry { Center = new Point(_dotX, _rowHeight / 2), RadiusX = radius, RadiusY = radius },
            stroke: null,
            fill: CommitGraphBuilder.BrushFor(_dotColor, _look),
            thickness: 0));

        return segments;
    }

    /// <summary>One line, straight when it stays in its lane and in the chosen style when it changes lane.</summary>
    private PathFigure Figure(GraphLine line)
    {
        PathFigure figure = new() { StartPoint = new Point(line.X1, line.Y1) };
        Point end = new(line.X2, line.Y2);

        if (line.X1 == line.X2 || _look.Style == GraphLineStyle.Angular)
        {
            figure.Segments.Add(new LineSegment { Point = end });
            return figure;
        }

        if (_look.Style == GraphLineStyle.Curved)
        {
            // Leaves and arrives vertically, so it meets the lines above and below without a kink.
            double half = (line.Y1 + line.Y2) / 2;

            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(line.X1, half),
                Point2 = new Point(line.X2, half),
                Point3 = end
            });

            return figure;
        }

        // Rails: one vertical run and one horizontal run through the dot's row, joined by a rounded
        // corner. A line arriving from above comes down its own lane and then across into the dot; a
        // line leaving the dot goes across first and then down into its lane.
        double direction = Math.Sign(line.X2 - line.X1);
        double radius = Math.Min(5, Math.Min(Math.Abs(line.X2 - line.X1), Math.Abs(line.Y2 - line.Y1)));
        bool arriving = line.Y1 == 0;

        Point corner = arriving ? new Point(line.X1, line.Y2) : new Point(line.X2, line.Y1);
        Point before = arriving ? new Point(line.X1, line.Y2 - radius) : new Point(line.X2 - (direction * radius), line.Y1);
        Point after = arriving ? new Point(line.X1 + (direction * radius), line.Y2) : new Point(line.X2, line.Y1 + radius);

        figure.Segments.Add(new LineSegment { Point = before });
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = corner, Point2 = after });
        figure.Segments.Add(new LineSegment { Point = end });

        return figure;
    }
}
