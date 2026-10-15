// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Gitxen.Git;

/// <summary>
///  What one git invocation produced.
/// </summary>
/// <remarks>
///  <para>
///   Standard output is kept as <b>bytes</b> as well as text, and that is the important part. Git
///   does not promise its output is UTF-8: a path, an author name or a commit message can carry any
///   bytes the filesystem or the original committer used. Decoding eagerly and throwing the bytes
///   away is how a client ends up corrupting exactly the repositories it cannot test against.
///  </para>
///  <para>
///   So <see cref="Output"/> is the convenient reading for the common case, and
///   <see cref="OutputBytes"/> is there for parsers that must split on NUL or preserve what they
///   were given.
///  </para>
/// </remarks>
public sealed class GitResult
{
    private string? _output;
    private string? _error;
    private string? _text;

    internal GitResult(int exitCode, byte[] outputBytes, byte[] errorBytes)
    {
        ExitCode = exitCode;
        OutputBytes = outputBytes;
        ErrorBytes = errorBytes;
    }

    /// <summary>The process exit code. Zero means git considered the command successful.</summary>
    public int ExitCode { get; }

    /// <summary>
    ///  Whether git succeeded.
    /// </summary>
    /// <remarks>
    ///  A non-zero exit is ordinary information here — nothing to fetch, a rejected push, a dirty
    ///  worktree — not an exceptional condition. Callers report it; they do not throw on it.
    /// </remarks>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Raw standard output, exactly as git wrote it.</summary>
    public byte[] OutputBytes { get; }

    /// <summary>Raw standard error, exactly as git wrote it.</summary>
    public byte[] ErrorBytes { get; }

    /// <summary>Standard output decoded as UTF-8, with the trailing newline removed.</summary>
    public string Output => _output ??= Decode(OutputBytes);

    /// <summary>
    ///  Standard output decoded as UTF-8 and otherwise untouched.
    /// </summary>
    /// <remarks>
    ///  For output whose last byte matters. A patch is the case in point: it goes back to
    ///  <c>git apply</c> for partial staging, and a trimmed final newline turns a valid patch into a
    ///  corrupt one.
    /// </remarks>
    public string Text => _text ??= OutputBytes.Length == 0 ? "" : Encoding.UTF8.GetString(OutputBytes);

    /// <summary>Standard error decoded as UTF-8, with the trailing newline removed.</summary>
    public string Error => _error ??= Decode(ErrorBytes);

    /// <summary>Whichever stream has something to say — output when git succeeded, error when it did not.</summary>
    public string Message => Succeeded || Error.Length == 0 ? Output : Error;

    /// <summary>
    ///  Both streams together, for reporting an operation's outcome to the user.
    /// </summary>
    /// <remarks>
    ///  Git splits its reporting across the two without much consistency — a push writes its
    ///  progress to standard error and succeeds — so anything shown verbatim to a person wants both.
    /// </remarks>
    public string AllOutput =>
        Output.Length == 0 ? Error
        : Error.Length == 0 ? Output
        : $"{Output}\n{Error}";

    /// <summary>
    ///  Splits standard output on NUL, which is what git's <c>-z</c> produces.
    /// </summary>
    /// <remarks>
    ///  Worth using wherever git offers it: a NUL-separated listing needs no unquoting and cannot be
    ///  confused by a path containing a newline, a quote or a space.
    /// </remarks>
    public IEnumerable<string> SplitNul() =>
        Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Standard output as lines, with blank ones dropped.</summary>
    public IEnumerable<string> Lines() =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Decode(byte[] bytes) =>
        bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes).TrimEnd('\n', '\r');
}
