// SPDX-License-Identifier: GPL-3.0-or-later
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Windows.Security.Credentials;

namespace Gitxen.Services;

/// <summary>An AI request that could not be answered; the message says why, for the result bar.</summary>
internal sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///  Opt-in writing help from Claude: commit messages, pull request descriptions, and plain-language
///  explanations of a commit. Nothing is sent until the user asks, and only the diff and messages
///  that request needs.
/// </summary>
/// <remarks>
///  <para>
///   The key is the user's own Anthropic API key, kept in Windows Credential Manager
///   (<see cref="PasswordVault"/>) rather than in Gitxen's files; <c>ANTHROPIC_API_KEY</c> in the
///   environment is used when no key is stored.
///  </para>
///  <para>
///   Claude Opus 5.5 at low effort: these are short writing tasks where a quick answer matters more
///   than long deliberation. Server-side fallback is on, so a request the model declines is retried on
///   another model instead of failing; a refusal that remains is reported, never shown as a message.
///  </para>
/// </remarks>
internal static class AiAssistant
{
    public const string ModelId = "claude-opus-5-5";

    /// <summary>Longest diff sent in one request; past this the request says the rest was left out.</summary>
    public const int MaxDiffChars = 300_000;

    private const string VaultResource = "Gitxen";
    private const string VaultUser = "anthropic-api-key";

    /// <summary>Whether a key is available, stored or in the environment.</summary>
    public static bool IsConfigured => ApiKey() is not null;

    /// <summary>Where the key comes from, for Settings.</summary>
    public static string KeySource => StoredKey() is not null
        ? "Stored in Windows Credential Manager."
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 }
            ? "Using ANTHROPIC_API_KEY from the environment."
            : "No key yet.";

    public static void SaveKey(string key)
    {
        ForgetKey();
        new PasswordVault().Add(new PasswordCredential(VaultResource, VaultUser, key.Trim()));
    }

    public static void ForgetKey()
    {
        try
        {
            PasswordVault vault = new();
            vault.Remove(vault.Retrieve(VaultResource, VaultUser));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            // Nothing stored.
        }
    }

    /// <summary>A commit message for <paramref name="stagedDiff"/>, in the style of <paramref name="recentSubjects"/>.</summary>
    public static Task<string> WriteCommitMessageAsync(string stagedDiff, IReadOnlyList<string> recentSubjects, CancellationToken cancellationToken = default) =>
        CompleteAsync(
            """
            You write git commit messages for the diff you are given.
            Reply with the message only: a subject line of at most 72 characters in the imperative mood, then, only if it helps a reviewer, a blank line and a short body wrapped at 72 characters that says why the change was made rather than repeating the diff.
            Follow the conventions of the repository's recent subjects, which you are shown — for example Conventional Commits prefixes and scopes if they use them.
            No code fences, no quotes around the message, no sign-off.
            """,
            $"Recent commit subjects in this repository:\n{string.Join('\n', recentSubjects)}\n\nStaged changes:\n{Clip(stagedDiff)}",
            cancellationToken);

    /// <summary>A pull request title and description for the commits from <paramref name="baseBranch"/> to <paramref name="head"/>.</summary>
    public static async Task<(string Title, string Body)> WritePullRequestAsync(
        string baseBranch,
        string head,
        string commits,
        string diff,
        CancellationToken cancellationToken = default)
    {
        string text = await CompleteAsync(
            """
            You write pull request titles and descriptions.
            Reply with the title on the first line (at most 70 characters, no prefix such as "Title:"), then a blank line, then the description in GitHub Markdown: a short summary paragraph, a "Changes" list of the notable changes, and, only when the commits or diff show it, how the change was tested or what a reviewer should check.
            Be specific to this change and brief. Do not invent issue numbers, links or test results.
            """,
            $"Merging {head} into {baseBranch}.\n\nCommits:\n{commits}\n\nDiff:\n{Clip(diff)}",
            cancellationToken);

        string[] parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', 2);
        return (parts[0].Trim().TrimStart('#', ' '), parts.Length > 1 ? parts[1].Trim() : "");
    }

    /// <summary>What a commit does and why, in plain language.</summary>
    public static Task<string> ExplainCommitAsync(string commitAndPatch, CancellationToken cancellationToken = default) =>
        CompleteAsync(
            """
            You explain a git commit to a developer who did not write it.
            In a few short paragraphs of plain language: what the commit changes, why it was probably made (from its message and the code), and anything a reviewer should look at closely. Name files and functions where it helps. Do not restate the diff line by line.
            """,
            Clip(commitAndPatch),
            cancellationToken);

    private static async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        if (ApiKey() is not string key)
        {
            throw new AiException("No Anthropic API key. Add one in Settings → AI.");
        }

        AnthropicClient client = new() { ApiKey = key };

        try
        {
            BetaMessage response = await client.Beta.Messages.Create(
                new MessageCreateParams
                {
                    Model = ModelId,
                    MaxTokens = 16000,
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                    System = system,
                    Messages = [new() { Role = Role.User, Content = user }],
                },
                cancellationToken);

            if (response.StopReason == "refusal")
            {
                throw new AiException("Claude declined this request.");
            }

            string text = string.Concat(response.Content
                .Select(block => block.TryPickText(out BetaTextBlock? part) ? part.Text : "")).Trim();

            return text.Length > 0 ? text : throw new AiException("Claude returned no text.");
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            throw new AiException($"The request to Anthropic failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Could not reach Anthropic: {ex.Message}", ex);
        }
    }

    /// <summary>The diff, or its first <see cref="MaxDiffChars"/> characters with a note saying the rest was left out.</summary>
    private static string Clip(string text) => text.Length <= MaxDiffChars
        ? text
        : $"{text[..MaxDiffChars]}\n\n[The rest of the diff, {text.Length - MaxDiffChars:N0} more characters, was left out because it is too long.]";

    private static string? ApiKey() => StoredKey() ?? (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } fromEnvironment ? fromEnvironment : null);

    private static string? StoredKey()
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(VaultResource, VaultUser);
            credential.RetrievePassword();
            return credential.Password is { Length: > 0 } password ? password : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
