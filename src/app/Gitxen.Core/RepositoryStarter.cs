// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.GitHub;

namespace Gitxen.Services;

/// <summary>What a new repository should start with. The template files are named, not yet fetched.</summary>
internal sealed record NewRepositoryChoices(
    string ParentDirectory,
    string Name,
    string DefaultBranch,
    RepositoryWorkflow Workflow,
    string Description,
    string GitignoreTemplate,
    string LicenseKey,
    bool Readme,
    bool GitAttributes,
    bool Publish,
    bool Private);

/// <summary>
///  The two halves of starting a repository around <see cref="RepositoryCreator.Create"/>: fetching
///  the starter files GitHub keeps, and creating the repository on GitHub afterwards. Shared by the New
///  repository dialog and <c>gx new</c>.
/// </summary>
internal static class RepositoryStarter
{
    /// <summary>
    ///  The template with its .gitignore and licence fetched from GitHub, and the licence's placeholders
    ///  filled with <paramref name="owner"/> and this year. A template that cannot be fetched is left
    ///  out rather than failing the whole repository.
    /// </summary>
    public static async Task<RepositoryTemplate> PrepareAsync(NewRepositoryChoices choices, GitHubClient gh, string owner)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(gh);

        string gitignore = choices.GitignoreTemplate.Length > 0 ? await gh.GetGitignoreTemplateAsync(choices.GitignoreTemplate) : "";
        string license = choices.LicenseKey.Length > 0 ? await gh.GetLicenseBodyAsync(choices.LicenseKey) : "";

        if (license.Length > 0)
        {
            license = RepositoryTemplate.FillLicense(license, owner.Length > 0 ? owner : Environment.UserName, DateTime.Now.Year);
        }

        return new RepositoryTemplate(
            choices.ParentDirectory,
            choices.Name,
            choices.DefaultBranch,
            choices.Workflow,
            choices.Description,
            gitignore,
            license,
            choices.Readme,
            choices.GitAttributes);
    }

    /// <summary>
    ///  Creates the repository on GitHub from the new folder and pushes it. For Gitflow, gh pushes the
    ///  branch that is checked out — <c>develop</c> — so the default branch is pushed as well.
    /// </summary>
    public static async Task<GhResult> PublishAsync(
        NewRepositoryChoices choices,
        RepositoryTemplate template,
        GitHubClient gh,
        Action<GitCommandCompleted>? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(gh);

        GhResult published = await gh.CreateRepositoryAsync(choices.Name, choices.Private, choices.Description, template.WorkingDirectory, push: true);

        if (published.Succeeded && choices.Workflow == RepositoryWorkflow.GitFlow)
        {
            GitCli git = new(template.WorkingDirectory);

            if (recorder is not null)
            {
                git.CommandCompleted += (_, command) => recorder(command);
            }

            await git.WriteAsync(new GitArgs("push").Add("--quiet", "origin", choices.DefaultBranch));
        }

        return published;
    }
}
