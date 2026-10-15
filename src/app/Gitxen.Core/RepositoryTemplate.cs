// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>How a new repository's branches are meant to be used.</summary>
internal enum RepositoryWorkflow
{
    /// <summary>One branch and nothing said about how to use it.</summary>
    None,

    /// <summary>A protected <c>main</c>; short-lived feature branches merged back through pull requests.</summary>
    GitHubFlow,

    /// <summary><c>main</c> for releases, <c>develop</c> for integration, prefixed feature/release/hotfix branches.</summary>
    GitFlow,

    /// <summary>Everyone commits to <c>main</c> little and often; branches, if any, live for a day or two.</summary>
    TrunkBased
}

/// <summary>Everything a new repository starts with. The file texts are already fetched.</summary>
/// <param name="GitignoreText">The .gitignore to write, or empty for none.</param>
/// <param name="LicenseText">The licence with its placeholders filled in, or empty for none.</param>
internal sealed record RepositoryTemplate(
    string ParentDirectory,
    string Name,
    string DefaultBranch,
    RepositoryWorkflow Workflow,
    string Description,
    string GitignoreText,
    string LicenseText,
    bool Readme,
    bool GitAttributes)
{
    public string WorkingDirectory => Path.Combine(ParentDirectory, Name);

    /// <summary>One line for the workflow, as the dialog and the README describe it.</summary>
    public static string Describe(RepositoryWorkflow workflow) => workflow switch
    {
        RepositoryWorkflow.GitHubFlow => "main is always releasable. Work happens on short-lived branches that come back through pull requests.",
        RepositoryWorkflow.GitFlow => "main holds releases, develop gathers work. Features branch from develop; releases and hotfixes have their own prefixed branches.",
        RepositoryWorkflow.TrunkBased => "Everyone commits to main in small steps, behind feature flags where needed. Branches, if any, last a day or two.",
        _ => "A single branch, with no convention set up.",
    };

    public static string Title(RepositoryWorkflow workflow) => workflow switch
    {
        RepositoryWorkflow.GitHubFlow => "GitHub Flow",
        RepositoryWorkflow.GitFlow => "Gitflow",
        RepositoryWorkflow.TrunkBased => "Trunk-based development",
        _ => "None",
    };

    /// <summary>
    ///  Fills a licence's placeholders. GitHub's texts use two spellings: <c>[year]</c>/<c>[fullname]</c>
    ///  (MIT, BSD) and <c>[yyyy]</c>/<c>[name of copyright owner]</c> (Apache); the rest have none.
    /// </summary>
    public static string FillLicense(string body, string owner, int year) => body
        .Replace("[year]", year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("[yyyy]", year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("[fullname]", owner, StringComparison.Ordinal)
        .Replace("[name of copyright owner]", owner, StringComparison.Ordinal);

    /// <summary>The files to write, by path relative to the working directory.</summary>
    public IEnumerable<(string Path, string Text)> Files()
    {
        if (Readme)
        {
            yield return ("README.md", ReadmeText());
        }

        if (GitignoreText.Length > 0)
        {
            yield return (".gitignore", EndWithNewline(GitignoreText));
        }

        if (LicenseText.Length > 0)
        {
            yield return ("LICENSE", EndWithNewline(LicenseText));
        }

        if (GitAttributes)
        {
            // Normalise line endings in the repository and leave each checkout its platform's own.
            yield return (".gitattributes", "* text=auto\n\n*.sh text eol=lf\n*.cmd text eol=crlf\n*.bat text eol=crlf\n*.ps1 text eol=crlf\n");
        }

        if (Workflow is RepositoryWorkflow.GitHubFlow or RepositoryWorkflow.GitFlow)
        {
            yield return (".github/pull_request_template.md", "## What\n\n\n## Why\n\n\n## How it was checked\n\n");
        }
    }

    private static string EndWithNewline(string text) => text.EndsWith('\n') ? text : text + "\n";

    private string ReadmeText()
    {
        System.Text.StringBuilder text = new();
        text.Append("# ").Append(Name).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(Description))
        {
            text.Append(Description.Trim()).Append("\n\n");
        }

        if (Workflow != RepositoryWorkflow.None)
        {
            text.Append("## Workflow\n\n").Append("This repository uses **").Append(Title(Workflow)).Append("**. ")
                .Append(Describe(Workflow).Replace("main", $"`{DefaultBranch}`", StringComparison.Ordinal).Replace("develop", "`develop`", StringComparison.Ordinal))
                .Append("\n");

            if (Workflow == RepositoryWorkflow.GitFlow)
            {
                text.Append("\nBranch prefixes: `feature/`, `release/`, `hotfix/`, `support/`.\n");
            }
        }

        return text.ToString();
    }
}
