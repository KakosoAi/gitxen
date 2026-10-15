// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Gitxen.Views;

/// <summary>
///  A repository's start page: headline numbers, commits over time, languages, the last year day by
///  day, who commits, when, and what the files are.
/// </summary>
/// <remarks>
///  <para>
///   Everything is read at <c>HEAD</c> (<see cref="RepositoryLoader.ReadOverview"/>) and kept on the
///   tab until <c>HEAD</c> moves. The charts are drawn with shapes rather than a charting library: they
///   are few and simple, and a library would be the largest thing in the application.
///  </para>
///  <para>
///   Colour: one hue (blue) for every single-series chart, stepped light to dark for the heatmap's
///   magnitude, with dark mode given its own steps rather than a flip. Languages keep the colours GitHub
///   gives them — a colour follows the language — and every one is also named in the legend, so no
///   value is told by colour alone. Values and labels are always in text colours.
///  </para>
/// </remarks>
public sealed partial class OverviewView : RepositoryPage
{
    private const double AxisWidth = 34;
    private const double AxisHeight = 20;
    private const double CellSize = 12;
    private const double CellGap = 3;

    private RepositoryOverview? _overview;
    private List<Point> _timelinePoints = [];
    private int _generation;

    public OverviewView()
    {
        InitializeComponent();
        TimelineHost.SizeChanged += (_, _) => DrawTimeline();
        WeekdayHost.SizeChanged += (_, _) => DrawWeekdays();
        ActualThemeChanged += (_, _) => Draw();
    }

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>The single series hue: blue, a step lighter on the dark surface.</summary>
    private Color Series => IsDark ? Hex("#3987e5") : Hex("#2a78d6");

    public override async Task ActivateAsync()
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        int generation = ++_generation;
        LoadingBar.Visibility = _overview is null ? Visibility.Visible : Visibility.Collapsed;

        RepositoryOverview overview;

        try
        {
            overview = await tab.GetOverviewAsync();
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        if (generation == _generation)
        {
            _overview = overview;
            Draw();
        }
    }

    protected override void OnTabChanged()
    {
        _overview = null;
        Body.Visibility = Visibility.Collapsed;

        if (Visibility == Visibility.Visible && Tab is not null)
        {
            _ = ActivateAsync();
        }
    }

