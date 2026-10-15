// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Gitxen.Services;

/// <summary>
///  Times and durations as short human-scale text.
/// </summary>
/// <remarks>
///  Deliberately not localised: the rest of this front-end is English-only, and the precise timestamp
///  is always one tooltip away.
/// </remarks>
public static class TimeText
{
    /// <summary>Coarse relative age: "3 days ago". Dates in the future are shown as dates.</summary>
    public static string Relative(DateTime date)
    {
        TimeSpan age = DateTime.Now - date;

        if (age < TimeSpan.Zero)
        {
            // Commits dated in the future happen — a rebase across a clock change, or a bad committer date.
            return date.ToString("d", CultureInfo.CurrentCulture);
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age.TotalDays < 1)
        {
            return Plural((int)age.TotalHours, "hour");
        }

        if (age.TotalDays < 31)
        {
            return Plural((int)age.TotalDays, "day");
        }

        if (age.TotalDays < 365)
        {
            return Plural((int)(age.TotalDays / 30), "month");
        }

        return Plural((int)(age.TotalDays / 365), "year");
    }

    /// <summary>The same for a timestamp that carries its offset, as GitHub's do.</summary>
    public static string Relative(DateTimeOffset date) => date.Year <= 1 ? "" : Relative(date.LocalDateTime);

    /// <summary>A full local timestamp, for tooltips.</summary>
    public static string Full(DateTimeOffset date) =>
        date.Year <= 1 ? "" : date.LocalDateTime.ToString("f", CultureInfo.CurrentCulture);

    /// <summary>"1m 23s", "45s", "2h 05m": the two largest units, enough to compare runs at a glance.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds}s";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        }

        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";
}
