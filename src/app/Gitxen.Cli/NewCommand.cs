// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary><c>gx new &lt;name&gt;</c>: the New repository dialog, from the command line.</summary>
internal static class NewCommand
{
    public static async Task<int> RunAsync(Arguments arguments)
    {
        string name = arguments[0];

        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new UsageException("gx new <name> [--in folder] [--flow github-flow|gitflow|trunk|none] [--gitignore X] [--license X] [--github private|public]");
        }

        string parent = Path.GetFullPath(arguments.Option("in", "."));
        RepositoryWorkflow workflow = arguments.Option("flow", "github-flow").ToLowerInvariant() switch
        {
            "github-flow" or "github" or "gh" => RepositoryWorkflow.GitHubFlow,
            "gitflow" or "git-flow" => RepositoryWorkflow.GitFlow,
            "trunk" or "trunk-based" => RepositoryWorkflow.TrunkBased,
            "none" => RepositoryWorkflow.None,
            string other => throw new UsageException($"Unknown workflow '{other}': github-flow, gitflow, trunk or none.")
        };

        string github = arguments.Option("github").ToLowerInvariant();

        if (github is not ("" or "private" or "public"))
        {
            throw new UsageException("--github takes private or public.");
        }

        GitHubClient gh = new(new GhCli(parent.Length > 0 && Directory.Exists(parent) ? parent : Directory.GetCurrentDirectory()));
        string gitignore = await MatchAsync(arguments.Option("gitignore"), async () => (await gh.ListGitignoreTemplatesAsync()).Items, "gitignore");
        string license = await MatchAsync(arguments.Option("license"), async () => [.. (await gh.ListLicensesAsync()).Items.Select(l => l.Key)], "licenses");

        NewRepositoryChoices choices = new(
            parent,
            name,
            arguments.Option("branch", "main"),
            workflow,
            arguments.Option("description"),
            gitignore,
            license,
            !arguments.Has("no-readme"),
            !arguments.Has("no-gitattributes"),
            github.Length > 0,
            github != "public");

        GitResult owner = await new GitCli(Directory.Exists(parent) ? parent : Directory.GetCurrentDirectory()).ReadAsync(new GitArgs("config").Add("--get", "user.name"));
        RepositoryTemplate template = await RepositoryStarter.PrepareAsync(choices, gh, owner.Succeeded ? owner.Output.Trim() : "");
        (GitOperationResult result, string directory) = new RepositoryCreator().Create(template);

        if (!result.Succeeded)
        {
            Output.Error(result.Output);
            return 1;
        }

        Output.Line($"Created {Output.Bold(directory)}");
        Output.Line(Output.Dim($"  {RepositoryTemplate.Title(workflow)}, on {(workflow == RepositoryWorkflow.GitFlow ? "develop" : choices.DefaultBranch)}; " +
            string.Join(", ", template.Files().Select(file => file.Path))));

        if (choices.Publish)
        {
            GhResult published = await RepositoryStarter.PublishAsync(choices, template, gh);

            if (!published.Succeeded)
            {
                Output.Error($"Created here, but not on GitHub: {published.AllOutput.Trim()}");
                return 1;
            }

            Output.Line($"On GitHub ({github}): {published.Output.Trim()}");
        }

        return 0;
    }

    /// <summary>
    ///  The template name as GitHub spells it, matched without regard to case; empty when none was
    ///  asked for. An unknown name is a usage error listing the near ones.
    /// </summary>
    private static async Task<string> MatchAsync(string wanted, Func<Task<IReadOnlyList<string>>> list, string kind)
    {
        if (wanted.Length == 0)
        {
            return "";
        }

        IReadOnlyList<string> known = await list();

        if (known.Count == 0)
        {
            throw new UsageException($"Could not read GitHub's {kind} templates. Is gh installed and signed in? (gh auth login)");
        }

        if (known.FirstOrDefault(name => name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is string match)
        {
            return match;
        }

        string[] close = [.. known.Where(name => name.Contains(wanted[..Math.Min(3, wanted.Length)], StringComparison.OrdinalIgnoreCase)).Take(8)];
        throw new UsageException($"No {kind} template '{wanted}'.{(close.Length > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} All of them: gx templates {kind}");
    }
}