    private static Color Hex(string hex) => Color.FromArgb(
        0xff,
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Compact(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 10_000 => (value / 1_000.0).ToString("0", CultureInfo.CurrentCulture) + "k",
        _ => Count(value)
    };

    private Brush TextBrush(string key) => (Brush)Application.Current.Resources[key];

    private void Draw()
    {
        if (_overview is not RepositoryOverview overview || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        bool empty = overview.Commits == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Subtitle.Text = empty
            ? tab.WorkingDir
            : $"{tab.Branch} at {overview.HeadId[..Math.Min(8, overview.HeadId.Length)]} · {tab.WorkingDir}";

        if (empty)
        {
            return;
        }

        DrawTiles(overview);
        DrawTimeline();
        DrawLanguages(overview);
        DrawHeatmap(overview);
        DrawContributors(overview);
        DrawWeekdays();
        DrawFiles(overview);
    }

    // ---- Tiles ----------------------------------------------------------------------------------

    private void DrawTiles(RepositoryOverview o)
    {
        string age = o.FirstCommit is DateTimeOffset first ? Age(first) : "";
        string since = o.FirstCommit is DateTimeOffset start ? $"since {start:d MMM yyyy}" : "";
        string last = o.LastCommit is DateTimeOffset newest ? $"last {TimeText.Relative(newest)}" : "";

        static string G(int code) => char.ConvertFromUtf32(code);

        Tiles.ItemsSource = new List<StatTile>
        {
            new(G(0xE81C), "Commits", Count(o.Commits), last, $"{Count(o.Commits)} commits reachable from HEAD. Opens History.", "history"),
            new(G(0xE716), "Contributors", Count(o.Contributors.Count), o.Contributors.Count > 0 ? $"most: {o.Contributors[0].Name}" : "", "Distinct authors, after the mailmap. Opens History.", "history"),
            new(G(0xE8AB), "Branches", Count(o.Branches), "local", "Local branches. Opens Branches.", "branches"),
            new(G(0xE8EC), "Tags", Count(o.Tags), o.Tags > 0 ? "releases and marks" : "none yet", "Tags. Opens Tags.", "tags"),
            new(G(0xE8A5), "Files", Count(o.Files), o.Submodules > 0 ? $"{o.Submodules} submodules" : FileEntryRow.FormatSize(o.Bytes), "Files in the tree at HEAD. Opens Files.", "files"),
            new(G(0xE943), "Lines", Compact(o.Lines), "in text files", $"{Count(o.Lines)} lines in text files at HEAD (binary files are not counted). Opens Files.", "files"),
            new(G(0xE787), "Age", age, since, "Time since the first commit. Opens History.", "history"),
            new(G(0xE9D2), "This year", Count(o.CommitsLastYear), "commits in 12 months", "Commits in the last 53 weeks. Opens History.", "history"),
        };
    }

    /// <summary>"3 years", "5 months", "12 days": the largest whole unit.</summary>
    private static string Age(DateTimeOffset first)
    {
        DateTime start = first.LocalDateTime.Date;
        DateTime today = DateTime.Today;
        int months = ((today.Year - start.Year) * 12) + today.Month - start.Month - (today.Day < start.Day ? 1 : 0);

        return months >= 24 ? $"{months / 12} years"
            : months >= 12 ? "1 year"
            : months >= 2 ? $"{months} months"
            : (today - start).Days is int days && days >= 2 ? $"{days} days"
            : "new";
    }

    // ---- Commits over time: an area chart, one point a month ------------------------------------

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        TimelineHover.Children.Clear();
        _timelinePoints = [];

        if (_overview is not RepositoryOverview o || o.Monthly.Count == 0 || TimelineHost.ActualWidth < 100)
        {
            return;
        }

        IReadOnlyList<MonthCount> months = o.Monthly;
        double width = TimelineHost.ActualWidth - AxisWidth;
        double height = TimelineHost.ActualHeight - AxisHeight;
        int peak = Math.Max(1, months.Max(month => month.Commits));
        int step = NiceStep(peak);
        int top = (int)Math.Ceiling(peak / (double)step) * step;

        TimelineNote.Text = o.StampsRead < o.Commits
            ? $"the latest {Count(o.StampsRead)} commits, by month"
            : $"{Count(o.Commits)} commits, by month";

        // Recessive horizontal grid with its values on the left.
        for (int value = 0; value <= top; value += step)
        {
            double y = height - (value / (double)top * height);
            TimelineCanvas.Children.Add(new Line
            {
                X1 = AxisWidth, X2 = AxisWidth + width, Y1 = y, Y2 = y,
                Stroke = TextBrush("DividerStrokeColorDefaultBrush"), StrokeThickness = 1
            });
            AddText(TimelineCanvas, Count(value), 0, y - 8, AxisWidth - 6, TextAlignment.Right);
        }

        double spacing = months.Count > 1 ? width / (months.Count - 1) : 0;

        for (int i = 0; i < months.Count; i++)
        {
            double x = AxisWidth + (months.Count > 1 ? i * spacing : width / 2);
            double y = height - (months[i].Commits / (double)top * height);
            _timelinePoints.Add(new Point(x, y));
        }

        // Area under the line, fading to nothing at the baseline.
        PathFigure area = new() { StartPoint = new Point(_timelinePoints[0].X, height), IsClosed = true };
        PathFigure line = new() { StartPoint = _timelinePoints[0] };

        foreach (Point point in _timelinePoints)
        {
            area.Segments.Add(new LineSegment { Point = point });
            line.Segments.Add(new LineSegment { Point = point });
        }

        area.Segments.Add(new LineSegment { Point = new Point(_timelinePoints[^1].X, height) });

        LinearGradientBrush fill = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop { Color = WithAlpha(Series, 0x66), Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = WithAlpha(Series, 0x00), Offset = 1 });

        TimelineCanvas.Children.Add(new Path { Data = new PathGeometry { Figures = { area } }, Fill = fill });
        TimelineCanvas.Children.Add(new Path
        {
            Data = new PathGeometry { Figures = { line } },
            Stroke = new SolidColorBrush(Series),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        // Month labels: about one per 90 pixels, on a January where there is room for years.
        int every = Math.Max(1, (int)Math.Ceiling(months.Count / Math.Max(1, width / 90)));
        bool years = months.Count > 24;

        for (int i = 0; i < months.Count; i += every)
        {
            DateOnly month = months[i].Month;
            string label = years ? month.ToString("MMM yyyy", CultureInfo.CurrentCulture) : month.ToString("MMM yy", CultureInfo.CurrentCulture);
            AddText(TimelineCanvas, label, _timelinePoints[i].X - 40, height + 4, 80, TextAlignment.Center);
        }
    }

    /// <summary>A round step for the value axis: 1, 2 or 5 times a power of ten, about four lines.</summary>
    private static int NiceStep(int peak)
    {
        double raw = peak / 4.0;
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, raw))));
        double scaled = raw / power;
        double nice = scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10;
        return Math.Max(1, (int)(nice * power));
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_overview is not RepositoryOverview o || _timelinePoints.Count == 0)
        {
            return;
        }

        double x = e.GetCurrentPoint(TimelineHost).Position.X;
        int index = 0;

        for (int i = 1; i < _timelinePoints.Count; i++)
        {
            if (Math.Abs(_timelinePoints[i].X - x) < Math.Abs(_timelinePoints[index].X - x))
            {
                index = i;
            }
        }

        Point point = _timelinePoints[index];
        double height = TimelineHost.ActualHeight - AxisHeight;

        TimelineHover.Children.Clear();
        TimelineHover.Children.Add(new Line
        {
            X1 = point.X, X2 = point.X, Y1 = 0, Y2 = height,
            Stroke = TextBrush("TextFillColorTertiaryBrush"), StrokeThickness = 1, StrokeDashArray = { 3, 3 }
        });

        Ellipse dot = new()
        {
            Width = 10, Height = 10,
            Fill = new SolidColorBrush(Series),
            Stroke = TextBrush("LayerFillColorDefaultBrush"), StrokeThickness = 2
        };
        Canvas.SetLeft(dot, point.X - 5);
        Canvas.SetTop(dot, point.Y - 5);
        TimelineHover.Children.Add(dot);

        MonthCount month = o.Monthly[index];
        TimelineTipValue.Text = month.Commits == 1 ? "1 commit" : $"{Count(month.Commits)} commits";
        TimelineTipLabel.Text = month.Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        TimelineTip.Visibility = Visibility.Visible;
        TimelineTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double left = point.X + 12;

        if (left + TimelineTip.DesiredSize.Width > TimelineHost.ActualWidth)
        {
            left = point.X - 12 - TimelineTip.DesiredSize.Width;
        }

        TimelineTip.Margin = new Thickness(left, Math.Max(0, Math.Min(point.Y - 24, height - TimelineTip.DesiredSize.Height)), 0, 0);
    }

    private void Timeline_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TimelineHover.Children.Clear();
        TimelineTip.Visibility = Visibility.Collapsed;
    }

    // ---- Languages: a donut with a legend that names every slice --------------------------------

    private void DrawLanguages(RepositoryOverview o)
    {
        DonutCanvas.Children.Clear();
        LanguageLegend.Children.Clear();
        NoLanguages.Visibility = o.Languages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DonutValue.Text = "";
        DonutLabel.Text = "";

        if (o.Languages.Count == 0)
        {
            return;
        }

        const double Size = 176;
        const double Outer = 86;
        const double Inner = 60;
        Point centre = new(Size / 2, Size / 2);
        double angle = -90;

        foreach (LanguageStat language in o.Languages)
        {
            double sweep = language.Share * 360;
            Path slice = new()
            {
                Data = Slice(centre, Outer, Inner, angle, Math.Min(sweep, 359.99)),
                Fill = new SolidColorBrush(Hex(language.Language.Color)),

                // The surface between slices: a 2px gap, so neighbours never merge.
                Stroke = TextBrush("LayerFillColorDefaultBrush"),
                StrokeThickness = o.Languages.Count > 1 ? 2 : 0
            };

            ToolTipService.SetToolTip(slice, LanguageTip(language));
            DonutCanvas.Children.Add(slice);
            angle += sweep;

            LanguageLegend.Children.Add(LegendRow(language));
        }

        LanguageStat top = o.Languages[0];
        DonutValue.Text = $"{top.Share * 100:0}%";
        DonutLabel.Text = top.Language.Name;
    }

    private static string LanguageTip(LanguageStat language) =>
        $"{language.Language.Name}: {language.Share * 100:0.0}% · {FileEntryRow.FormatSize(language.Bytes)} · " +
        $"{Count(language.Files)} files · {Count(language.Lines)} lines";

    private Grid LegendRow(LanguageStat language)
    {
        Grid row = new() { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ToolTipService.SetToolTip(row, LanguageTip(language));

        Ellipse swatch = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Hex(language.Language.Color)), VerticalAlignment = VerticalAlignment.Center };
        TextBlock name = new() { Text = language.Language.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock share = new()
        {
            Text = $"{language.Share * 100:0.0}%",
            Foreground = TextBrush("TextFillColorSecondaryBrush")
        };

        Grid.SetColumn(name, 1);
        Grid.SetColumn(share, 2);
        row.Children.Add(swatch);
        row.Children.Add(name);
        row.Children.Add(share);
        return row;
    }

    /// <summary>One ring segment from <paramref name="start"/> degrees, <paramref name="sweep"/> long.</summary>
    private static PathGeometry Slice(Point centre, double outer, double inner, double start, double sweep)
    {
        static Point At(Point c, double r, double degrees)
        {
            double radians = degrees * Math.PI / 180;
            return new Point(c.X + (r * Math.Cos(radians)), c.Y + (r * Math.Sin(radians)));
        }

        bool large = sweep > 180;
        PathFigure figure = new() { StartPoint = At(centre, outer, start), IsClosed = true };
        figure.Segments.Add(new ArcSegment { Point = At(centre, outer, start + sweep), Size = new Size(outer, outer), IsLargeArc = large, SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = At(centre, inner, start + sweep) });
        figure.Segments.Add(new ArcSegment { Point = At(centre, inner, start), Size = new Size(inner, inner), IsLargeArc = large, SweepDirection = SweepDirection.Counterclockwise });
        return new PathGeometry { Figures = { figure } };
    }

    // ---- The last year, day by day --------------------------------------------------------------

    /// <summary>
    ///  Nothing, then four steps of blue from light to dark. On the dark surface the order turns
    ///  round so "more" is always the step that stands out most, and "none" recedes into the card.
    /// </summary>
    private Color[] HeatSteps => IsDark
        ? [Hex("#2c2c2a"), Hex("#104281"), Hex("#1c5cab"), Hex("#3987e5"), Hex("#86b6ef")]
        : [Hex("#ecebe7"), Hex("#b7d3f6"), Hex("#6da7ec"), Hex("#2a78d6"), Hex("#184f95")];

    private void DrawHeatmap(RepositoryOverview o)
    {
        HeatmapCanvas.Children.Clear();
        HeatmapLegend.Children.Clear();

        const double Left = 30;
        const double Top = 18;
        double pitch = CellSize + CellGap;
        int[] daily = o.Daily;
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        // Quartiles of the days that had any commits, so one busy day does not wash the rest out.
        int[] active = [.. daily.Where(count => count > 0).Order()];
        int Quartile(double q) => active.Length == 0 ? 1 : active[Math.Min(active.Length - 1, (int)(q * active.Length))];
        int[] thresholds = [Quartile(0.25), Quartile(0.5), Quartile(0.75)];
        Color[] steps = HeatSteps;

        int Level(int count) => count == 0 ? 0 : count <= thresholds[0] ? 1 : count <= thresholds[1] ? 2 : count <= thresholds[2] ? 3 : 4;

        string[] dayNames = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;

        foreach (int row in new[] { 0, 2, 4 })
        {
            AddText(HeatmapCanvas, dayNames[(row + 1) % 7], 0, Top + (row * pitch) - 2, Left - 4, TextAlignment.Left);
        }

        int lastMonth = -1;

        for (int week = 0; week < RepositoryOverview.HeatmapWeeks; week++)
        {
            DateOnly monday = o.HeatmapStart.AddDays(week * 7);

            if (monday.Month != lastMonth && week < RepositoryOverview.HeatmapWeeks - 2)
            {
                AddText(HeatmapCanvas, monday.ToString("MMM", CultureInfo.CurrentCulture), Left + (week * pitch), 0, 40, TextAlignment.Left);
                lastMonth = monday.Month;
            }

            for (int day = 0; day < 7; day++)
            {
                DateOnly date = monday.AddDays(day);

                if (date > today)
                {
                    break;
                }

                int count = daily[(week * 7) + day];
                Rectangle cell = new()
                {
                    Width = CellSize,
                    Height = CellSize,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                    Fill = new SolidColorBrush(steps[Level(count)])
                };

                ToolTipService.SetToolTip(cell, $"{(count == 1 ? "1 commit" : $"{count} commits")} on {date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}");
                Canvas.SetLeft(cell, Left + (week * pitch));
                Canvas.SetTop(cell, Top + (day * pitch));
                HeatmapCanvas.Children.Add(cell);
            }
        }

        HeatmapCanvas.Width = Left + (RepositoryOverview.HeatmapWeeks * pitch);
        HeatmapCanvas.Height = Top + (7 * pitch);

        int busiest = daily.Max();
        int activeDays = active.Length;
        HeatmapNote.Text = $"{Count(o.CommitsLastYear)} commits on {Count(activeDays)} days" + (busiest > 0 ? $" · busiest {Count(busiest)}" : "");

        HeatmapLegend.Children.Add(new TextBlock { Text = "Less", FontSize = 11, Foreground = TextBrush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });

        foreach (Color color in steps)
        {
            HeatmapLegend.Children.Add(new Rectangle { Width = 11, Height = 11, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        }

        HeatmapLegend.Children.Add(new TextBlock { Text = "More", FontSize = 11, Foreground = TextBrush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
    }

    // ---- Contributors, weekdays and files: bars -------------------------------------------------

    private void DrawContributors(RepositoryOverview o)
    {
        ContributorBars.Children.Clear();
        int peak = o.Contributors.Count > 0 ? o.Contributors[0].Commits : 1;
        int total = Math.Max(1, o.StampsRead);
        ContributorsNote.Text = o.Contributors.Count > 8 ? $"8 of {Count(o.Contributors.Count)}" : "";

        foreach (ContributorStat person in o.Contributors.Take(8))
        {
            ContributorBars.Children.Add(BarRow(
                person.Name,
                $"{Count(person.Commits)}  ·  {person.Commits * 100.0 / total:0}%",
                person.Commits / (double)peak,
                $"{person.Name} <{person.Email}>: {Count(person.Commits)} commits"));
        }
    }

    private void DrawWeekdays()
    {
        WeekdayCanvas.Children.Clear();

        if (_overview is not RepositoryOverview o || WeekdayHost.ActualWidth < 60)
        {
            return;
        }

        double width = WeekdayHost.ActualWidth;
        double height = WeekdayHost.ActualHeight - AxisHeight;
        int peak = Math.Max(1, o.Weekdays.Max());
        double slot = width / 7;
        double bar = Math.Min(28, slot - 8);
        string[] dayNames = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        SolidColorBrush fill = new(Series);

        for (int day = 0; day < 7; day++)
        {
            int count = o.Weekdays[day];
            double barHeight = Math.Max(count > 0 ? 3 : 0, count / (double)peak * (height - 16));
            double x = (day * slot) + ((slot - bar) / 2);

            // Rounded at the data end only: the top corners, with the base square on the axis.
            Path shape = new()
            {
                Data = TopRoundedBar(x, height - barHeight, bar, barHeight, 4),
                Fill = fill
            };

            ToolTipService.SetToolTip(shape, $"{dayNames[(day + 1) % 7]}: {Count(count)} commits");
            WeekdayCanvas.Children.Add(shape);

            if (count == peak)
            {
                AddText(WeekdayCanvas, Compact(count), x - 10, height - barHeight - 16, bar + 20, TextAlignment.Center);
            }

            AddText(WeekdayCanvas, dayNames[(day + 1) % 7], day * slot, height + 4, slot, TextAlignment.Center);
        }

        WeekdayCanvas.Children.Add(new Line
        {
            X1 = 0, X2 = width, Y1 = height, Y2 = height,
            Stroke = TextBrush("DividerStrokeColorDefaultBrush"), StrokeThickness = 1
        });

        int hour = Array.IndexOf(o.Hours, o.Hours.Max());
        HoursNote.Text = o.Hours.Max() > 0 ? $"Busiest hour: {hour:00}:00–{(hour + 1) % 24:00}:00, local time" : "";
    }

    private static PathGeometry TopRoundedBar(double x, double y, double width, double height, double radius)
    {
        double r = Math.Min(radius, Math.Min(width / 2, height));
        PathFigure figure = new() { StartPoint = new Point(x, y + height), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(x, y + r) });
        figure.Segments.Add(new ArcSegment { Point = new Point(x + r, y), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(x + width - r, y) });
        figure.Segments.Add(new ArcSegment { Point = new Point(x + width, y + r), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(x + width, y + height) });
        return new PathGeometry { Figures = { figure } };
    }

    private void DrawFiles(RepositoryOverview o)
    {
        KindBars.Children.Clear();
        LargestFiles.Children.Clear();
        int peak = o.Kinds.Count > 0 ? o.Kinds.Max(kind => kind.Files) : 1;

        foreach (KindStat kind in o.Kinds)
        {
            KindBars.Children.Add(BarRow(
                KindName(kind.Kind),
                $"{Count(kind.Files)}  ·  {FileEntryRow.FormatSize(kind.Bytes)}",
                kind.Files / (double)peak,
                $"{KindName(kind.Kind)}: {Count(kind.Files)} files, {FileEntryRow.FormatSize(kind.Bytes)}"));
        }

        foreach (LargeFile file in o.Largest)
        {
            Grid row = new() { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, file.Path);

            TextBlock name = new() { Text = file.Path, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
            TextBlock size = new() { Text = FileEntryRow.FormatSize(file.Size), FontSize = 12, Foreground = TextBrush("TextFillColorSecondaryBrush") };
            Grid.SetColumn(size, 1);
            row.Children.Add(name);
            row.Children.Add(size);
            LargestFiles.Children.Add(row);
        }
    }

    private static string KindName(FileKind kind) => kind switch
    {
        FileKind.Code => "Code",
        FileKind.Markup => "Markup and styles",
        FileKind.Data => "Data and config",
        FileKind.Docs => "Docs and text",
        FileKind.Media => "Images and media",
        _ => "Other"
    };

    /// <summary>A labelled horizontal bar: the name and value in text colours, the bar in the series hue.</summary>
    private Grid BarRow(string label, string value, double fraction, string tip)
    {
        Grid row = new() { RowSpacing = 3 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ToolTipService.SetToolTip(row, tip);

        Grid text = new() { ColumnSpacing = 8 };
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock name = new() { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock amount = new() { Text = value, Foreground = TextBrush("TextFillColorSecondaryBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(amount, 1);
        text.Children.Add(name);
        text.Children.Add(amount);

        Grid track = new() { Height = 6 };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.004), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0), GridUnitType.Star) });
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Series) });
        Grid.SetRow(track, 1);

        row.Children.Add(text);
        row.Children.Add(track);
        return row;
    }

    private void AddText(Canvas canvas, string text, double left, double top, double width, TextAlignment alignment)
    {
        TextBlock block = new()
        {
            Text = text,
            FontSize = 11,
            Width = width,
            TextAlignment = alignment,
            Foreground = TextBrush("TextFillColorSecondaryBrush"),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        canvas.Children.Add(block);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section } && section.Length > 0)
        {
            RequestSection(section);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Tab?.ForgetOverview();
        _ = ActivateAsync();
    }
}
