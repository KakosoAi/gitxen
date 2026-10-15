// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

internal sealed record ContributorStat(string Name, string Email, int Commits);

/// <param name="Share">Of the bytes of every language shown, 0 to 1.</param>
internal sealed record LanguageStat(LanguageInfo Language, long Bytes, long Lines, int Files, double Share);

internal sealed record KindStat(FileKind Kind, int Files, long Bytes);

internal sealed record LargeFile(string Path, long Size);

/// <summary>Commits in one calendar month.</summary>
internal sealed record MonthCount(DateOnly Month, int Commits);

/// <summary>Everything the Overview page shows, read at one commit.</summary>
/// <param name="HeadId">The commit it describes; the whole answer holds for as long as that is <c>HEAD</c>.</param>
/// <param name="StampsRead">How many commits the dates and authors come from — all of them, unless the history is enormous.</param>
/// <param name="Daily">Commits per day for the heatmap: <see cref="HeatmapStart"/> onwards, 53 weeks of 7 days.</param>
/// <param name="Weekdays">Commits by weekday, Monday first.</param>
internal sealed record RepositoryOverview(
    string HeadId,
    int Commits,
    int StampsRead,
    DateTimeOffset? FirstCommit,
    DateTimeOffset? LastCommit,
    IReadOnlyList<ContributorStat> Contributors,
    IReadOnlyList<MonthCount> Monthly,
    DateOnly HeatmapStart,
    int[] Daily,
    int[] Weekdays,
    int[] Hours,
    int Branches,
    int Tags,
    int Files,
    long Bytes,
    long Lines,
    int Submodules,
    IReadOnlyList<LanguageStat> Languages,
    IReadOnlyList<KindStat> Kinds,
    IReadOnlyList<LargeFile> Largest)
{
    public const int HeatmapWeeks = 53;

    public int CommitsLastYear => Daily.Sum();

    public static RepositoryOverview Empty(string headId) => new(
        headId, 0, 0, null, null, [], [], DateOnly.FromDateTime(DateTime.Today), new int[HeatmapWeeks * 7],
        new int[7], new int[24], 0, 0, 0, 0, 0, 0, [], [], []);
}

/// <summary>Turns the raw reads of <see cref="GitStats"/> into a <see cref="RepositoryOverview"/>. Pure.</summary>
internal static class OverviewBuilder
{
    /// <summary>Languages beyond this fold into "Other", so the chart keeps colours a reader can tell apart.</summary>
    public const int LanguageSlots = 7;

