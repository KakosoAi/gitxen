// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Models;

namespace Gitxen.Services;

/// <summary>
///  Reading and writing git configuration.
/// </summary>
/// <remarks>
///  Configuration is edited through <c>git config</c> rather than by writing the files directly: git
///  owns the include directives, the conditional includes and the escaping rules, and a front-end
///  that rewrites the file itself gets those wrong sooner or later.
/// </remarks>
internal sealed partial class RepositoryLoader
{
    /// <summary>
    ///  Every setting at one scope, as git resolves it.
    /// </summary>
    /// <remarks>
    ///  <c>--null</c> terminates each entry with a NUL rather than a newline, which is what makes
    ///  multi-line values (a commit template, a signing program with arguments) readable at all.
    /// </remarks>
    public IReadOnlyList<GitConfigEntry> GetConfiguration(GitConfigScope scope)
    {
        GitResult result = _git.Read(new GitArgs("config").Add(ScopeArgument(scope), "--list", "--null"));

        if (!result.Succeeded)
        {
            // No global file yet, or not a repository — an empty list is the honest answer.
            return [];
        }

        List<GitConfigEntry> entries = [];

        foreach (string record in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "key\nvalue", or just "key" for a valueless boolean.
            int newline = record.IndexOf('\n');

            string key = newline < 0 ? record : record[..newline];
            string value = newline < 0 ? "" : record[(newline + 1)..];

            entries.Add(new GitConfigEntry(key.Trim(), value, scope));
        }

        return entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads one setting at one scope, empty when it is not set there.</summary>
    public string GetConfigValue(GitConfigScope scope, string key)
    {
        GitResult result = _git.Read(new GitArgs("config").Add(ScopeArgument(scope), "--get", key));

        return result.Succeeded ? result.Output.Trim() : "";
    }

    /// <summary>
    ///  Sets a value, or removes the setting when <paramref name="value"/> is empty.
    /// </summary>
    /// <remarks>
    ///  Clearing rather than storing an empty string matters: git treats <c>user.name=</c> as a name
    ///  that is genuinely empty and will refuse to commit, whereas an absent key falls through to the
    ///  next scope, which is almost always what someone emptying a box meant.
    /// </remarks>
    public GitOperationResult SetConfigValue(GitConfigScope scope, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            GitResult cleared = _git.Write(new GitArgs("config").Add(ScopeArgument(scope), "--unset-all", key));

            // Exit code 5 is "nothing to unset", which is success as far as the caller is concerned.
            return cleared.Succeeded || cleared.ExitCode == 5
                ? new GitOperationResult($"Clear {key}", true, "")
                : new GitOperationResult($"Clear {key}", false, cleared.AllOutput.Trim());
        }

        // Nothing is quoted here, and nothing should be: arguments reach the process individually,
        // so a value containing spaces arrives intact and hand-added quotes would become part of it.
        return Run($"Set {key}", new GitArgs("config").Add(ScopeArgument(scope), key, value));
    }

    private static string ScopeArgument(GitConfigScope scope) => scope switch
    {
        GitConfigScope.Global => "--global",
        _ => "--local"
    };
}
