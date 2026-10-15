// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>Where a repository's GitHub account was chosen.</summary>
internal enum AccountSource
{
    /// <summary>Nothing chose one: gh's active account is used.</summary>
    None,

    /// <summary>The repository's own <c>credential.https://github.com.username</c>, set by gx or by choosing in Gitxen.</summary>
    Repository,

    /// <summary>Gitxen's per-repository choice, kept in its session by <c>owner/name</c>.</summary>
    Gitxen
}

/// <summary>
///  Which GitHub account a repository uses, as the window and <c>gx</c> both see it.
/// </summary>
/// <remarks>
///  The repository's own config comes first: it is what Git Credential Manager and <c>gx credential</c>
///  read, and what choosing an account in either place writes. Gitxen's session is second, so a choice
///  it found automatically (the first account that could see a private repository) still applies.
/// </remarks>
internal static class GitHubAccountChoice
{
    /// <summary>The per-repository setting that names the github.com account to authenticate as.</summary>
    public const string CredentialUserKey = "credential.https://github.com.username";

    /// <summary>Gitxen's session file, shared with the window. Only ever read here.</summary>
    public static string SessionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gitxen", "session.json");

    /// <summary>The account chosen for the repository <paramref name="git"/> runs in, and where the choice came from.</summary>
    public static (string Login, AccountSource Source) Resolve(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult local = git.Read(new GitArgs("config").Add("--local", "--get", CredentialUserKey));

        if (local.Succeeded && local.Output.Trim() is { Length: > 0 } configured)
        {
            return (configured, AccountSource.Repository);
        }

        if (Host(git) is HostedRepository { IsGitHub: true } host && ChosenInGitxen($"{host.Owner}/{host.Name}") is { Length: > 0 } chosen)
        {
            return (chosen, AccountSource.Gitxen);
        }

        return ("", AccountSource.None);
    }

    /// <summary>The GitHub repository behind <c>origin</c>, or the first remote when there is no origin.</summary>
    public static HostedRepository? Host(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult origin = git.Read(new GitArgs("config").Add("--get", "remote.origin.url"));

        if (origin.Succeeded && GitHostLinks.Parse(origin.Output.Trim()) is HostedRepository fromOrigin)
        {
            return fromOrigin;
        }

        GitResult any = git.Read(new GitArgs("config").Add("--get-regexp", @"^remote\..*\.url$"));

        return any.Succeeded
            ? any.Output.Split('\n')
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => GitHostLinks.Parse(parts[1].Trim()))
                .FirstOrDefault(host => host is not null)
            : null;
    }

    /// <summary>Gitxen's choice for <c>owner/name</c>, or empty. A missing or unreadable session is simply no choice.</summary>
    public static string ChosenInGitxen(string repository)
    {
        try
        {
            using FileStream stream = File.OpenRead(SessionPath);
            using JsonDocument document = JsonDocument.Parse(stream);

            if (document.RootElement.TryGetProperty("GitHubAccounts", out JsonElement accounts) && accounts.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in accounts.EnumerateObject())
                {
                    if (entry.Name.Equals(repository, StringComparison.OrdinalIgnoreCase) && entry.Value.ValueKind == JsonValueKind.String)
                    {
                        return entry.Value.GetString() ?? "";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No session yet, or the window is writing it: no choice.
        }

        return "";
    }
}
