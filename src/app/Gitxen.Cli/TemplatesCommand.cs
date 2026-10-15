// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;

namespace Gitxen.Cli;

/// <summary><c>gx templates [gitignore|licenses]</c>: the starter files <c>gx new</c> can use, as GitHub keeps them.</summary>
internal static class TemplatesCommand
{
    public static async Task<int> RunAsync(Arguments arguments)
    {
        GitHubClient gh = new(new GhCli(Directory.GetCurrentDirectory()));
        string which = arguments[0].ToLowerInvariant();

        if (which is "" or "gitignore")
        {
            (IReadOnlyList<string> names, GhResult result) = await gh.ListGitignoreTemplatesAsync();

            if (!result.Succeeded)
            {
                Output.Error(result.Message);
                return 1;
            }

            Output.Line(Output.Bold(".gitignore templates") + Output.Dim("  (gx new <name> --gitignore <template>)"));
            Columns(names);
        }

        if (which is "" or "licenses" or "license")
        {
            (IReadOnlyList<LicenseInfo> licenses, GhResult result) = await gh.ListLicensesAsync();

            if (!result.Succeeded)
            {
                Output.Error(result.Message);
                return 1;
            }

            Output.Line();
            Output.Line(Output.Bold("Licences") + Output.Dim("  (gx new <name> --license <key>)"));

            foreach (LicenseInfo license in licenses)
            {
                Output.Line($"  {license.Key,-14} {Output.Dim(license.Name)}");
            }
        }

        return 0;
    }

    private static void Columns(IReadOnlyList<string> names)
    {
        const int Width = 26;
        int perLine = Math.Max(1, (Console.IsOutputRedirected ? 100 : Math.Max(40, Console.WindowWidth)) / Width);

        for (int i = 0; i < names.Count; i += perLine)
        {
            Output.Line("  " + string.Concat(names.Skip(i).Take(perLine).Select(name => name.PadRight(Width))).TrimEnd());
        }
    }
}
