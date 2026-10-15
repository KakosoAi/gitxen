// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  The GitHub account a repository's pushes, pulls and fetches authenticate as, with the token gh
///  holds for it.
/// </summary>
/// <remarks>
///  <para>
///   Git's credential helper — Git Credential Manager, normally — keeps one sign-in per host, so
///   every github.com repository pushes as the same account whatever the GitHub pages are using. A
///   private repository that account cannot see answers "Repository not found".
///  </para>
///  <para>
///   <see cref="Apply"/> fixes that for the one command: it clears the helper list for github.com and
///   puts in its place a helper that answers with this account's token, which it reads from an
///   environment variable of that process. Nothing is written anywhere — not to a config file, not to
///   the credential store (the helper ignores <c>store</c>), not to a command line — and the helper's
///   text, which the activity console shows, holds only the variable's name.
///  </para>
/// </remarks>
internal sealed partial record GitHubCredential(string Login, string Token)
{
    private const string TokenVariable = "GITXEN_GITHUB_TOKEN";
    private const string HelperKey = "credential.https://github.com.helper";

    /// <summary>GitHub's own rule for logins, which also keeps one safe inside the helper's shell text.</summary>
    public static bool IsValidLogin(string login) => LoginPattern().IsMatch(login);

    public GitArgs Apply(GitArgs args) => args
        .WithConfig(HelperKey, "")
        .WithConfig(HelperKey, $"!f() {{ test \"$1\" = get && echo \"username={Login}\" && echo \"password=${TokenVariable}\"; }}; f")
        .WithEnvironment(TokenVariable, Token);

    /// <summary>A record prints every member by default; this one must never print its token.</summary>
    public override string ToString() => $"GitHubCredential {{ Login = {Login} }}";

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex LoginPattern();
}
