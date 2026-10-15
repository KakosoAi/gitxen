// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>What the guard found.</summary>
internal enum FindingKind
{
    /// <summary>Text with the exact shape of a real credential: a cloud key, a token, a private key.</summary>
    Secret,

    /// <summary>A password- or key-like assignment. Often real, sometimes a placeholder.</summary>
    PossibleSecret,

    /// <summary>A file that usually holds secrets: <c>.env</c>, a private key, a certificate store.</summary>
    SensitiveFile,

    /// <summary>A file big enough to bloat every clone; GitHub refuses files over 100 MB.</summary>
    LargeFile
}

/// <param name="Line">1-based line in the new file; 0 when the finding is about the whole file.</param>
/// <param name="Excerpt">What matched, with most of it masked, so the guard does not display the secret it found.</param>
internal sealed record ScanFinding(FindingKind Kind, string Path, int Line, string Rule, string Excerpt);

/// <summary>
///  Looks through what is about to be committed or pushed for credentials, files that hold them, and
///  files too big for a repository.
/// </summary>
/// <remarks>
///  <para>
///   Only added lines are read: a secret being removed is the fix, not the problem. The patch is
///   <c>diff --no-prefix --unified=0 -c core.quotePath=false</c>, so paths come through as they are
///   and only added lines need walking. Learned from git: a path containing a space ends with a tab
///   on its <c>+++</c> line, and a hunk header's <c>+n</c> without a count means one line.
///  </para>
///  <para>
///   A line containing <c>gitxen:allow-secret</c> is skipped, for test fixtures and documented example
///   keys.
///  </para>
/// </remarks>
internal static partial class SecretScanner
{
    /// <summary>Files at or above this are flagged; GitHub warns at 50 MB and refuses 100 MB.</summary>
    public const long LargeFileBytes = 50L * 1024 * 1024;

    private const string AllowMarker = "gitxen:allow-secret";

    /// <summary>Longest patch read; past this the rest is not scanned rather than holding it all in memory.</summary>
    private const int MaxPatchChars = 32 * 1024 * 1024;

