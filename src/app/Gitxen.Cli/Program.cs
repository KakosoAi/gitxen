// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text;
using Gitxen.Git;

namespace Gitxen.Cli;

/// <summary>
///  <c>gx</c>: Gitxen from the command line.
/// </summary>
/// <remarks>
///  Exit codes: 0 done, 1 git or gh said no (its message is printed), 2 the command line was wrong.
/// </remarks>
internal static class Program
{
    private const string Usage = """
        gx — Gitxen from the command line

        Usage:
          gx open [path]            open a repository in the Gitxen window (also: gx .)
          gx stats [path] [--json]  the repository's figures: commits, languages, contributors, activity
          gx search <words…> [--here] [--limit N]
                                    find commits in every repository Gitxen has indexed
          gx new <name> [options]   start a repository from a workflow and starter files
          gx account                which GitHub account this repository uses, and the others signed in
          gx account use <login>    use that account here, for gh and for git push/pull
          gx account auto           go back to gh's active account
          gx credential install     let git ask gx for each repository's GitHub account (all repositories)
          gx credential uninstall   stop doing that
          gx credential status      whether it is set up
          gx speedup [--status]     turn on git's speed-ups for large repositories
          gx templates [gitignore|licenses]
                                    what gx new can start with
          gx version

        gx new options:
          --in <folder>             where to create it (default: here)
          --flow <workflow>         github-flow (default), gitflow, trunk or none
          --branch <name>           default branch (default: main)
          --gitignore <template>    a GitHub .gitignore template, e.g. VisualStudio, Node, Python
          --license <key>           a licence, e.g. mit, apache-2.0
          --description <text>      one line for the README and GitHub
          --no-readme --no-gitattributes
          --github private|public   also create it on GitHub and push

        gx speedup options:
          --watcher / --no-watcher  also start, or stop, git's file watcher
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string[] rest = args.Length > 1 ? args[1..] : [];

        try
        {
            return command switch
            {
                "." => OpenCommand.Run(new Arguments(["."])),
                "open" => OpenCommand.Run(new Arguments(rest)),
                "stats" => StatsCommand.Run(new Arguments(rest)),
                "search" => SearchCommand.Run(new Arguments(rest, "limit")),
                "new" => await NewCommand.RunAsync(new Arguments(rest, "in", "flow", "branch", "gitignore", "license", "description", "github")),
                "account" => await AccountCommand.RunAsync(new Arguments(rest)),
                "credential" => await CredentialCommand.RunAsync(new Arguments(rest)),
                "speedup" => SpeedCommand.Run(new Arguments(rest)),
                "templates" => await TemplatesCommand.RunAsync(new Arguments(rest)),
                "version" or "--version" or "-v" => Version(),
                "help" or "--help" or "-h" or "/?" => Help(),
                _ => throw new UsageException($"Unknown command '{args[0]}'. Run gx help.")
            };
        }
        catch (UsageException ex)
        {
            Output.Error(ex.Message);
            return 2;
        }
    }

    /// <summary>The repository containing <paramref name="path"/> (default: here), or a usage error saying there is none.</summary>
    public static string RepositoryRoot(string path)
    {
        string start = Path.GetFullPath(path.Length == 0 ? "." : path);

        if (!Directory.Exists(start))
        {
            throw new UsageException($"{start} does not exist.");
        }

        GitResult top = new GitCli(start).Read(new GitArgs("rev-parse").Add("--show-toplevel"));

        return top.Succeeded
            ? Path.GetFullPath(top.Output.Trim())
            : throw new UsageException($"{start} is not inside a git repository.");
    }

    private static int Version()
    {
        string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        Output.Line($"gx {version.Split('+')[0]}");
        return 0;
    }

    private static int Help()
    {
        Output.Line(Usage);
        return 0;
    }
}
