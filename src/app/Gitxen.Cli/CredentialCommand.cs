// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Services;

namespace Gitxen.Cli;

/// <summary>
///  <c>gx credential</c>: a git credential helper that answers for github.com with the token of the
///  account chosen for the repository — so a push from any terminal, editor or script uses the same
///  account Gitxen does.
/// </summary>
/// <remarks>
///  <para>
///   git calls <c>gx credential get</c> with <c>key=value</c> lines on standard input and reads
///   <c>username</c> and <c>password</c> back (<c>git help credential</c>). The account is, in order:
///   the <c>username</c> git passes (from the repository's <c>credential.https://github.com.username</c>),
///   Gitxen's choice for the repository, gh's active account. The token comes from gh, which holds the
///   sign-in; gx stores nothing, and ignores <c>store</c> and <c>erase</c>.
///  </para>
///  <para>
///   <c>install</c> writes two global entries for <c>https://github.com</c> only: an empty helper, which
///   clears the list git built from system config (Git Credential Manager, normally), then gx. Without
///   the reset GCM would answer first, with its one account. Other hosts are untouched.
///  </para>
/// </remarks>
internal static class CredentialCommand
{
    private const string HelperKey = "credential.https://github.com.helper";

    /// <summary>What github.com's helpers were before <c>install</c>, so <c>uninstall</c> can put them back exactly.</summary>
    private const string SavedKey = "gitxen.savedGitHubHelper";

    public static async Task<int> RunAsync(Arguments arguments)
    {
        return arguments[0] switch
        {
            "get" => await GetAsync(),
            "store" or "erase" => Drain(),
            "install" => Install(),
            "uninstall" => Uninstall(),
            "status" or "" => Status(),
            _ => throw new UsageException("gx credential install | uninstall | status  (git itself calls get, store and erase)")
        };
    }

    /// <summary>The value written to git's config: gx by its full path, so it works whatever PATH git runs with.</summary>
    private static string HelperValue => $"!'{(Environment.ProcessPath ?? "gx").Replace('\\', '/')}' credential";

    private static async Task<int> GetAsync()
    {
        Dictionary<string, string> request = ReadRequest();

        if (!request.TryGetValue("host", out string? host) || !host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || (request.TryGetValue("protocol", out string? protocol) && protocol != "https"))
        {
            return 0;
        }

        string here = Directory.GetCurrentDirectory();
        GhCli gh = new(here);
        string login = request.TryGetValue("username", out string? named) && named.Length > 0 ? named : GitHubAccountChoice.Resolve(new GitCli(here)).Login;

        if (login.Length == 0)
        {
            GhStatus status = await GitHubClient.CheckAsync(gh);
            login = status.IsReady ? status.Login : "";
        }

        if (login.Length == 0 || await GitHubClient.GetAccountTokenAsync(gh, login) is not string token)
        {
            // Saying nothing lets git carry on: it reports that it has no credential, which is true.
            return 0;
        }

        await Console.Out.WriteAsync($"username={login}\npassword={token}\n");
        await Console.Out.FlushAsync();
        return 0;
    }

    private static Dictionary<string, string> ReadRequest()
    {
        Dictionary<string, string> request = new(StringComparer.Ordinal);

        while (Console.In.ReadLine() is string line && line.Length > 0)
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0)
            {
                request[line[..equals]] = line[(equals + 1)..];
            }
        }

        return request;
    }

    private static int Drain()
    {
        ReadRequest();
        return 0;
    }

    /// <remarks>
    ///  Whatever github.com used before — often gh's own <c>gh auth git-credential</c>, from
    ///  <c>gh auth setup-git</c> — is saved first, and only when gx is not already installed, so
    ///  installing twice never saves gx as the thing to go back to.
    /// </remarks>
    private static int Install()
    {
        GitCli git = new(Directory.GetCurrentDirectory());

        if (!IsInstalled(git))
        {
            git.Write(new GitArgs("config").Add("--global", "--unset-all", SavedKey));

            foreach (string previous in GlobalValues(git, HelperKey))
            {
                git.Write(new GitArgs("config").Add("--global", "--add", SavedKey, previous));
            }
        }

        git.Write(new GitArgs("config").Add("--global", "--unset-all", HelperKey));
        GitResult reset = git.Write(new GitArgs("config").Add("--global", "--add", HelperKey, ""));
        GitResult added = git.Write(new GitArgs("config").Add("--global", "--add", HelperKey, HelperValue));

        if (!reset.Succeeded || !added.Succeeded)
        {
            Output.Error((reset.Succeeded ? added : reset).AllOutput.Trim());
            return 1;
        }

        Output.Line($"git now asks gx for github.com credentials, in every repository.");
        Output.Line(Output.Dim("Each repository uses its chosen account (gx account use <login>, or Gitxen's account menu), else gh's active one."));
        Output.Line(Output.Dim("Other hosts still use your usual credential helper. Undo with: gx credential uninstall"));
        return 0;
    }

    private static int Uninstall()
    {
        GitCli git = new(Directory.GetCurrentDirectory());

        if (!IsInstalled(git))
        {
            Output.Line("gx is not set up as a credential helper.");
            return 0;
        }

        IReadOnlyList<string> saved = GlobalValues(git, SavedKey);
        git.Write(new GitArgs("config").Add("--global", "--unset-all", HelperKey));

        foreach (string previous in saved)
        {
            git.Write(new GitArgs("config").Add("--global", "--add", HelperKey, previous));
        }

        git.Write(new GitArgs("config").Add("--global", "--unset-all", SavedKey));
        Output.Line(saved.Count > 0
            ? "Removed: github.com credentials come from the helper you had before."
            : "Removed: github.com credentials come from your usual helper again.");
        return 0;
    }

    private static bool IsInstalled(GitCli git) =>
        GlobalValues(git, HelperKey).Any(value => value.Contains("gx", StringComparison.OrdinalIgnoreCase) && value.EndsWith(" credential", StringComparison.Ordinal));

    /// <summary>Every value of a global key, in order, read NUL-separated so an empty value survives.</summary>
    private static List<string> GlobalValues(GitCli git, string key)
    {
        GitResult result = git.Read(new GitArgs("config").Add("--global", "-z", "--get-all", key));
        List<string> values = result.Succeeded ? [.. result.Text.Split('\0')] : [];

        if (values.Count > 0 && values[^1].Length == 0)
        {
            values.RemoveAt(values.Count - 1);
        }

        return values;
    }

    private static int Status()
    {
        bool installed = IsInstalled(new GitCli(Directory.GetCurrentDirectory()));

        Output.Line(installed
            ? $"Installed: git asks gx for github.com credentials. {Output.Dim("(gx credential uninstall to stop)")}"
            : $"Not installed. {Output.Dim("gx credential install makes git push/pull use each repository's chosen account.")}");
        return 0;
    }
}