    private static readonly (string Rule, FindingKind Kind, Regex Pattern)[] Rules =
    [
        ("AWS access key", FindingKind.Secret, new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("GitHub token", FindingKind.Secret, new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b", RegexOptions.CultureInvariant)),
        ("GitLab token", FindingKind.Secret, new Regex(@"\bglpat-[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant)),
        ("Slack token", FindingKind.Secret, new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant)),
        ("Google API key", FindingKind.Secret, new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.CultureInvariant)),
        ("Stripe live key", FindingKind.Secret, new Regex(@"\b(?:sk|rk)_live_[0-9A-Za-z]{20,}\b", RegexOptions.CultureInvariant)),
        ("Anthropic API key", FindingKind.Secret, new Regex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)),
        ("OpenAI API key", FindingKind.Secret, new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{32,}", RegexOptions.CultureInvariant)),
        ("Private key", FindingKind.Secret, new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY( BLOCK)?-----", RegexOptions.CultureInvariant)),
        ("Azure storage key", FindingKind.Secret, new Regex(@"AccountKey=[A-Za-z0-9+/=]{40,}", RegexOptions.CultureInvariant)),
        ("JSON web token", FindingKind.PossibleSecret, new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant)),
        ("Password or key in code", FindingKind.PossibleSecret, new Regex(
            @"(?i)\b(?:password|passwd|pwd|secret|api[_\-]?key|access[_\-]?key|auth[_\-]?token|client[_\-]?secret)\b[""']?\s*[:=]\s*[""'](?<value>[^""'\s]{8,})[""']",
            RegexOptions.CultureInvariant)),
    ];

    /// <summary>What is staged, ready to commit.</summary>
    public static IReadOnlyList<ScanFinding> ScanStaged(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult patch = git.Read(Diff().Add("--cached"));
        GitResult names = git.Read(new GitArgs("-c").Add("core.quotePath=false", "diff", "--cached", "--name-only", "--diff-filter=AMR", "-z"));
        HashSet<string> changed = [.. names.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

        GitResult sizes = git.Read(new GitArgs("ls-files").Add("-z", "--format=%(objectsize)%x09%(path)"));
        IEnumerable<(string Path, long Size)> files = sizes.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Split('\t', 2))
            .Where(parts => parts.Length == 2 && changed.Contains(parts[1]))
            .Select(parts => (parts[1], long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : 0));

        return [.. ScanFiles(files), .. ScanPatch(patch.Succeeded ? patch.Text : "")];
    }

    /// <summary>What a push would send: the commits from <paramref name="from"/> to <paramref name="to"/>, taken together.</summary>
    public static IReadOnlyList<ScanFinding> ScanRange(GitCli git, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(git);

        GitResult patch = git.Read(Diff().Add($"{from}...{to}"));
        GitResult files = git.Read(new GitArgs("-c").Add("core.quotePath=false", "diff", "--name-only", "--diff-filter=AMR", "-z", $"{from}...{to}"));
        List<(string Path, long Size)> sized = [];

        foreach (string path in files.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            GitResult size = git.Read(new GitArgs("cat-file").Add("-s", $"{to}:{path}"));
            sized.Add((path, size.Succeeded && long.TryParse(size.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : 0));
        }

        return [.. ScanFiles(sized), .. ScanPatch(patch.Succeeded ? patch.Text : "")];
    }

    /// <summary>Added lines of a <c>--no-prefix --unified=0</c> patch, checked against every rule.</summary>
    public static IReadOnlyList<ScanFinding> ScanPatch(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        List<ScanFinding> findings = [];
        string path = "";
        int line = 0;
        string text = patch.Length > MaxPatchChars ? patch[..MaxPatchChars] : patch;

        foreach (string raw in text.Split('\n'))
        {
            string current = raw.TrimEnd('\r');

            if (current.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string target = current[4..].TrimEnd('\t');
                path = target == "/dev/null" ? "" : target;
                continue;
            }

            if (current.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Match hunk = HunkHeader().Match(current);
                line = hunk.Success ? int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                continue;
            }

            if (current.Length == 0 || current[0] != '+' || path.Length == 0)
            {
                continue;
            }

            string added = current[1..];

            if (!added.Contains(AllowMarker, StringComparison.Ordinal))
            {
                foreach ((string rule, FindingKind kind, Regex pattern) in Rules)
                {
                    Match match = pattern.Match(added);

                    if (match.Success)
                    {
                        string value = match.Groups["value"].Success ? match.Groups["value"].Value : match.Value;
                        findings.Add(new ScanFinding(kind, path, line, rule, Mask(value)));
                        break;
                    }
                }
            }

            line++;
        }

        return findings;
    }

    /// <summary>Files by name and size alone.</summary>
    public static IReadOnlyList<ScanFinding> ScanFiles(IEnumerable<(string Path, long Size)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<ScanFinding> findings = [];

        foreach ((string path, long size) in files)
        {
            string name = path[(path.LastIndexOf('/') + 1)..];

            if (SensitiveName(name) is string why)
            {
                findings.Add(new ScanFinding(FindingKind.SensitiveFile, path, 0, why, ""));
            }

            if (size >= LargeFileBytes)
            {
                findings.Add(new ScanFinding(FindingKind.LargeFile, path, 0, "Large file", $"{size / (1024.0 * 1024):0.#} MB"));
            }
        }

        return findings;
    }

    private static string? SensitiveName(string name)
    {
        string lower = name.ToLowerInvariant();

        return lower switch
        {
            ".env" => "Environment file",
            _ when lower.StartsWith(".env.", StringComparison.Ordinal) && !lower.EndsWith(".example", StringComparison.Ordinal)
                && !lower.EndsWith(".sample", StringComparison.Ordinal) && !lower.EndsWith(".template", StringComparison.Ordinal) => "Environment file",
            "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" => "SSH private key",
            "credentials.json" or "service-account.json" or ".npmrc" or ".pypirc" or ".netrc" or ".git-credentials" => "Credentials file",
            _ when lower.EndsWith(".pem", StringComparison.Ordinal) || lower.EndsWith(".key", StringComparison.Ordinal) => "Private key or certificate",
            _ when lower.EndsWith(".pfx", StringComparison.Ordinal) || lower.EndsWith(".p12", StringComparison.Ordinal) || lower.EndsWith(".keystore", StringComparison.Ordinal) || lower.EndsWith(".jks", StringComparison.Ordinal) => "Certificate store",
            _ => null
        };
    }

    /// <summary>Enough of the value to recognise it, not enough to use it.</summary>
    private static string Mask(string value) => value.Length <= 8
        ? new string('•', value.Length)
        : $"{value[..4]}{new string('•', Math.Min(12, value.Length - 6))}{value[^2..]}";

    private static GitArgs Diff() => new GitArgs("-c").Add(
        "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--no-prefix", "--unified=0");

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
