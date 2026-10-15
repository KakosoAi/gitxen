// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;

namespace Gitxen.Git;

/// <summary>
///  The arguments of one git invocation, as a list rather than a command line.
/// </summary>
/// <remarks>
///  <para>
///   Deliberately a list of separate strings. <see cref="System.Diagnostics.ProcessStartInfo"/>
///   accepts an <c>ArgumentList</c> and does the Windows quoting itself, so nothing here has to
///   reason about quotes, spaces or trailing backslashes — the three things hand-rolled command-line
///   building gets wrong.
///  </para>
///  <para>
///   It also sidesteps a real bug in the inherited engine: composing one command line means Windows
///   splits it on tabs as well as spaces, so a <c>--format</c> containing a literal tab fragments and
///   the command fails. Passing arguments separately, a tab is just a character. Use one anyway only
///   where git's own <c>%x09</c> will not do.
///  </para>
/// </remarks>
public sealed class GitArgs : IEnumerable<string>
{
    private readonly List<string> _values = [];
    private readonly List<string> _config = [];
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    /// <param name="command">The git subcommand, e.g. <c>log</c> or <c>for-each-ref</c>.</param>
    public GitArgs(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _values.Add(command);
    }

    /// <summary>The arguments, in order, including the subcommand itself.</summary>
    public IReadOnlyList<string> Values => _values;

    /// <summary><c>-c key=value</c> pairs, which git reads before the subcommand.</summary>
    public IReadOnlyList<string> Config => _config;

    /// <summary>Environment variables for this one process. Never shown in <see cref="ToString"/>.</summary>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>
    ///  Sets a configuration value for this command only, as <c>git -c key=value</c>. Child processes
    ///  git starts — a submodule's fetch, a credential helper — see it too.
    /// </summary>
    public GitArgs WithConfig(string key, string value)
    {
        _config.Add("-c");
        _config.Add($"{key}={value}");
        return this;
    }

    /// <summary>
    ///  Sets an environment variable for this command only. The place for a secret: unlike an
    ///  argument, it is not in the process's command line or in the activity log.
    /// </summary>
    public GitArgs WithEnvironment(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    public GitArgs Add(string value)
    {
        _values.Add(value);
        return this;
    }

    public GitArgs Add(params ReadOnlySpan<string> values)
    {
        foreach (string value in values)
        {
            _values.Add(value);
        }

        return this;
    }

    /// <summary>Adds <paramref name="values"/> only when <paramref name="condition"/> holds.</summary>
    public GitArgs AddIf(bool condition, params ReadOnlySpan<string> values) =>
        condition ? Add(values) : this;

    /// <summary>
    ///  Adds a value only when it has content, which is what most optional git options want.
    /// </summary>
    public GitArgs AddIfNotEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? this : Add(value);

    /// <summary>
    ///  Ends the options and begins pathspecs, so a path that looks like a revision or an option
    ///  cannot be read as one.
    /// </summary>
    public GitArgs AddPaths(params ReadOnlySpan<string> paths)
    {
        _values.Add("--");

        foreach (string path in paths)
        {
            // git speaks posix separators everywhere, including on Windows.
            _values.Add(path.Replace('\\', '/'));
        }

        return this;
    }

    public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    ///  A readable rendering, for logging and for the activity console.
    /// </summary>
    /// <remarks>
    ///  Not what is executed — the process is started from <see cref="Values"/>. Anything with a
    ///  space is wrapped so the log stays unambiguous to read.
    /// </remarks>
    public override string ToString() =>
        string.Join(' ', _config.Concat(_values).Select(static value =>
            value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value));
}
