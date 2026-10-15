// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary>
///  <c>gx account</c>: which GitHub account a repository uses, and choosing another. The choice is the
///  repository's own <c>credential.https://github.com.username</c>, which Gitxen, Git Credential
///  Manager and <c>gx credential</c> all read.
/// </summary>
internal static class AccountCommand
{
    public static async Task<int> RunAsync(Arguments arguments)
    {
        string action = arguments[0];
        string root = Program.RepositoryRoot(action is "use" or "auto" ? arguments[action == "use" ? 2 : 1] : arguments[0]);
        GitCli git = new(root);
        GhStatus status = await GitHubClient.CheckAsync(new GhCli(root));

        switch (action)
        {
            case "use":
                return Use(git, status, arguments[1]);

            case "auto":
                await git.WriteAsync(new GitArgs("config").Add("--local", "--unset", GitHubAccountChoice.CredentialUserKey));
                (string login, AccountSource source) = GitHubAccountChoice.Resolve(git);
                Output.Line(source == AccountSource.Gitxen
                    ? $"This repository now uses {Output.Bold(login)}, the account Gitxen found for it."
                    : $"This repository now uses gh's active account ({Output.Bold(Show(status.Login))}).");
                return 0;

            default:
                return Describe(git, status);
        }
    }

    private static int Use(GitCli git, GhStatus status, string login)
    {
        if (login.Length == 0)
        {
            throw new UsageException("gx account use <login>");
        }

        if (!status.IsReady)
        {
            Output.Error(status.Detail.Length > 0 ? status.Detail : "gh is not signed in. Run: gh auth login");
            return 1;
        }

        string? match = status.Accounts.FirstOrDefault(account => account.Equals(login, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Output.Error($"{login} is not signed in to gh. Signed in: {string.Join(", ", status.Accounts)}. Add it with: gh auth login");
            return 1;
        }

        GitResult set = git.Write(new GitArgs("config").Add("--local", GitHubAccountChoice.CredentialUserKey, match));

        if (!set.Succeeded)
        {
            Output.Error(set.AllOutput.Trim());
            return 1;
        }

        Output.Line($"This repository now uses {Output.Bold(match)} — in Gitxen, and for git push and pull.");
        return 0;
    }

    private static int Describe(GitCli git, GhStatus status)
    {
        HostedRepository? host = GitHubAccountChoice.Host(git);

        if (host is not { IsGitHub: true })
        {
            Output.Line("This repository has no GitHub remote.");
            return 0;
        }

        (string login, AccountSource source) = GitHubAccountChoice.Resolve(git);
        string uses = source switch
        {
            AccountSource.Repository => $"{Output.Bold(login)} {Output.Dim("(this repository's setting)")}",
            AccountSource.Gitxen => $"{Output.Bold(login)} {Output.Dim("(chosen in Gitxen)")}",
            _ => $"{Output.Bold(Show(status.Login))} {Output.Dim("(gh's active account; nothing chosen here)")}"
        };

        Output.Line($"{host.Owner}/{host.Name}");
        Output.Line($"  uses        {uses}");
        Output.Line($"  signed in   {(status.IsReady ? string.Join(", ", status.Accounts) : Output.Yellow("gh is not signed in — gh auth login"))}");

        GitResult helper = git.Read(new GitArgs("config").Add("--get-all", "credential.https://github.com.helper"));

        if (!(helper.Succeeded && helper.Output.Contains("gx", StringComparison.OrdinalIgnoreCase)))
        {
            Output.Line(Output.Dim("  git push from a terminal may still sign in as another account: gx credential install"));
        }

        return 0;
    }

    private static string Show(string login) => login.Length > 0 ? login : "none";
}
