// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Gitxen.Git;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary><c>gx stats [path] [--json]</c>: the Overview page, in the terminal or as JSON for scripts.</summary>
internal static class StatsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(Arguments arguments)
    {
        string root = Program.RepositoryRoot(arguments[0]);
        GitCli git = new(root);
        RepositoryOverview o = OverviewReader.Read(git);

        if (arguments.Has("json"))
        {
            Output.Line(JsonSerializer.Serialize(ToJson(root, o), JsonOptions));
            return 0;
        }

        Output.Line($"{Output.Bold(Path.GetFileName(root))}  {Output.Dim(o.HeadId.Length > 0 ? $"at {o.HeadId[..8]}" : "no commits yet")}");

        if (o.Commits == 0)
        {
            return 0;
        }

        Output.Line();
        Row("Commits", Count(o.Commits), o.LastCommit is DateTimeOffset last ? $"last {Ago(last)}" : "");
        Row("Contributors", Count(o.Contributors.Count), o.Contributors.Count > 0 ? $"most: {o.Contributors[0].Name}" : "");
        Row("Branches", Count(o.Branches), $"{Count(o.Tags)} tags");
        Row("Files", Count(o.Files), $"{Size(o.Bytes)}{(o.Submodules > 0 ? $", {o.Submodules} submodules" : "")}");
        Row("Lines", Count(o.Lines), "in text files");
        Row("Age", o.FirstCommit is DateTimeOffset first ? Ago(first).Replace(" ago", "", StringComparison.Ordinal) : "", o.FirstCommit is DateTimeOffset since ? $"since {since:d MMM yyyy}" : "");
        Row("This year", Count(o.CommitsLastYear), "commits in the last 12 months");

        if (o.Languages.Count > 0)
        {
            Output.Line();
            Output.Line(Output.Bold("Languages"));

            foreach (LanguageStat language in o.Languages)
            {
                Output.Line($"  {language.Language.Name,-16} {Output.Bar(language.Share, 28)} {language.Share * 100,5:0.0}%  {Output.Dim($"{Count(language.Lines)} lines")}");
            }
        }

        Output.Line();
        Output.Line(Output.Bold("Top contributors"));
        int peak = o.Contributors.Count > 0 ? o.Contributors[0].Commits : 1;

        foreach (ContributorStat person in o.Contributors.Take(8))
        {
            Output.Line($"  {Trim(person.Name, 22),-22} {Output.Bar(person.Commits / (double)peak, 20)} {Count(person.Commits),7}");
        }

        IReadOnlyList<MonthCount> months = [.. o.Monthly.TakeLast(24)];

        if (months.Count > 1)
        {
            Output.Line();
            Output.Line($"{Output.Bold("Commits by month")}  {Output.Dim($"{months[0].Month:MMM yyyy} – {months[^1].Month:MMM yyyy}")}");
            Output.Line($"  {Output.Sparkline([.. months.Select(month => month.Commits)])}");
        }

        Output.Line();
        Output.Line(Output.Bold("Weekdays"));
        string[] days = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int busiest = Math.Max(1, o.Weekdays.Max());

        for (int day = 0; day < 7; day++)
        {
            Output.Line($"  {days[(day + 1) % 7],-4} {Output.Bar(o.Weekdays[day] / (double)busiest, 20)} {Count(o.Weekdays[day]),7}");
        }

        if (o.StampsRead < o.Commits)
        {
            Output.Line();
            Output.Line(Output.Dim($"Dates and authors from the latest {Count(o.StampsRead)} commits."));
        }

        return 0;
    }

    private static void Row(string label, string value, string detail) =>
        Output.Line($"  {label,-13} {Output.Bold(value),-10} {Output.Dim(detail)}");

    private static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Trim(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static string Ago(DateTimeOffset time)
    {
        TimeSpan span = DateTimeOffset.Now - time;

        return span.TotalDays >= 730 ? $"{(int)(span.TotalDays / 365)} years ago"
            : span.TotalDays >= 60 ? $"{(int)(span.TotalDays / 30)} months ago"
            : span.TotalDays >= 2 ? $"{(int)span.TotalDays} days ago"
            : span.TotalHours >= 2 ? $"{(int)span.TotalHours} hours ago"
            : "just now";
    }

    private static object ToJson(string root, RepositoryOverview o) => new
    {
        repository = root,
        head = o.HeadId,
        commits = o.Commits,
        commitsRead = o.StampsRead,
        firstCommit = o.FirstCommit,
        lastCommit = o.LastCommit,
        commitsLastYear = o.CommitsLastYear,
        branches = o.Branches,
        tags = o.Tags,
        files = o.Files,
        bytes = o.Bytes,
        lines = o.Lines,
        submodules = o.Submodules,
        languages = o.Languages.Select(l => new { name = l.Language.Name, share = Math.Round(l.Share, 4), bytes = l.Bytes, lines = l.Lines, files = l.Files }),
        contributors = o.Contributors.Select(c => new { name = c.Name, email = c.Email, commits = c.Commits }),
        monthly = o.Monthly.Select(m => new { month = m.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture), commits = m.Commits }),
        weekdays = o.Weekdays,
        hours = o.Hours,
        kinds = o.Kinds.Select(k => new { kind = k.Kind.ToString(), files = k.Files, bytes = k.Bytes }),
        largest = o.Largest.Select(f => new { path = f.Path, bytes = f.Size })
    };
}
