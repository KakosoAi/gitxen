// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  Reads and writes <c>git config --global</c>, with no repository open.
/// </summary>
/// <remarks>
///  <para>
///   <see cref="RepositoryLoader"/> can already do this, but only for a repository that exists: it is
///   built around one working directory. The setup wizard runs before anything has been opened and
///   still needs to ask about the identity that every future commit will carry, so this runs git in
///   the user's profile — a folder that is always there and that git is happy to run in.
///  </para>
///  <para>
///   Entirely on Gitxen's own git layer, and the first file to be so. It needs nothing from the
///   inherited engine: global configuration is four <c>git config</c> invocations and a version
///   check.
///  </para>
/// </remarks>
internal sealed class GlobalGitSettings
{
    private readonly GitCli _git;

    public GlobalGitSettings()
    {
        _git = new GitCli(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _git.CommandCompleted += (_, command) => GitActivity.Record(command);
    }

    /// <summary>The value at global scope, empty when it is not set there.</summary>
    public string Get(string key)
    {
        // Exit code 1 simply means "not set", which is the normal answer on a fresh machine.
        GitResult result = _git.Read(new GitArgs("config").Add("--global", "--get", key));

        return result.Succeeded ? result.Output.Trim() : "";
    }

    /// <summary>
    ///  Sets a value, or removes the setting when <paramref name="value"/> is empty.
    /// </summary>
    /// <remarks>
    ///  <c>user.name=</c> is a name that is genuinely empty and makes git refuse to commit, whereas
    ///  an absent key is what someone clearing a box meant.
    /// </remarks>
    public GitOperationResult Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            GitResult cleared = _git.Write(new GitArgs("config").Add("--global", "--unset-all", key));

            // Exit code 5 is "nothing to unset", which is success as far as the caller is concerned.
            return cleared.Succeeded || cleared.ExitCode == 5
                ? new GitOperationResult($"Clear {key}", true, "")
                : new GitOperationResult($"Clear {key}", false, cleared.AllOutput.Trim());
        }

        // No quoting here, and none wanted: arguments are passed to the process individually, so a
        // value containing spaces arrives intact and quotes added by hand would become part of it.
        GitResult result = _git.Write(new GitArgs("config").Add("--global", key, value.Trim()));

        return new GitOperationResult($"Set {key}", result.Succeeded, result.AllOutput.Trim());
    }

    /// <summary>Whether git itself can be run at all, which is worth knowing before anything else.</summary>
    public bool IsGitAvailable() => Version().Length > 0;

    /// <summary>The version string, for the wizard to show when it reports git was found.</summary>
    public string GetVersion() => Version();

    private string Version()
    {
        try
        {
            GitResult result = _git.Read(new GitArgs("--version"));
            return result.Succeeded ? result.Output.Trim() : "";
        }
        catch (Exception)
        {
            // No git on PATH at all: the process could not even be started.
            return "";
        }
    }
}
