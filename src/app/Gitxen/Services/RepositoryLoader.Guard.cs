// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>The secret and large-file guard: what <see cref="SecretScanner"/> finds before a commit or a push.</summary>
internal sealed partial class RepositoryLoader
{
    public IReadOnlyList<ScanFinding> ScanStaged() => SecretScanner.ScanStaged(_git);

    /// <summary>
    ///  What pushing <paramref name="branch"/> would send: against its upstream, or — for a branch never
    ///  pushed — against the remote's default branch. Nothing to compare with means nothing is scanned.
    /// </summary>
    public IReadOnlyList<ScanFinding> ScanOutgoing(string branch)
    {
        string from = "";
        GitResult upstream = _git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", $"{branch}@{{upstream}}"));

        if (upstream.Succeeded)
        {
            from = upstream.Output.Trim();
        }
        else
        {
            GitResult remoteHead = _git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", "refs/remotes/origin/HEAD"));
            from = remoteHead.Succeeded ? remoteHead.Output.Trim() : "";
        }

        return from.Length == 0 ? [] : SecretScanner.ScanRange(_git, from, branch);
    }

    /// <summary>
    ///  Takes flagged files out of the commit, and adds the ones that should never be committed — a
    ///  <c>.env</c>, a private key — to <c>.gitignore</c> so they stay out.
    /// </summary>
    public GitOperationResult UnstageAndIgnore(IReadOnlyList<string> paths, IReadOnlyList<string> ignore)
    {
        GitResult unstaged = _git.Write(new GitArgs("reset").Add("--quiet").AddPaths([.. paths]));

        if (!unstaged.Succeeded)
        {
            return new GitOperationResult("Unstage", false, unstaged.AllOutput.Trim());
        }

        if (ignore.Count > 0)
        {
            try
            {
                string gitignore = Path.Combine(_workingDirectory, ".gitignore");
                string existing = File.Exists(gitignore) ? File.ReadAllText(gitignore) : "";
                IEnumerable<string> entries = ignore.Select(IgnoreEntry).Where(entry => !existing.Split('\n').Select(line => line.Trim()).Contains(entry));
                string separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
                File.AppendAllText(gitignore, separator + string.Concat(entries.Select(entry => entry + "\n")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new GitOperationResult("Unstage", true, $"Unstaged {paths.Count} files, but .gitignore could not be written: {ex.Message}");
            }
        }

        return new GitOperationResult(
            "Unstage",
            true,
            $"Took {string.Join(", ", paths)} out of the commit." + (ignore.Count > 0 ? $" Added {string.Join(", ", ignore)} to .gitignore." : ""));
    }

    /// <summary>
    ///  A .gitignore line matching exactly this file: anchored with a leading slash, and with the
    ///  characters .gitignore treats specially escaped.
    /// </summary>
    private static string IgnoreEntry(string path)
    {
        System.Text.StringBuilder entry = new("/");

        foreach (char character in path)
        {
            if (character is '\\' or '*' or '?' or '[' or '!' or '#')
            {
                entry.Append('\\');
            }

            entry.Append(character);
        }

        return entry.ToString();
    }
}
