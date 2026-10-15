// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary>
///  <c>gx search &lt;words&gt;</c>: commits across every repository Gitxen has indexed, or only this one
///  with <c>--here</c>. The window keeps the same index up to date in the background; here the current
///  repository is brought up to date first, so what was just committed is found.
/// </summary>
internal static class SearchCommand
{
    public static int Run(Arguments arguments)
    {
        string text = string.Join(' ', arguments.Positional);

        if (text.Length == 0)
        {
            throw new UsageException("gx search <words…> [--here] [--limit N]");
        }

        int limit = int.TryParse(arguments.Option("limit", "20"), out int parsed) ? Math.Clamp(parsed, 1, 200) : 20;
        RepositoryIndex index = new();
        string? here = null;

        try
        {
            here = Program.RepositoryRoot("");
            index.Update(new GitCli(here), here);
        }
        catch (UsageException)
        {
            if (arguments.Has("here"))
            {
                throw;
            }
        }

        IReadOnlyList<IndexedCommit> found = index.Search(text, limit, arguments.Has("here") ? here : null);

        if (found.Count == 0)
        {
            Output.Line(index.Status().Commits == 0
                ? "Nothing is indexed yet. Open Gitxen once, or run gx search inside a repository."
                : "No commits match.");
            return 0;
        }

        bool several = found.Select(commit => commit.RepositoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        foreach (IndexedCommit commit in found)
        {
            string repository = several ? $"  {Output.Blue(commit.RepositoryName)}" : "";
            Output.Line($"{Output.Dim(commit.ShortId)}  {Output.Dim(commit.Date.ToLocalTime().ToString("yyyy-MM-dd"))}  {commit.Subject}  {Output.Dim(commit.Author)}{repository}");
        }

        return 0;
    }
}
