// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Cli;

/// <summary>
///  Terminal output: colour only when writing to a terminal that wants it (not redirected, and
///  <c>NO_COLOR</c> unset), so pipes and scripts always get plain text.
/// </summary>
internal static class Output
{
    private static readonly bool Colour =
        !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static string Bold(string text) => Wrap("1", text);

    public static string Dim(string text) => Wrap("2", text);

    public static string Blue(string text) => Wrap("34", text);

    public static string Green(string text) => Wrap("32", text);

    public static string Yellow(string text) => Wrap("33", text);

    public static void Line(string text = "") => Console.Out.WriteLine(text);

    public static void Error(string text) => Console.Error.WriteLine(Colour ? $"\u001b[31m{text}\u001b[0m" : text);

    /// <summary>A bar of <paramref name="width"/> cells, filled to <paramref name="fraction"/>.</summary>
    public static string Bar(double fraction, int width)
    {
        int filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return Blue(new string('█', filled)) + Dim(new string('░', width - filled));
    }

    /// <summary>One character per value, eight heights, scaled to the largest.</summary>
    public static string Sparkline(IReadOnlyList<int> values)
    {
        const string Heights = " ▁▂▃▄▅▆▇█";
        int peak = values.Count == 0 ? 0 : values.Max();

        return peak == 0
            ? new string(' ', values.Count)
            : Blue(string.Concat(values.Select(value => value == 0 ? ' ' : Heights[Math.Max(1, (int)Math.Round(value / (double)peak * 8))])));
    }

    private static string Wrap(string code, string text) => Colour ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