    public static RepositoryOverview Build(
        string headId,
        int commits,
        IReadOnlyList<GitCommitStamp> stamps,
        IReadOnlyList<GitTreeFile> tree,
        IReadOnlyDictionary<string, int> lines,
        (int Branches, int Tags) refs,
        DateTime today)
    {
        DateTimeOffset[] times = [.. stamps.Select(stamp => DateTimeOffset.FromUnixTimeSeconds(stamp.Time).ToLocalTime())];

        // The heatmap ends with the week holding today and starts 52 weeks before that week's Monday.
        DateOnly thisMonday = DateOnly.FromDateTime(today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7)));
        DateOnly heatmapStart = thisMonday.AddDays(-7 * (RepositoryOverview.HeatmapWeeks - 1));
        int[] daily = new int[RepositoryOverview.HeatmapWeeks * 7];
        int[] weekdays = new int[7];
        int[] hours = new int[24];
        SortedDictionary<DateOnly, int> months = [];

        foreach (DateTimeOffset time in times)
        {
            DateOnly day = DateOnly.FromDateTime(time.DateTime);
            int index = day.DayNumber - heatmapStart.DayNumber;

            if (index >= 0 && index < daily.Length)
            {
                daily[index]++;
            }

            weekdays[((int)time.DayOfWeek + 6) % 7]++;
            hours[time.Hour]++;

            DateOnly month = new(day.Year, day.Month, 1);
            months[month] = months.GetValueOrDefault(month) + 1;
        }

        List<ContributorStat> contributors = [.. stamps
            .GroupBy(stamp => (stamp.Author, stamp.Email.ToLowerInvariant()))
            .Select(group => new ContributorStat(group.Key.Author, group.First().Email, group.Count()))
            .GroupBy(contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ContributorStat(group.First().Name, group.OrderByDescending(c => c.Commits).First().Email, group.Sum(c => c.Commits)))
            .OrderByDescending(contributor => contributor.Commits)
            .ThenBy(contributor => contributor.Name, StringComparer.CurrentCultureIgnoreCase)];

        List<GitTreeFile> files = [.. tree.Where(file => !file.IsSubmodule)];

        return new RepositoryOverview(
            headId,
            commits,
            stamps.Count,
            times.Length > 0 ? times[^1] : null,
            times.Length > 0 ? times[0] : null,
            contributors,
            FillMonths(months),
            heatmapStart,
            daily,
            weekdays,
            hours,
            refs.Branches,
            refs.Tags,
            files.Count,
            files.Sum(file => file.Size),
            lines.Values.Sum(count => (long)count),
            tree.Count(file => file.IsSubmodule),
            Languages(files, lines),
            [.. files
                .GroupBy(file => LanguageMap.Of(file.Path).Kind)
                .Select(group => new KindStat(group.Key, group.Count(), group.Sum(file => file.Size)))
                .OrderByDescending(kind => kind.Files)],
            [.. files.OrderByDescending(file => file.Size).Take(6).Select(file => new LargeFile(file.Path, file.Size))]);
    }

    /// <summary>Every month from the first commit to the last, including the empty ones, so the chart's x axis is time.</summary>
    private static List<MonthCount> FillMonths(SortedDictionary<DateOnly, int> months)
    {
        List<MonthCount> filled = [];

        if (months.Count == 0)
        {
            return filled;
        }

        for (DateOnly month = months.Keys.First(); month <= months.Keys.Last(); month = month.AddMonths(1))
        {
            filled.Add(new MonthCount(month, months.GetValueOrDefault(month)));
        }

        return filled;
    }

    /// <summary>
    ///  Share by bytes, as GitHub measures it, of the code and markup a person wrote: data, docs and
    ///  media are left out, and so is anything generated or vendored.
    /// </summary>
    private static List<LanguageStat> Languages(List<GitTreeFile> files, IReadOnlyDictionary<string, int> lines)
    {
        List<(LanguageInfo Language, long Bytes, long Lines, int Files)> totals = [.. files
            .Where(file => !LanguageMap.IsGenerated(file.Path))
            .Select(file => (File: file, Language: LanguageMap.Of(file.Path)))
            .Where(entry => entry.Language.Kind is FileKind.Code or FileKind.Markup)
            .GroupBy(entry => entry.Language)
            .Select(group => (group.Key, group.Sum(entry => entry.File.Size), group.Sum(entry => (long)lines.GetValueOrDefault(entry.File.Path)), group.Count()))
            .OrderByDescending(entry => entry.Item2)];

        long all = totals.Sum(entry => entry.Bytes);

        if (all == 0)
        {
            return [];
        }

        List<LanguageStat> shown = [.. totals.Take(LanguageSlots).Select(entry =>
            new LanguageStat(entry.Language, entry.Bytes, entry.Lines, entry.Files, entry.Bytes / (double)all))];

        List<(LanguageInfo Language, long Bytes, long Lines, int Files)> rest = [.. totals.Skip(LanguageSlots)];

        if (rest.Count > 0)
        {
            long bytes = rest.Sum(entry => entry.Bytes);
            shown.Add(new LanguageStat(
                new LanguageInfo($"Other ({rest.Count})", "#8b8b8b", FileKind.Other),
                bytes,
                rest.Sum(entry => entry.Lines),
                rest.Sum(entry => entry.Files),
                bytes / (double)all));
        }

        return shown;
    }
}
